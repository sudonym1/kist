using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ThorsKist
{
    // These patches are installed only after the explicit test-switch/savedir
    // guard succeeds. Steam authentication/networking stays enabled; only this
    // game's save-provider view is local-only. No Steam preference is changed.
    [HarmonyPatch]
    internal static class TestSaveIsolation
    {
        internal static string Root;
        [HarmonyPatch(typeof(FileHelpers), "get_CloudStorageSupported")]
        [HarmonyPrefix]
        private static bool NoCloudProvider(ref bool __result) { __result = false; return false; }
        [HarmonyPatch(typeof(FileHelpers), "get_CloudStorageSupportedAndEnabled")]
        [HarmonyPrefix]
        private static bool NoCloudSaving(ref bool __result) { __result = false; return false; }
        [HarmonyPatch(typeof(FileHelpers), nameof(FileHelpers.GetFiles))]
        [HarmonyPrefix]
        private static bool NoCloudEnumeration(FileHelpers.FileSource fileSource, ref string[] __result)
        {
            if (fileSource != FileHelpers.FileSource.Cloud) return true;
            __result = Array.Empty<string>(); return false;
        }
        [HarmonyPatch(typeof(Utils), nameof(Utils.GetSaveDataPath))]
        [HarmonyPrefix]
        private static bool IsolatedPath(ref string __result)
        {
            if (string.IsNullOrEmpty(Root)) throw new InvalidOperationException("Test save root was not initialized");
            __result = Root; return false;
        }
        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.GetAllPlayerProfiles))]
        [HarmonyPostfix]
        private static void OnlyLocalCharacters(List<PlayerProfile> __result)
            => __result.RemoveAll(p => p.m_fileSource != FileHelpers.FileSource.Local);
        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.GetWorldList))]
        [HarmonyPostfix]
        private static void OnlyLocalWorlds(List<World> __result)
            => __result.RemoveAll(w => w.m_fileSource != FileHelpers.FileSource.Local);
    }
}
