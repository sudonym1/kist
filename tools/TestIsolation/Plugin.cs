using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory.TestIsolation;

// Test tooling only. This project is not part of the mod's build or package.
[BepInPlugin("local.sharedinventory.testisolation", "Shared Inventory Test Isolation", "0.1.0")]
[BepInDependency("local.sharedinventory")]
public sealed class Plugin : BaseUnityPlugin
{
    private static string saveRoot = "";
    private static Plugin instance = null!;

    private void Awake()
    {
        instance = this;
        try
        {
            var game = Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar);
            var root = Directory.GetParent(game)?.FullName
                ?? throw new InvalidOperationException("Missing isolated test root");
            var marker = Path.Combine(root, ".sharedinventory-test");
            var arguments = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(arguments, "-savedir");
            saveRoot = Path.Combine(root, "saves");
            if (!string.Equals(Path.GetFileName(game), "game", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(marker) || File.ReadAllText(marker).Trim() != "sharedinventory-test-v1"
                || Array.IndexOf(arguments, "-sharedinventory-test") < 0
                || index < 0 || index + 1 >= arguments.Length
                || !string.Equals(Path.GetFullPath(arguments[index + 1]).TrimEnd(Path.DirectorySeparatorChar),
                    saveRoot, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFullPath(Paths.BepInExRootPath).TrimEnd(Path.DirectorySeparatorChar),
                    Path.Combine(game, "BepInEx"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Isolation requires the marked game copy and its guarded launcher");
            Directory.CreateDirectory(saveRoot);
            Utils.SetSaveDataPath(saveRoot);
            new Harmony("local.sharedinventory.testisolation").PatchAll(typeof(Plugin).Assembly);
            if (FileHelpers.CloudStorageSupported || FileHelpers.CloudStorageSupportedAndEnabled
                || Utils.GetSaveDataPath(FileHelpers.FileSource.Local) != saveRoot)
                throw new InvalidOperationException("Local-only save isolation did not install");
            Logger.LogInfo("SHARED INVENTORY LOCAL-ONLY TEST ISOLATION: " + saveRoot);
        }
        catch (Exception error)
        {
            Logger.LogError("TEST ISOLATION FAILED; exiting before testing: " + error);
            Application.Quit();
            throw;
        }
    }

    // Enabled only inside the guarded test game. Uses Valheim's own free-build mode.
    private void Update()
    {
        var player = Player.m_localPlayer;
        if (player != null) player.SetNoPlacementCost(true);
    }

    [HarmonyPatch(typeof(Game), "Start")]
    private static class ReportTestWorld
    {
        private static void Postfix()
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
                instance.Logger.LogInfo("SHARED INVENTORY TEST WORLD uid=" + ZNet.instance.GetWorldUID()
                    + " name=" + ZNet.instance.GetWorldName());
        }
    }

    [HarmonyPatch]
    private static class LocalSaves
    {
        [HarmonyPatch(typeof(Utils), nameof(Utils.GetSaveDataPath))]
        [HarmonyPrefix]
        private static bool SavePath(ref string __result) { __result = saveRoot; return false; }

        [HarmonyPatch(typeof(FileHelpers), "get_CloudStorageSupported")]
        [HarmonyPrefix]
        private static bool CloudProvider(ref bool __result) { __result = false; return false; }

        [HarmonyPatch(typeof(FileHelpers), "get_CloudStorageSupportedAndEnabled")]
        [HarmonyPrefix]
        private static bool CloudSaving(ref bool __result) { __result = false; return false; }

        [HarmonyPatch(typeof(FileHelpers), nameof(FileHelpers.GetFiles))]
        [HarmonyPrefix]
        private static bool CloudFiles(FileHelpers.FileSource fileSource, ref string[] __result)
        {
            if (fileSource != FileHelpers.FileSource.Cloud) return true;
            __result = Array.Empty<string>();
            return false;
        }

        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.GetAllPlayerProfiles))]
        [HarmonyPostfix]
        private static void Characters(List<PlayerProfile> __result) =>
            __result.RemoveAll(profile => profile.m_fileSource != FileHelpers.FileSource.Local);

        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.GetWorldList))]
        [HarmonyPostfix]
        private static void Worlds(List<World> __result) =>
            __result.RemoveAll(world => world.m_fileSource != FileHelpers.FileSource.Local);
    }
}
