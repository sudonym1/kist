using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

// All game state and routing stay on the main thread. Registry lifetime is one
// world/router session. A GameObject's OnDestroy is deliberately not observed.
internal sealed class ChestAccess
{
    private const string Prefix = "SharedInventory.Access.v1.";
    internal static ChestAccess? Current { get; private set; }
    internal static bool WorldStarted;
    internal static bool CanAccess => Current?.CanAccessNow == true;
    internal static string Unavailable => Current?.coverage.Ready == true
        ? "A nearby Einherjar Chest is required for shared access."
        : "Waiting for the server's Einherjar Chest registry.";
    private static readonly int chestHash = ContentIds.Chest.GetStableHashCode();
    private readonly ZRoutedRpc router;
    private readonly ManualLogSource log;
    private readonly bool server;
    private readonly ChestCoverage<ZDOID> coverage = new ChestCoverage<ZDOID>();
    private readonly HashSet<ZDOID> dirty = new HashSet<ZDOID>();
    private readonly HashSet<ZDOID> pendingChanges = new HashSet<ZDOID>();
    private readonly HashSet<long> waiting = new HashSet<long>();
    private readonly List<ZDO> scan = new List<ZDO>();
    private int scanIndex;
    private bool stopped, configDirty;
    private long revision, world;
    private float nextRequest;
    internal float Radius => coverage.Radius;
    internal bool Ready => coverage.Ready;
    internal int Count => coverage.Positions.Count;
    internal bool CanAccessNow
    {
        get
        {
            var player = Player.m_localPlayer;
            return !stopped && player != null && !player.IsDead() && Covers(player.transform.position);
        }
    }
    internal bool Covers(Vector3 position)
    {
        // A player action may run before this plugin's next Update. Reconcile
        // observed record changes now, retaining the pending network delta.
        if (server && coverage.Ready && dirty.Count != 0) ApplyDirty();
        return coverage.Covers(Point(position));
    }
    private static ChestPoint Point(Vector3 p) => new ChestPoint(p.x, p.y, p.z);
    private long ServerId => server ? ZNet.GetUID() : ZNet.instance.GetServerPeer()?.m_uid ?? 0;

    internal ChestAccess(ZRoutedRpc router, ManualLogSource log, float radius)
    {
        this.router = router; this.log = log;
        server = ZNet.instance.IsServer();
        Current = this;
        if (server) Configure(radius);
        router.Register<ZPackage>(Prefix + "Request", (sender, _) =>
        {
            if (stopped || !server || !KnownPeer(sender)) return;
            if (coverage.Ready) SendSnapshot(sender);
            else waiting.Add(sender);
        });
        router.Register<ZPackage>(Prefix + "Snapshot", (sender, packet) => Receive(sender, packet, true));
        router.Register<ZPackage>(Prefix + "Changes", (sender, packet) => Receive(sender, packet, false));
    }

    private bool KnownPeer(long sender) => sender == ZNet.GetUID() || ZNet.instance.GetPeer(sender) != null;

    internal void Configure(float radius)
    {
        if (!server) return; // Client configuration never overrides server policy.
        if (!ChestPoint.ValidRadius(radius))
        {
            log.LogWarning("SharedChestRadiusMeters must be finite and positive; using 100 metres.");
            radius = 100;
        }
        if (coverage.Radius == radius) return;
        coverage.SetRadius(radius); configDirty = true;
    }

    internal void Update()
    {
        if (stopped || !WorldStarted || ZDOMan.instance == null) return;
        if (!server)
        {
            if (!coverage.Ready && Time.unscaledTime >= nextRequest && ServerId != 0)
            {
                nextRequest = Time.unscaledTime + 5;
                router.InvokeRoutedRPC(ServerId, Prefix + "Request", new ZPackage());
            }
            return;
        }
        if (!coverage.Ready)
        {
            // Valheim's iterative sector scan includes network objects without
            // scenes. Bound initialization to one batch per frame, once only.
            var done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(ContentIds.Chest, scan, ref scanIndex);
            foreach (var zdo in scan)
                if (zdo.IsValid()) dirty.Add(zdo.m_uid);
            scan.Clear();
            ApplyDirty();
            if (!done) return;
            world = ZNet.instance.GetWorldUID();
            coverage.Ready = true;
            configDirty = false;
            pendingChanges.Clear();
            revision = 1;
            foreach (var peer in waiting) if (KnownPeer(peer)) SendSnapshot(peer);
            waiting.Clear();
            return;
        }
        if (dirty.Count == 0 && pendingChanges.Count == 0 && !configDirty) return;
        ApplyDirty();
        if (pendingChanges.Count == 0 && !configDirty) return;
        var changes = new List<ZDOID>(pendingChanges);
        pendingChanges.Clear();
        revision++;
        var packet = Header();
        packet.Write(changes.Count);
        foreach (var id in changes)
        {
            packet.Write(id);
            var exists = coverage.Positions.TryGetValue(id, out var p);
            packet.Write(exists);
            if (exists) WritePoint(packet, p);
        }
        configDirty = false;
        router.InvokeRoutedRPC(ZRoutedRpc.Everybody, Prefix + "Changes", packet);
    }

    private void ApplyDirty()
    {
        foreach (var id in dirty)
        {
            var zdo = ZDOMan.instance.GetZDO(id);
            var changed = zdo != null && zdo.IsValid() && zdo.GetPrefab() == chestHash
                ? coverage.Upsert(id, Point(zdo.GetPosition())) : coverage.Remove(id);
            if (changed) pendingChanges.Add(id);
        }
        dirty.Clear();
    }

    private ZPackage Header()
    {
        var packet = new ZPackage();
        packet.Write(world); packet.Write(revision); packet.Write(coverage.Radius);
        return packet;
    }
    private static void WritePoint(ZPackage packet, ChestPoint p) { packet.Write(p.X); packet.Write(p.Y); packet.Write(p.Z); }
    private void SendSnapshot(long peer)
    {
        ApplyDirty();
        var packet = Header();
        packet.Write(coverage.Positions.Count);
        foreach (var pair in coverage.Positions) { packet.Write(pair.Key); WritePoint(packet, pair.Value); }
        router.InvokeRoutedRPC(peer, Prefix + "Snapshot", packet);
    }

    private void Receive(long sender, ZPackage packet, bool snapshot)
    {
        if (stopped || server || sender != ServerId || sender == 0) return;
        try
        {
            var incomingWorld = packet.ReadLong();
            var incomingRevision = packet.ReadLong();
            var radius = packet.ReadSingle();
            if (!ChestPoint.ValidRadius(radius)) throw new InvalidOperationException("Invalid chest radius.");
            if (snapshot)
            {
                if (coverage.Ready && incomingWorld == world && incomingRevision < revision) return;
            }
            else
            {
                if (!coverage.Ready) return;
                if (incomingWorld != world || incomingRevision > revision + 1)
                { coverage.Ready = false; nextRequest = 0; return; }
                if (incomingRevision <= revision) return;
            }
            var count = packet.ReadInt();
            if (count < 0 || count > packet.Size() / 12) throw new InvalidOperationException("Invalid chest registry size.");
            // Decode completely before publishing: malformed packets cannot
            // leave a partially granted registry or partially applied changes.
            var updates = new Dictionary<ZDOID, ChestPoint>();
            var removals = new List<ZDOID>();
            for (var i = 0; i < count; i++)
            {
                var id = packet.ReadZDOID();
                if (!snapshot && !packet.ReadBool()) { removals.Add(id); continue; }
                var point = new ChestPoint(packet.ReadSingle(), packet.ReadSingle(), packet.ReadSingle());
                if (id.IsNone() || !point.Valid) throw new InvalidOperationException("Invalid chest position.");
                updates[id] = point;
            }
            if (snapshot) coverage.Positions.Clear();
            foreach (var id in removals) coverage.Remove(id);
            foreach (var pair in updates) coverage.Upsert(pair.Key, pair.Value);
            coverage.SetRadius(radius);
            world = incomingWorld; revision = incomingRevision; coverage.Ready = true;
        }
        catch (Exception error)
        {
            coverage.Ready = false; nextRequest = 0;
            log.LogWarning("Chest registry rejected; requesting a new snapshot: " + error.Message);
        }
    }

    internal void Stop()
    {
        stopped = true;
        coverage.Ready = false;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    internal void Changed(ZDO zdo)
    {
        if (!server || stopped) return;
        if (zdo.GetPrefab() == chestHash || coverage.Positions.ContainsKey(zdo.m_uid)) dirty.Add(zdo.m_uid);
    }

    internal void Destroyed(ZDOID id)
    {
        if (server && !stopped && coverage.Positions.ContainsKey(id)) dirty.Add(id);
    }

    [HarmonyPatch(typeof(ZNet), "Start")]
    private static class WorldLoaded { private static void Postfix() => WorldStarted = true; }

    [HarmonyPatch]
    private static class ChestChanged
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ZDO), "SetPrefab");
            yield return AccessTools.Method(typeof(ZDO), "Deserialize");
            yield return AccessTools.Method(typeof(ZDO), "InternalSetPosition");
        }
        private static void Postfix(ZDO __instance) => Current?.Changed(__instance);
    }

    [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
    private static class ChestDestroyed
    {
        private static void Postfix(ZDOID uid)
        {
            Current?.Destroyed(uid);
        }
    }
}
