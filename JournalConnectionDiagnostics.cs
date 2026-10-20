using System.Collections.Generic;

namespace ThorsKist
{
    // Connection transitions only; never a frame trace or per-transfer logger.
    internal static class JournalConnectionDiagnostics
    {
        private static readonly Dictionary<string, string> states = new Dictionary<string, string>();
        private static ZNet clientWorld;
        internal static void State(string key, string value)
        {
            if (states.TryGetValue(key, out var previous) && previous == value) return;
            states[key] = value;
            Main.Log.LogInfo("KIST CONNECT " + value);
        }
        internal static void ClientReady(ZNet world, Player player, bool introduced, Hird hird)
        {
            if (ReferenceEquals(clientWorld, world)) return;
            clientWorld = world;
            State("client-world", "client world=" + (world.GetWorld() != null ? world.GetWorldUID().ToString() : "pending")
                + " hosting=" + world.IsServer() + " character=" + player.GetPlayerID()
                + " introduced=" + introduced + " hird=" + (hird?.Id ?? "none"));
        }
        internal static string Peer(ZNetPeer peer, long claimed)
        {
            if (peer == null) return "peer missing; claimed=" + claimed;
            var zdo = !peer.m_characterID.IsNone() ? ZDOMan.instance?.GetZDO(peer.m_characterID) : null;
            return "peer=" + peer.m_uid + " ready=" + peer.IsReady() + " connected=" + peer.m_socket.IsConnected()
                + " socket=" + peer.m_socket.GetType().Name + " claimed=" + claimed
                + " characterZdo=" + peer.m_characterID + " replicated=" + (zdo != null)
                + " owner=" + (zdo?.GetOwner() ?? 0) + " actual=" + (zdo?.GetLong(ZDOVars.s_playerID, 0L) ?? 0);
        }
        internal static void Clear() { states.Clear(); clientWorld = null; }
    }
}
