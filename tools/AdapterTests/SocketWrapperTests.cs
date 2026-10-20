using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private void SocketWrappers()
        {
            int start = checks;
            var playfab = (ZPlayFabSocket)FormatterServices.GetUninitializedObject(typeof(ZPlayFabSocket));
            typeof(ZPlayFabSocket).GetField("m_remotePlayerId").SetValue(playfab, "native-fixture-account");
            var peer = (ZNetPeer)FormatterServices.GetUninitializedObject(typeof(ZNetPeer));
            peer.m_socket = playfab;
            Check(JournalDirectRpc.SocketAccount(peer) == "playfab:native-fixture-account", "Direct transport retains authenticated account source");
            var tcp = (ISocket)FormatterServices.GetUninitializedObject(typeof(ZSocket2));
            peer.m_socket = tcp;
            Check(JournalDirectRpc.SocketAccount(peer) == null, "TCP endpoint is never promoted to an authenticated account");
            Check(JournalSocketTransport.Unwrap(null) == null, "Missing transport fails closed");
            ISocket nested = playfab;
            string root = Path.Combine(Path.GetDirectoryName(Paths.BepInExRootPath), "fixtures");
            foreach (string name in new[] { "MistBeGone.dll", "Build Camera.dll" })
            {
                var assembly = Assembly.Load(File.ReadAllBytes(Path.Combine(root, name)));
                var type = assembly.GetType("ServerSync.ConfigSync+SendConfigsAfterLogin+BufferingSocket", true);
                Func<ISocket, ISocket> wrap = input => (ISocket)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { input }, null);
                var wrapped = wrap(playfab); peer.m_socket = wrapped;
                Check(ReferenceEquals(JournalSocketTransport.Unwrap(wrapped), playfab), "Actual " + name + " wrapper resolves exact underlying connection");
                Check(JournalDirectRpc.SocketAccount(peer) == "playfab:native-fixture-account", "Actual " + name + " wrapper cannot masquerade as a separate PlayFab account");
                var steam = (ZSteamSocket)FormatterServices.GetUninitializedObject(typeof(ZSteamSocket));
                Check(ReferenceEquals(JournalSocketTransport.Unwrap(wrap(steam)), steam), "Actual " + name + " wrapper resolves Steam despite inheriting PlayFab");
                peer.m_socket = wrap(tcp);
                Check(JournalDirectRpc.SocketAccount(peer) == null, "Wrapped TCP stays unauthenticated");
                nested = wrap(nested); peer.m_socket = nested;
                Check(JournalDirectRpc.SocketAccount(peer) == "playfab:native-fixture-account", "Independent embedded ServerSync wrappers can nest");
                var field = type.GetField("Original"); field.SetValue(wrapped, wrapped);
                Check(JournalSocketTransport.Unwrap(wrapped) == null, "Cyclic wrapper chain is bounded and refused");
                field.SetValue(wrapped, null);
                Check(JournalSocketTransport.Unwrap(wrapped) == null, "Missing wrapper transport fails closed");
            }
            CutoverLog("M7 SOCKET WRAPPER PASS " + (checks - start) + " native assertions with actual installed ServerSync implementations");
        }
    }
}
