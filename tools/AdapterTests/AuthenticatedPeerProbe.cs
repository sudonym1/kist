using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    // Test RPC only: validates actual connected-peer facts and the production
    // ingress binding rules. It never installs the gameplay authority or touches
    // character items. Both independent game clients need this isolated plugin.
    internal sealed class AuthenticatedPeerProbe : IDisposable
    {
        private const string Rpc = "ThorsKist_M7_AuthenticatedProbe_v1";
        internal static AuthenticatedPeerProbe Current;
        private readonly Action<string> report;
        private readonly HashSet<ZNetPeer> passed = new HashSet<ZNetPeer>();
        private ZNet world;
        private long worldId;
        private JournalDirectRpc inspector;
        private Func<ZNetPeer, JournalPeerIdentity> identify;
        private float next;
        private Guid pending;
        private bool disposed;
        internal AuthenticatedPeerProbe(Action<string> report) { this.report = report; Current = this; }
        internal static void Register(ZNetPeer peer)
            => peer.m_rpc.Register<ZPackage>(Rpc, (rpc, package) => Current?.Receive(peer, rpc, package));
        private bool Live(ZNetPeer peer, ZRpc rpc)
            => !disposed && world != null && ReferenceEquals(world, ZNet.instance) && ReferenceEquals(peer.m_rpc, rpc)
                && world.GetPeers().Contains(peer) && peer.IsReady() && peer.m_socket.IsConnected();
        internal void Tick()
        {
            if (disposed || Time.unscaledTime < next) return; next = Time.unscaledTime + 2;
            if (!ReferenceEquals(world, ZNet.instance) || (world != null && world.GetWorldUID() != worldId))
            {
                inspector?.Dispose(); inspector = null; identify = null; passed.Clear(); pending = Guid.Empty; world = ZNet.instance;
                if (world != null)
                {
                    worldId = world.GetWorldUID();
                    var vault = new VaultIdentity(worldId.ToString(), "authenticated-probe-only", Guid.NewGuid());
                    inspector = new JournalDirectRpc(world, (account, character, position) => (vault, JournalAccess.All),
                        (peer, binding, queue, frame, token) => System.Threading.Tasks.Task.CompletedTask, _ => { });
                    identify = (Func<ZNetPeer, JournalPeerIdentity>)Delegate.CreateDelegate(typeof(Func<ZNetPeer, JournalPeerIdentity>), inspector,
                        typeof(JournalDirectRpc).GetMethod("Identify", BindingFlags.Instance | BindingFlags.NonPublic));
                    foreach (var peer in world.GetPeers()) Register(peer);
                }
            }
            if (world == null || world.IsServer() || Player.m_localPlayer == null) return;
            var server = world.GetServerPeer();
            if (server == null || passed.Contains(server) || !Live(server, server.m_rpc)) return;
            pending = Guid.NewGuid(); var message = new ZPackage(); message.Write(1); message.Write(pending.ToString("N"));
            server.m_rpc.Invoke(Rpc, message);
        }
        private static string Fingerprint(string account)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(account))).Replace("-", "").Substring(0, 12);
        }
        private void Receive(ZNetPeer peer, ZRpc rpc, ZPackage message)
        {
            if (!Live(peer, rpc) || message.Size() > 256) return;
            try
            {
                int kind = message.ReadInt(); var request = Guid.ParseExact(message.ReadString(), "N");
                if (world.IsServer())
                {
                    if (kind != 1 || identify == null) throw new InvalidDataException("Wrong probe request direction");
                    var actor = identify(peer); // Actual production socket/ZDO/ownership checks.
                    if (actor == null) return; // Wait for replicated character readiness; no claimed headers accepted.
                    if (message.GetPos() != message.Size()) throw new InvalidDataException("Trailing probe request");
                    if (!passed.Contains(peer))
                    {
                        var binding = new JournalPeerBinding(actor); var v = actor.Vault;
                        var address = new SessionAddress(v.World, v.Vault, v.Epoch, actor.Account, Guid.NewGuid());
                        var command = new ItemCommand(address, Guid.NewGuid(), Guid.NewGuid(), ItemCommandKind.Deposit, new ItemRecord("probe-only", 1, 110, Array.Empty<byte>()));
                        var frame = new JournalFrame(JournalMessage.Command, command.RequestId, JournalWire.EncodeCommand(command));
                        Reject(() => JournalRequest.Decode(frame, actor, binding));
                        if (!binding.Bind(actor, address) || JournalRequest.Decode(frame, actor, binding).Command.RequestId != command.RequestId)
                            throw new InvalidOperationException("Authenticated binding failed");
                        Reject(() => JournalRequest.Decode(frame, new JournalPeerIdentity(new object(), actor.Account, actor.Character, v), binding));
                        Reject(() => JournalRequest.Decode(frame, new JournalPeerIdentity(actor.Connection, actor.Account + ":forged", actor.Character, v), binding));
                        Reject(() => JournalRequest.Decode(frame, new JournalPeerIdentity(actor.Connection, actor.Account, actor.Character + 1, v), binding));
                        binding.Close(); Reject(() => JournalRequest.Decode(frame, actor, binding));
                        passed.Add(peer);
                        report("M7 AUTHENTICATED PEER PASS backend=" + peer.m_socket.GetType().Name + " account-fingerprint=" + Fingerprint(actor.Account)
                            + " character=" + actor.Character + "; actual socket/ZDO owner and pre-login/stale/forged-account/forged-character/closed binding checks passed. Probe-only membership; gameplay owner remains uninstalled.");
                    }
                    var reply = new ZPackage(); reply.Write(2); reply.Write(request.ToString("N")); reply.Write(Fingerprint(actor.Account));
                    rpc.Invoke(Rpc, reply);
                }
                else
                {
                    if (!ReferenceEquals(world.GetServerPeer(), peer) || kind != 2 || request != pending) throw new InvalidDataException("Wrong server or unrequested probe reply");
                    string fingerprint = message.ReadString();
                    if (fingerprint.Length != 12 || message.GetPos() != message.Size()) throw new InvalidDataException("Invalid probe reply");
                    passed.Add(peer);
                    report("M7 AUTHENTICATED RPC ROUNDTRIP PASS actual current server connection; account-fingerprint=" + fingerprint);
                }
            }
            catch (Exception error) { report("M7 AUTHENTICATED PEER FAIL " + error); }
        }
        private static void Reject(Action action)
        { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Forged/unbound probe command was accepted"); }
        public void Dispose()
        { disposed = true; inspector?.Dispose(); if (ReferenceEquals(Current, this)) Current = null; }
    }
    [HarmonyPatch(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) })]
    internal static class RegisterAuthenticatedProbe
    {
        [HarmonyPostfix] private static void Postfix(ZNetPeer peer) { AuthenticatedPeerProbe.Register(peer); }
    }
}
