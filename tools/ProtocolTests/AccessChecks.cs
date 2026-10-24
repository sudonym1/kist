using System;
using BepInEx.Logging;
using SharedInventory;
using UnityEngine;

internal static class AccessChecks
{
    public static void Run()
    {
        void Check(bool condition, string name) { if (!condition) throw new Exception("Access: " + name); }
        const string prefix = "SharedInventory.Access.v1.";
        var log = new ManualLogSource();
        ZNet.instance = new ZNet(); ZDOMan.instance = new ZDOMan { Complete = false };
        ChestAccess.WorldStarted = true; Time.unscaledTime = 0;
        var first = new ZDO { m_uid = new ZDOID(1, 10), Prefab = ContentIds.Chest.GetStableHashCode(), Position = new Vector3(0, 0, 0) };
        ZDOMan.instance.Records[first.m_uid] = first;
        var serverRouter = new ZRoutedRpc(); var clientRouter = new ZRoutedRpc();
        var server = new ChestAccess(serverRouter, log, 100);
        ZNet.instance.Server = false;
        var client = new ChestAccess(clientRouter, log, 9999);
        int packets = 0;
        serverRouter.Transport = (target, method, packet) => { packets++; if (target == 0 || target == 2) clientRouter.Inject(1, method, packet); };
        clientRouter.Transport = (target, method, packet) => { packets++; serverRouter.Inject(2, method, packet); };
        client.Update(); server.Update();
        Check(!server.Ready && !client.Ready && !client.Covers(new Vector3()), "no access during initial scan");
        ZDOMan.instance.Complete = true; server.Update();
        Check(server.Ready && client.Ready && client.Radius == 100 && client.Count == 1, "queued initial snapshot and server radius");
        Check(client.Covers(new Vector3(0, 100, 0)) && !client.Covers(new Vector3(0, 100.001f, 0)), "snapshot 3D boundary");
        var scans = ZDOMan.instance.Scans; var messages = packets;
        for (var i = 0; i < 1000; i++) { server.Update(); client.Update(); }
        Check(packets == messages && ZDOMan.instance.Scans == scans, "idle has no scans or network traffic");
        client.Configure(9999); Check(client.Radius == 100, "client cannot override radius");
        server.Configure(20); server.Update(); Check(client.Radius == 20, "live server configuration sync");
        server.Configure(float.NaN); server.Update(); Check(client.Radius == 100 && log.Warnings.Count == 1, "invalid server configuration falls back safely");
        var second = new ZDO { m_uid = new ZDOID(1, 11), Prefab = ContentIds.Chest.GetStableHashCode(), Position = new Vector3(200, 0, 0) };
        ZDOMan.instance.Records[second.m_uid] = second; server.Changed(second); server.Update();
        Check(client.Count == 2 && client.Covers(second.Position), "placement delta");
        second.Position = new Vector3(500, 0, 0); server.Changed(second); server.Update();
        Check(!client.Covers(new Vector3(200, 0, 0)) && client.Covers(second.Position), "position delta");
        var stablePackets = packets; server.Changed(second); server.Update();
        Check(packets == stablePackets, "unchanged record emits no delta");
        ZDOMan.instance.Records.Remove(first.m_uid); server.Destroyed(first.m_uid);
        Check(!server.Covers(new Vector3()), "next action rechecks deletion before plugin update");
        server.Update();
        Check(client.Count == 1 && !client.Covers(new Vector3()), "actual deletion revokes only destroyed coverage");

        ZPackage Header(long version, float radius = 100)
        { var p = new ZPackage(); p.Write(1L); p.Write(version); p.Write(radius); return p; }
        var forged = Header(99); forged.Write(0);
        clientRouter.Inject(3, prefix + "Snapshot", forged);
        Check(client.Count == 1, "nonserver cannot replace registry");
        var stale = Header(1); stale.Write(0);
        clientRouter.Inject(1, prefix + "Snapshot", stale);
        Check(client.Count == 1, "stale snapshot ignored");
        var gap = Header(99); gap.Write(0);
        clientRouter.Inject(1, prefix + "Changes", gap);
        Check(!client.Ready && !client.Covers(second.Position), "missing delta fails closed");
        client.Update(); Check(client.Ready && client.Count == 1, "gap requests complete snapshot");
        var truncated = Header(100); truncated.Write(1); truncated.Write(second.m_uid);
        clientRouter.Inject(1, prefix + "Snapshot", truncated);
        Check(!client.Ready, "truncated snapshot fails closed atomically");
        client.Update(); Check(client.Ready && client.Count == 1, "malformed snapshot recovers");

        client.Stop(); ZNet.instance.Server = false;
        var reconnectRouter = new ZRoutedRpc();
        var reconnect = new ChestAccess(reconnectRouter, log, 1);
        reconnectRouter.Transport = (target, method, packet) => serverRouter.Inject(2, method, packet);
        serverRouter.Transport = (target, method, packet) => reconnectRouter.Inject(1, method, packet);
        Check(!reconnect.Ready, "reconnect does not reuse cached coverage");
        reconnect.Update(); Check(reconnect.Ready && reconnect.Count == 1 && reconnect.Radius == 100, "reconnect snapshot includes unloaded positions");
        ZDOMan.instance.Records.Remove(second.m_uid); server.Destroyed(second.m_uid); server.Update();
        Check(reconnect.Count == 0 && !reconnect.Covers(second.Position), "last chest destruction removes access");
        reconnect.Stop(); server.Stop();
        Check(!reconnect.Ready && !server.Ready && ChestAccess.Current == null, "session teardown revokes coverage");
        ZNet.instance = new ZNet(); ChestAccess.WorldStarted = false;
        System.Console.WriteLine("PASS access registry snapshots/deltas, server-only radius, malformed/gapped/stale packet recovery, reconnect, destruction, and zero idle traffic");
    }
}
