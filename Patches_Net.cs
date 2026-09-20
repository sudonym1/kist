using HarmonyLib;

namespace ThorsKist
{
    /// <summary>
    /// Wiring. Both routed RPCs are registered the moment the router exists, so
    /// neither side can miss a message because it joined the world too late.
    /// </summary>
    [HarmonyPatch]
    public static class Patches_Net
    {
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
            KistServer.Reset();
            KistClient.Reset();
            // The progression table is read out of ZoneSystem and ObjectDB, so
            // it is only as good as the world that was loaded when it was built.
            // Dropping it here costs one rebuild and removes any chance of a
            // table assembled from a half-loaded world outliving that world.
            KistSort.Reset();
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        [HarmonyPostfix]
        public static void OnPeerLost()
        {
            KistServer.BroadcastRoster();
        }
    }
}
