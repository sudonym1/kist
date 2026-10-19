using HarmonyLib;

namespace ThorsKist
{
    /// <summary>
    /// Wiring. Legacy routed RPCs register with the router; journal messages
    /// register on each direct peer connection and require an installed owner.
    /// </summary>
    [HarmonyPatch]
    public static class Patches_Net
    {
        [HarmonyPatch(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) })]
        [HarmonyPostfix]
        public static void RegisterJournalPeer(ZNetPeer peer)
        {
            JournalDirectRpc.Register(peer);
        }

        [HarmonyPatch(typeof(ZRoutedRpc), MethodType.Constructor, new[] { typeof(bool) })]
        [HarmonyPostfix]
        public static void RegisterRpcs(ZRoutedRpc __instance)
        {
            __instance.Register<ZPackage>(Wire.RpcToServer, KistServer.OnMessage);
            __instance.Register<ZPackage>(Wire.RpcToClient, KistClient.OnMessage);
            Main.Log.LogInfo("ThorsKist: routed RPCs registered.");
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        [HarmonyPrefix]
        public static void OnShutdown()
        {
            JournalDirectRpc.Current?.Dispose();
            KistServer.Reset();
            KistClient.Reset();
            // The progression table is read out of ZoneSystem and ObjectDB, so
            // it is only as good as the world that was loaded when it was built.
            // Dropping it here costs one rebuild and removes any chance of a
            // table assembled from a half-loaded world outliving that world.
            KistSort.Reset();
            // Findings survive -- the installed mod set does not change while
            // the game runs -- but the warning is offered again on the next
            // world, since leaving a world is exactly when someone acts on it.
            KistConflicts.Reset();
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        [HarmonyPostfix]
        public static void OnPeerLost()
        {
            KistServer.BroadcastRoster();
        }
    }
}
