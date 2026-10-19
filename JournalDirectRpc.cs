using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    // World-owned adapter. The world authority installs this only once its
    // persistence and gameplay interception are ready. Merely registering RPCs
    // must never create a second item authority beside the legacy path.
    internal sealed class JournalDirectRpc : IDisposable
    {
        internal const string RpcName = "ThorsKist_Journal_v1";
        internal static JournalDirectRpc Current;
        private sealed class Connection : IDisposable
        {
            public ZNetPeer Peer;
            public JournalPeerIdentity Identity;
            public JournalPeerBinding Binding;
            public JournalConnectionWorker Worker;
            public readonly JournalSendQueue Outgoing = new JournalSendQueue();
            public void Dispose() { Binding?.Close(); Worker.Dispose(); Outgoing.Dispose(); }
        }
        private readonly ZNet world;
        // Must check catalog account/character membership and action range using
        // this captured position, never the legacy claimed header or roster map.
        private readonly Func<string, long, Vector3, VaultIdentity> resolve;
        private readonly Func<JournalPeerIdentity, JournalPeerBinding, JournalSendQueue, JournalFrame, CancellationToken, Task> receive;
        private readonly Action<SessionAddress> excluded;
        private readonly Dictionary<ZNetPeer, Connection> connections = new Dictionary<ZNetPeer, Connection>();
        private readonly int owner = Thread.CurrentThread.ManagedThreadId;
        private bool disposed;
        internal JournalDirectRpc(ZNet world, Func<string, long, Vector3, VaultIdentity> resolve,
            Func<JournalPeerIdentity, JournalPeerBinding, JournalSendQueue, JournalFrame, CancellationToken, Task> receive,
            Action<SessionAddress> excluded)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            this.receive = receive ?? throw new ArgumentNullException(nameof(receive));
            this.excluded = excluded ?? throw new ArgumentNullException(nameof(excluded));
        }

        internal static void Register(ZNetPeer peer)
        {
            // Capture the exact peer, not an RPC-supplied UID or routed sender.
            peer.m_rpc.Register<ZPackage>(RpcName, (rpc, package) => Current?.Receive(peer, rpc, package));
        }
        private bool IsCurrent(ZNetPeer peer, ZRpc rpc)
            => !disposed && ReferenceEquals(ZNet.instance, world) && peer.IsReady() && peer.m_socket.IsConnected()
                && ReferenceEquals(peer.m_rpc, rpc) && world.GetPeers().Contains(peer)
                && (world.IsServer() || ReferenceEquals(world.GetServerPeer(), peer));

        internal static string SocketAccount(ZNetPeer peer)
        {
            // TCP endpoints are addresses, not authenticated accounts. Crossplay
            // GetHostName is the platform ID sent in its package; the Party peer
            // entity is supplied by the connected socket instead.
            if (peer.m_socket is ZSteamSocket) return "steam:" + peer.m_socket.GetHostName();
            if (peer.m_socket is ZPlayFabSocket playfab && !string.IsNullOrWhiteSpace(playfab.m_remotePlayerId))
                return "playfab:" + playfab.m_remotePlayerId;
            return null;
        }
        private JournalPeerIdentity Identify(ZNetPeer peer)
        {
            string account = SocketAccount(peer);
            if (account == null || peer.m_playerID == 0 || peer.m_characterID.IsNone() || ZDOMan.instance == null) return null;
            var character = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (character == null || character.GetOwner() != peer.m_uid
                || character.GetLong(ZDOVars.s_playerID, 0L) != peer.m_playerID) return null;
            var position = character.GetPosition();
            if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z)) return null;
            var vault = resolve(account, peer.m_playerID, position);
            return vault == null ? null : new JournalPeerIdentity(peer.m_rpc, account, peer.m_playerID, vault);
        }
        private void Receive(ZNetPeer peer, ZRpc rpc, ZPackage package)
        {
            RequireOwner();
            if (!IsCurrent(peer, rpc) || package.Size() < JournalWire.FrameHeaderBytes || package.Size() > JournalWire.MaximumFrameBytes) return;
            // Clients accept replies only from the actual current server peer.
            var identity = world.IsServer() ? Identify(peer) : null;
            if (world.IsServer() && identity == null) { Remove(peer); return; }
            if (connections.TryGetValue(peer, out var existing)
                && world.IsServer() && !existing.Binding.IsCurrent(identity)) { Remove(peer); return; }
            if (!connections.TryGetValue(peer, out var c))
            {
                c = Open(peer, identity);
            }
            if (!c.Worker.TryReceive(package.GetArray())) Remove(peer);
        }
        private Connection Open(ZNetPeer peer, JournalPeerIdentity identity)
        {
            var c = new Connection { Peer = peer, Identity = identity, Binding = identity == null ? null : new JournalPeerBinding(identity) };
            var connection = c;
            c.Worker = new JournalConnectionWorker((frame, token) =>
            {
                if (worldIsServer && frame.Message != JournalMessage.Login && frame.Message != JournalMessage.Command
                    && frame.Message != JournalMessage.Acknowledge && frame.Message != JournalMessage.Logout && frame.Message != JournalMessage.Suspend)
                    throw new System.IO.InvalidDataException("Invalid client journal direction");
                if (!worldIsServer && frame.Message != JournalMessage.Reply && frame.Message != JournalMessage.Entries && frame.Message != JournalMessage.Transfer)
                    throw new System.IO.InvalidDataException("Invalid server journal direction");
                if (worldIsServer) JournalRequest.Decode(frame, connection.Identity, connection.Binding);
                return receive(connection.Identity, connection.Binding, connection.Outgoing, frame, token);
            });
            connections.Add(peer, c);
            return c;
        }
        private bool worldIsServer;
        internal void Install()
        {
            RequireOwner(); if (Current != null) throw new InvalidOperationException("Journal transport already installed");
            if (disposed || !ReferenceEquals(ZNet.instance, world)) throw new InvalidOperationException("World is no longer current");
            foreach (var peer in world.GetPeers()) Register(peer);
            worldIsServer = world.IsServer(); Current = this;
        }
        // Called on the game thread. Workers retain their connection's queue,
        // not the dictionary or any mutable Unity object.
        internal JournalSendQueue SendQueue(ZNetPeer peer)
        {
            RequireOwner();
            if (!IsCurrent(peer, peer.m_rpc)) return null;
            if (!connections.TryGetValue(peer, out var c))
            {
                var identity = worldIsServer ? Identify(peer) : null;
                if (worldIsServer && identity == null) return null;
                c = Open(peer, identity);
            }
            return c.Outgoing;
        }
        internal void Pump(int maximumFramesPerPeer = 2)
        {
            RequireOwner(); if (maximumFramesPerPeer <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFramesPerPeer));
            foreach (var c in new List<Connection>(connections.Values))
            {
                if (!IsCurrent(c.Peer, c.Peer.m_rpc) || c.Worker.Failure != null) { Remove(c.Peer); continue; }
                if (worldIsServer)
                {
                    var identity = Identify(c.Peer);
                    if (identity == null || !c.Binding.IsCurrent(identity)) { Remove(c.Peer); continue; }
                }
                for (int i = 0; i < maximumFramesPerPeer; i++)
                {
                    // Avoid draining our queue into an already backlogged socket.
                    if (c.Peer.m_socket.GetSendQueueSize() >= 256 * 1024) break;
                    var bytes = c.Outgoing.Dequeue(); if (bytes == null) break;
                    c.Peer.m_rpc.Invoke(RpcName, new ZPackage(bytes));
                }
            }
        }
        private void Remove(ZNetPeer peer)
        {
            if (!connections.TryGetValue(peer, out var c)) return;
            var session = c.Binding?.Session;
            connections.Remove(peer); c.Dispose();
            // The authority callback enqueues exclusion/settlement; it must never
            // wait on the writer or another client from this game-thread call.
            if (session != null) excluded(session);
        }
        public void Dispose()
        {
            RequireOwner(); if (disposed) return; disposed = true;
            if (ReferenceEquals(Current, this)) Current = null;
            foreach (var peer in new List<ZNetPeer>(connections.Keys)) Remove(peer);
        }
        private void RequireOwner() { if (Thread.CurrentThread.ManagedThreadId != owner) throw new InvalidOperationException("Direct RPC requires the game thread"); }
        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
