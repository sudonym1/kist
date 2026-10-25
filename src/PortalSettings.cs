using System;

namespace SharedInventory;

// The host/server owns portal policy. Clients start blocked and request the
// current value after attaching; local configuration never overrides it.
internal sealed class PortalSettings
{
    private const string Prefix = "SharedInventory.PortalSettings.v1.";
    private readonly ZRoutedRpc router;
    private readonly ZNet network;
    private readonly bool server;
    private readonly long world;
    private readonly Func<bool> configured;
    private readonly Action<bool> apply;
    private bool allowed, stopped;
    private long revision;
    private double nextRequest;

    public PortalSettings(ZRoutedRpc router, Func<bool> configured, Action<bool> apply)
    {
        this.router = router; this.configured = configured; this.apply = apply;
        network = ZNet.instance; server = network.IsServer(); world = network.GetWorldUID();
        allowed = server && configured();
        revision = server ? 1 : 0;
        apply(allowed);
        router.Register<ZPackage>(Prefix + "Request", ReceiveRequest);
        router.Register<ZPackage>(Prefix + "State", ReceiveState);
    }

    public void Update(double now)
    {
        if (stopped) return;
        if (server)
        {
            var value = configured();
            if (value == allowed) return;
            allowed = value; revision++;
            apply(allowed);
            foreach (var peer in network.GetPeers())
                if (peer.IsReady() && !peer.m_server) SendState(peer.m_uid);
        }
        else if (revision == 0 && now >= nextRequest)
        {
            var peer = network.GetServerPeer();
            if (peer == null) return;
            nextRequest = now + 2;
            var p = new ZPackage(); p.Write(world);
            router.InvokeRoutedRPC(peer.m_uid, Prefix + "Request", p);
        }
    }

    private void ReceiveRequest(long sender, ZPackage p)
    {
        if (stopped || !server || p.ReadLong() != world ||
            !network.GetPeers().Exists(peer => peer.m_uid == sender && peer.IsReady() && !peer.m_server)) return;
        SendState(sender);
    }

    private void SendState(long peer)
    {
        var p = new ZPackage(); p.Write(world); p.Write(revision); p.Write(allowed);
        router.InvokeRoutedRPC(peer, Prefix + "State", p);
    }

    private void ReceiveState(long sender, ZPackage p)
    {
        if (stopped || server || sender == 0 || sender != network.GetServerPeer()?.m_uid || p.ReadLong() != world) return;
        var serial = p.ReadLong(); var value = p.ReadBool();
        if (serial <= revision) return;
        revision = serial; allowed = value;
        apply(allowed);
    }

    public void Stop() { stopped = true; apply(false); }
}
