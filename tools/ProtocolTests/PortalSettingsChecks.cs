using System;
using System.Collections.Generic;
using SharedInventory;

internal static class PortalSettingsChecks
{
    private const string Prefix = "SharedInventory.PortalSettings.v1.";
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static ZPackage State(long world, long revision, bool allowed)
    {
        var p = new ZPackage(); p.Write(world); p.Write(revision); p.Write(allowed); return p;
    }
    public static void Run()
    {
        var original = ZNet.instance;
        try
        {
            var server = new ZNet();
            server.Peers.Add(new ZNet.Peer { m_uid = 2 });
            var client = new ZNet { Server = false };
            var serverRouter = new ZRoutedRpc(); var clientRouter = new ZRoutedRpc();
            var sent = new List<(long Peer, string Method, ZPackage Package)>();
            serverRouter.Sent = (peer, method, p) => sent.Add((peer, method, p));
            var requests = new List<ZPackage>();
            clientRouter.Sent = (peer, method, p) =>
            {
                Check(peer == 1 && method == Prefix + "Request", "Request must target the server");
                requests.Add(p);
            };
            bool configured = false, hostAllowed = true, clientAllowed = true;
            ZNet.instance = server;
            var host = new PortalSettings(serverRouter, () => configured, value => hostAllowed = value);
            ZNet.instance = client;
            var remote = new PortalSettings(clientRouter, () => true, value => clientAllowed = value);
            Check(!hostAllowed && !clientAllowed, "Default blocks essence, ignoring a remote client's enabled local setting");
            client.ServerPeer = null; remote.Update(0);
            Check(requests.Count == 0 && !clientAllowed, "No portal permission is granted before the server handshake");
            client.ServerPeer = new ZNet.Peer();
            remote.Update(0); remote.Update(1); remote.Update(2);
            Check(requests.Count == 2, "Client retries an unanswered initial request every two seconds");
            serverRouter.Inject(99, Prefix + "Request", new ZPackage(requests[0].GetArray()));
            Check(sent.Count == 0, "Unknown peers cannot request server settings");
            serverRouter.Inject(2, Prefix + "Request", new ZPackage(requests[0].GetArray()));
            Check(sent.Count == 1 && sent[0].Peer == 2, "Joining client receives the server setting");
            clientRouter.Inject(1, Prefix + "State", sent[0].Package);
            remote.Update(4); Check(requests.Count == 2 && !clientAllowed, "Initial synchronization completes and stops polling");
            configured = true; host.Update(5);
            Check(hostAllowed && sent.Count == 2, "Host changes are applied locally and broadcast");
            clientRouter.Inject(1, Prefix + "State", sent[1].Package);
            Check(clientAllowed, "Host can enable essence portal travel on remote clients");
            clientRouter.Inject(1, Prefix + "State", sent[0].Package);
            clientRouter.Inject(3, Prefix + "State", State(1, 99, false));
            clientRouter.Inject(1, Prefix + "State", State(9, 99, false));
            Check(clientAllowed, "Stale, foreign-peer and foreign-world packets cannot change policy");
            configured = false; host.Update(6);
            clientRouter.Inject(1, Prefix + "State", sent[2].Package);
            Check(!hostAllowed && !clientAllowed, "Host can restore the restriction for every client");
            configured = true; host.Update(7);
            clientRouter.Inject(1, Prefix + "State", sent[3].Package);
            remote.Stop(); clientRouter.Inject(1, Prefix + "State", State(1, 100, true));
            Check(!clientAllowed, "Disconnect resets policy and ignores delayed packets");
            var joinedRouter = new ZRoutedRpc();
            joinedRouter.Sent = (peer, method, p) => serverRouter.Inject(2, method, p);
            var joined = new PortalSettings(joinedRouter, () => false, value => clientAllowed = value);
            joined.Update(8);
            joinedRouter.Inject(1, Prefix + "State", sent[4].Package);
            Check(clientAllowed, "A newly joined client receives an already enabled server setting");
            joined.Stop(); host.Stop();
            System.Console.WriteLine("PASS essence portal setting defaults, server authority, initial sync, retries, updates, late join, stale packets and disconnect reset");
        }
        finally { ZNet.instance = original; }
    }
}
