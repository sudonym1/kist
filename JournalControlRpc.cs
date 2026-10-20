using UnityEngine;

namespace ThorsKist
{
    // Metadata uses the exact direct peer too. A routed packet's sender UID is
    // not an authentication boundary, even for non-item hird operations.
    internal static class JournalControlRpc
    {
        private const string Request = "ThorsKist_Control_v5";
        private const string Reply = "ThorsKist_ControlReply_v5";
        internal static void Register(ZNetPeer peer)
        {
            peer.m_rpc.Register<ZPackage>(Request, (rpc, package) =>
            {
                var world = ZNet.instance;
                if (world == null || !world.IsServer() || !Current(world, peer, rpc) || package.Size() > 65536) return;
                KistServer.OnMessage(peer.m_uid, package);
            });
            peer.m_rpc.Register<ZPackage>(Reply, (rpc, package) =>
            {
                var world = ZNet.instance;
                if (world == null || world.IsServer() || !ReferenceEquals(world.GetServerPeer(), peer)
                    || !Current(world, peer, rpc) || package.Size() > 65536) return;
                KistClient.OnMessage(peer.m_uid, package);
            });
        }
        private static bool Current(ZNet world, ZNetPeer peer, ZRpc rpc)
            => peer.IsReady() && peer.m_socket.IsConnected() && ReferenceEquals(peer.m_rpc, rpc) && world.GetPeers().Contains(peer);
        internal static void SendRequest(ZPackage package)
        {
            var world = ZNet.instance; if (world == null) return;
            if (world.IsServer()) KistServer.OnMessage(ZNet.GetUID(), package);
            else
            {
                var peer = world.GetServerPeer();
                if (peer != null && Current(world, peer, peer.m_rpc)) peer.m_rpc.Invoke(Request, package);
            }
        }
        internal static void SendReply(long uid, ZPackage package)
        {
            var world = ZNet.instance; if (world == null || !world.IsServer()) return;
            if (uid == ZNet.GetUID()) KistClient.OnMessage(uid, package);
            else
            {
                var peer = world.GetPeer(uid);
                if (peer != null && Current(world, peer, peer.m_rpc)) peer.m_rpc.Invoke(Reply, package);
            }
        }
    }
}
