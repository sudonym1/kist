using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace SharedInventory;

// Main-thread ritual endpoint. No inventory worker, stock RPC, or save format is
// involved. Only active validation/countdowns send presence probes (4 Hz).
internal sealed partial class Ritual
{
    private const string Prefix = "SharedInventory.Ritual.v1.";
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    private readonly ZRoutedRpc router;
    private readonly ManualLogSource log;
    private readonly bool server;
    private readonly long world;
    private readonly RitualGathering gathering = new RitualGathering();
    private readonly Dictionary<long, Confirmation> confirmations = new Dictionary<long, Confirmation>();
    private readonly Dictionary<(long Peer, bool Begin), double> nextRequest = new Dictionary<(long, bool), double>();
    private readonly string session = Guid.NewGuid().ToString("N");
    private string probe = "";
    private double probeSent, nextProbe, localNextRequest, localNextInspect;
    private double lastClientProbe = double.NegativeInfinity;
    private long initiator;
    private ZDOID stone;
    private bool pending, stopped;
    private string pendingId = "";
    private string AttemptId => pending ? pendingId : gathering.Id;
    private double pendingAt;
    private long revision;
    private readonly RitualCountdown display = new RitualCountdown();
    private readonly Dictionary<string, double> probes = new Dictionary<string, double>();
    public string Presence { get; private set; } = "";
    internal bool Active => pending || gathering.Active || outcomes?.Active == true;
    internal RitualGathering Gathering => gathering;
    internal int CountdownNumber => display.Number(Now);
    internal event Action<string, IReadOnlyList<RitualMember>>? Committed;

    private sealed class Confirmation
    {
        public ZDOID Character;
        public string Error = "";
        public Vector3 Position;
        public double At;
        public double ClientTime;
        public string OwnerLife = "";
    }

    public Ritual(ZRoutedRpc router, ManualLogSource log, Func<double> minutes)
    {
        this.router = router; this.log = log;
        server = ZNet.instance.IsServer(); world = ZNet.instance.GetWorldUID();
        router.Register<ZPackage>(Prefix + "Request", ReceiveRequest);
        router.Register<ZPackage>(Prefix + "Probe", ReceiveProbe);
        router.Register<ZPackage>(Prefix + "Presence", ReceivePresence);
        router.Register<ZPackage>(Prefix + "Display", ReceiveDisplay);
        InitializeOutcomes(minutes);
    }

    private long ServerId => server ? ZNet.GetUID() : ZNet.instance.GetServerPeer()?.m_uid ?? 0;
    private bool FromServer(long sender) => !stopped && sender == ServerId && ServerId != 0;
    private ZPackage Package() { var p = new ZPackage(); p.Write(world); return p; }
    private bool Accept(ZPackage p) => !stopped && p.ReadLong() == world;
    private void Send(long peer, string method, ZPackage p)
    {
        if (!stopped && peer != 0) router.InvokeRoutedRPC(peer, Prefix + method, p);
    }

    public void Begin(BossStone target) => Request(target, true);
    public void Inspect(BossStone target) => Request(target, false);
    private void Request(BossStone target, bool begin)
    {
        if (stopped || Now < (begin ? localNextRequest : localNextInspect) || !RitualSite.IsOriginal(target)) return;
        if (begin) localNextRequest = Now + .75; else localNextInspect = Now + 1;
        var p = Package(); p.Write(begin); p.Write(target.GetComponent<ZNetView>().GetZDO().m_uid);
        Send(ServerId, "Request", p);
    }

    private void ReceiveRequest(long sender, ZPackage p)
    {
        if (!server || !Accept(p)) return;
        var begin = p.ReadBool(); var target = p.ReadZDOID();
        var roster = Roster(false);
        if (!roster.Any(m => m.Peer == sender)) return;
        var rateKey = (sender, begin);
        if (nextRequest.TryGetValue(rateKey, out var due) && Now < due) return;
        nextRequest[rateKey] = Now + .5;
        if (!TryStone(target, out _, out var targetStone))
        { Display(sender, "", "The ritual requires an original sacrificial stone."); return; }
        var character = Character(sender);
        if (character == null || (character.GetPosition() - targetStone.transform.position).sqrMagnitude > 25)
        { Display(sender, "", "Move closer to the sacrificial stone."); return; }
        if (!begin)
        {
            Display(sender, "", "", PresenceText(roster));
            return;
        }
        if (Active) { Display(sender, "", "A ritual is already in progress."); return; }
        if (outcomes?.Ready != true || !outcomes.ValidInterval())
        { Display(sender, "", "The ritual is unavailable. Ask the server administrator to check its state."); return; }
        var error = RitualGathering.Missing(roster);
        if (error.Length != 0) { Display(sender, "", error, PresenceText(roster)); return; }
        initiator = sender; stone = target; pendingAt = Now; pending = true;
        pendingId = Guid.NewGuid().ToString("N");
        // Capture membership now, not after the confirmations arrive.
        pendingRoster = roster.Select(m => m.Copy()).ToArray();
        confirmations.Clear(); probes.Clear();
        nextProbe = 0;
        Probe();
    }
    private IReadOnlyList<RitualMember> pendingRoster = Array.Empty<RitualMember>();

    private static string PresenceText(IReadOnlyList<RitualMember> roster) =>
        "Connected: " + roster.Count(m => m.Unavailable.Length == 0) + "/" + roster.Count + " inside" +
        (roster.Any(m => m.Unavailable.Length != 0) ? "\nMissing: " + string.Join(", ", roster.Where(m => m.Unavailable.Length != 0)
            .Select(m => m.Name + " (" + m.Unavailable + ")")) : "\nPresent: " + string.Join(", ", roster.Select(m => m.Name)));

    private bool TryStone(ZDOID id, out RitualSite site, out BossStone target)
    {
        target = null!;
        if (!RitualSite.TryGet(out site)) return false;
        var instance = ZNetScene.instance.FindInstance(id);
        if (instance == null) return false;
        target = instance.GetComponent<BossStone>();
        return site.IsStone(target);
    }

    private ZDO? Character(long peer)
    {
        var id = peer == ZNet.GetUID() ? ZNet.instance.LocalPlayerCharacterID :
            ZNet.instance.GetPeers().FirstOrDefault(p => p.m_uid == peer)?.m_characterID ?? ZDOID.None;
        return id.IsNone() ? null : ZDOMan.instance.GetZDO(id);
    }

    internal List<RitualMember> Roster(bool requireConfirmation)
    {
        var roster = new List<RitualMember>();
        // A graphical host is a required player even while dead/loading. The
        // headless server process has no participant. Map publicity is ignored.
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            roster.Add(new RitualMember { Peer = ZNet.GetUID(), Name = Game.instance.GetPlayerProfile().GetName() });
        foreach (var peer in ZNet.instance.GetPeers())
        {
            if (peer.m_server) continue;
            roster.Add(new RitualMember { Peer = peer.m_uid, Name = string.IsNullOrEmpty(peer.m_playerName) ? "Connecting player" : peer.m_playerName,
                Unavailable = peer.IsReady() ? "" : "connecting" });
        }
        RitualSite.TryGet(out var site);
        foreach (var member in roster)
        {
            if (member.Unavailable.Length != 0) continue;
            var zdo = Character(member.Peer);
            member.Life = zdo == null ? "" : zdo.m_uid.ToString();
            if (zdo == null || zdo.GetLong(ZDOVars.s_playerID) == 0) member.Unavailable = "loading/respawning";
            // Character.Awake omits the health field at full health. Match the
            // game's max-health fallback instead of treating omission as death.
            else if (zdo.GetBool(ZDOVars.s_dead) ||
                zdo.GetFloat(ZDOVars.s_health, zdo.GetFloat(ZDOVars.s_maxHealth, 1)) <= 0) member.Unavailable = "dead/unavailable";
            else if (zdo.GetOwner() != member.Peer) member.Unavailable = "character unavailable";
            else if (site == null || !site.Contains(zdo.GetPosition())) member.Unavailable = "outside the circle";
            if (!requireConfirmation || member.Unavailable.Length != 0) continue;
            if (!confirmations.TryGetValue(member.Peer, out var confirmation) || Now - confirmation.At > 1.25 || confirmation.Character != zdo!.m_uid)
                member.Unavailable = "confirmation unavailable";
            else if (confirmation.Error.Length != 0) member.Unavailable = confirmation.Error;
            else if (!site!.Contains(confirmation.Position)) member.Unavailable = "outside the circle";
            else member.OwnerLife = confirmation.OwnerLife;
        }
        return roster;
    }

    private void Probe()
    {
        probe = Guid.NewGuid().ToString("N"); probeSent = Now; nextProbe = Now + .25;
        probes[probe] = probeSent;
        foreach (var old in probes.Where(p => Now - p.Value > 2).Select(p => p.Key).ToArray()) probes.Remove(old);
        foreach (var member in Roster(false))
        {
            var p = Package(); p.Write(session); p.Write(AttemptId); p.Write(probe); p.Write(probeSent);
            Send(member.Peer, "Probe", p);
        }
    }
    private void ReceiveProbe(long sender, ZPackage p)
    {
        if (!FromServer(sender) || !Accept(p)) return;
        var token = p.ReadString(); var attempt = p.ReadString(); var nonce = p.ReadString(); var sent = p.ReadDouble();
        if ((clientSession.Length != 0 && clientSession != token) || double.IsNaN(sent) || double.IsInfinity(sent) || sent <= lastClientProbe) return;
        lastClientProbe = sent;
        var player = Player.m_localPlayer;
        var error = player == null ? "loading/respawning" : player.IsDead() ? "dead" :
            player.InIntro() || player.IsTeleporting() || player.GetPlayerID() == 0 ? "loading/respawning" :
            !RitualLightning.Ready ? "ritual lightning unavailable" : "";
        var received = Now;
        clientSession = token; clientAttempt = attempt; clientProbedAt = received;
        var ownerLife = error.Length == 0 ? deathGuard.Probe(player!.GetZDOID().ToString(), token, attempt, received) : "";
        var reply = Package(); reply.Write(token); reply.Write(attempt); reply.Write(nonce); reply.Write(sent); reply.Write(received);
        reply.Write(player == null ? ZDOID.None : player.GetZDOID()); reply.Write(error);
        reply.Write(player == null ? Vector3.zero : player.transform.position);
        reply.Write(ownerLife);
        Send(sender, "Presence", reply);
    }
    private void ReceivePresence(long sender, ZPackage p)
    {
        if (!server || !Accept(p) || !Active) return;
        if (p.ReadString() != session) return;
        if (p.ReadString() != AttemptId) return;
        var nonce = p.ReadString(); var sent = p.ReadDouble(); var clientTime = p.ReadDouble();
        if (double.IsNaN(clientTime) || double.IsInfinity(clientTime)) return;
        if (!probes.TryGetValue(nonce, out var issued) || issued != sent || Now - sent > 1.25) return;
        var character = p.ReadZDOID(); var error = p.ReadString(); var position = p.ReadVector3();
        if (!Finite(position)) return;
        var ownerLife = p.ReadString();
        if (error.Length == 0 && ownerLife.Length == 0) return;
        if (!Roster(false).Any(m => m.Peer == sender) || Character(sender)?.m_uid != character) return;
        if (confirmations.TryGetValue(sender, out var old) && old.At >= sent) return;
        confirmations[sender] = new Confirmation { At = sent, Character = character, Error = error, Position = position,
            OwnerLife = ownerLife, ClientTime = clientTime };
        // Echo client monotonic time to estimate clock offset using half RTT.
        Display(sender, AttemptId, "", "", clientTime, gathering.State == RitualGathering.Stage.Countdown);
    }

    public void Update()
    {
        storage?.Pump();
        if (stopped) return;
        if (Player.m_localPlayer == null || Player.m_localPlayer.IsDead()) deathGuard.Invalidate();
        if (server && gathering.State == RitualGathering.Stage.Committed)
        {
            ConfirmDeaths();
            outcomes?.Update();
            if (outcomes?.Active == false) { gathering.Reset(); confirmations.Clear(); probes.Clear(); deathReports.Clear(); }
            return;
        }
        if (stopped || !server || !Active) return;
        if (Now >= nextProbe) Probe();
        var roster = Roster(true);
        if (!TryStone(stone, out _, out _)) { Cancel(); return; }
        if (pending)
        {
            if (roster.Count != pendingRoster.Count || pendingRoster.Any(m => !roster.Any(p => p.Peer == m.Peer && p.Life == m.Life)))
            { Cancel(); return; }
            var error = RitualGathering.Missing(roster);
            if (error.Length != 0)
            {
                if (Now - pendingAt < 1.5 && roster.All(m => m.Unavailable.Length == 0 || m.Unavailable == "confirmation unavailable")) return;
                Cancel(); return;
            }
            if (!gathering.Start(Now, roster, out error, pendingId)) { Cancel(); return; }
            pending = false;
            Broadcast("", true);
            return;
        }
        if (gathering.Advance(Now, roster, out var cancellation))
        {
            Broadcast("", false);
            if (outcomes?.Commit(gathering.Id, gathering.Members) != true)
            { Cancel(); return; }
            log.LogInfo("Einherjar ritual committed: " + gathering.Id + " (" + gathering.Members.Count + " participants)");
            Committed?.Invoke(gathering.Id, gathering.Members);
        }
        else if (cancellation.Length != 0) Cancel();
    }

    private void Cancel()
    {
        if (pending) Display(initiator, "", RitualGathering.Cancelled);
        else Broadcast(RitualGathering.Cancelled, false);
        pending = false; gathering.Reset(); confirmations.Clear(); probes.Clear();
    }

    private void Broadcast(string message, bool countdown)
    {
        foreach (var member in gathering.Members) Display(member.Peer, gathering.Id, message, "", 0, countdown);
    }

    private void Display(long peer, string attempt, string message, string presence = "", double echo = 0, bool countdown = false)
    {
        var p = Package(); p.Write(session); p.Write(++revision); p.Write(attempt); p.Write(message); p.Write(presence);
        p.Write(Now); p.Write(echo); p.Write(countdown); p.Write(gathering.StartedAt);
        Send(peer, "Display", p);
    }
    private void ReceiveDisplay(long sender, ZPackage p)
    {
        if (!FromServer(sender) || !Accept(p)) return;
        var token = p.ReadString(); var serial = p.ReadLong();
        var attempt = p.ReadString(); var message = p.ReadString(); var presence = p.ReadString();
        var serverTime = p.ReadDouble(); var echo = p.ReadDouble(); var countdown = p.ReadBool(); var start = p.ReadDouble();
        if (!display.Apply(Now, token, serial, attempt, serverTime, echo, countdown, start)) return;
        if (presence.Length != 0) Presence = presence;
        if (message.Length != 0) Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message);
    }

    public void Draw()
    {
        if (stopped) return;
        var number = CountdownNumber;
        if (number == 0) return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 58, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = new Color(1, .79f, .32f);
        GUI.Label(new Rect(Screen.width / 2f - 200, Screen.height * .22f, 400, 90), number + "...", style);
    }
    public void Stop()
    {
        outcomes?.Stop(); storage?.Stop(); deathGuard.Invalidate(); deathReports.Clear(); strikes.Clear();
        stopped = true; pending = false; gathering.Reset(); confirmations.Clear(); probes.Clear(); nextRequest.Clear();
        display.Clear(); Presence = "";
    }
}
