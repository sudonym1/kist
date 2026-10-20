using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ThorsKist
{
    [BepInPlugin(GUID, MODNAME, VERSION)]
    [BepInProcess("valheim.exe")]
    [BepInProcess("valheim_server.exe")]
    public class Main : BaseUnityPlugin
    {
        public const string MODNAME = "Thor's Kist";
        public const string AUTHOR = "posixone";
        public const string GUID = "posixone_ThorsKist";
        public const string VERSION = "0.8.6";

        public static Main Instance;
        public static ManualLogSource Log;
        private static Harmony _harmony;

        /// <summary>
        /// Our own Harmony instance, so the conflict check can ask it which
        /// methods we actually patched rather than keeping a second list of
        /// names that could drift out of step with the real patches.
        /// </summary>
        public static Harmony HarmonyInstance { get { return _harmony; } }

        // ---- General -------------------------------------------------------
        public static ConfigEntry<bool> ModEnable;
        public static ConfigEntry<int> ConfigVersion;
        public static ConfigEntry<bool> EnableProfiling;

        // ---- Keys ----------------------------------------------------------
        public static ConfigEntry<KeyboardShortcut> DepositModifier;
        public static ConfigEntry<KeyboardShortcut> OpenKistKey;

        // ---- Range ---------------------------------------------------------
        // Every range is measured from the hird's Kist anchor -- the spot the
        // hird was founded at, which the owner can move from the Kist window.
        public static ConfigEntry<bool> UnlimitedRange;
        public static ConfigEntry<float> DepositRadius;
        public static ConfigEntry<float> TakeRadius;
        public static ConfigEntry<float> CraftRadius;
        public static ConfigEntry<float> AnchorRadius;

        // ---- Crafting ------------------------------------------------------
        public static ConfigEntry<bool> CraftFromKist;

        // ---- Vault ---------------------------------------------------------
        public static ConfigEntry<int> KistRows;

        // ---- Feedback ------------------------------------------------------
        public static ConfigEntry<bool> DepositSound;
        public static ConfigEntry<bool> DepositVfx;
        public static ConfigEntry<string> DepositSfxPrefab;
        public static ConfigEntry<string> DepositVfxPrefab;
        public static ConfigEntry<bool> TakeSound;
        public static ConfigEntry<float> DepositVolume;
        public static ConfigEntry<float> TakeVolume;
        public static ConfigEntry<bool> AnchorEffects;
        public static ConfigEntry<string> AnchorSfxPrefab;
        public static ConfigEntry<string> AnchorVfxPrefab;
        public static ConfigEntry<string> AnchorFallVfxPrefab;
        public static ConfigEntry<float> AnchorVolume;
        public static ConfigEntry<float> AnchorDropHeight;
        public static ConfigEntry<float> AnchorDropSeconds;

        // ---- Interface -----------------------------------------------------
        public static ConfigEntry<bool> MapPin;
        public static ConfigEntry<string> MapPinPrefab;
        public static ConfigEntry<float> UiScale;
        public static ConfigEntry<int> ScrollRowsPerNotch;
        public static ConfigEntry<SortMode> KistSortMode;

        // ---- Server --------------------------------------------------------
        public static ConfigEntry<int> MinHirdSize;
        public static ConfigEntry<string> AnchorCostPrefab;
        public static ConfigEntry<int> AnchorCostAmount;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ModEnable = Config.Bind("General", "modEnable", true,
                "Master switch. False disables every part of Thor's Kist.");
            ConfigVersion = Config.Bind("General", "configVersion", 0,
                "Internal. Lets the mod correct settings whose defaults changed. Do not edit.");
            EnableProfiling = Config.Bind("Performance", "enableProfiling", false,
                "Enable optional diagnostic-plugin profiling hooks and the F4 capture key. Requires a game restart. Disabled by default.");

            DepositModifier = Config.Bind("Keys", "depositModifier",
                new KeyboardShortcut(KeyCode.LeftAlt),
                "Hold this while LEFT-CLICKING an item in your inventory to send it to the Kist.");
            OpenKistKey = Config.Bind("Keys", "openKist",
                new KeyboardShortcut(KeyCode.F3),
                "Opens and closes the Kist window.");

            UnlimitedRange = Config.Bind("Range", "unlimitedRange", false,
                "True ignores every radius below -- the Kist reaches the whole world.");
            DepositRadius = Config.Bind("Range", "depositRadius", 100f,
                new ConfigDescription("Metres from the Kist anchor within which you may deposit.",
                    new AcceptableValueRange<float>(1f, 100000f)));
            TakeRadius = Config.Bind("Range", "takeRadius", 100f,
                new ConfigDescription("Metres from the Kist anchor within which you may take.",
                    new AcceptableValueRange<float>(1f, 100000f)));
            CraftRadius = Config.Bind("Range", "craftRadius", 100f,
                new ConfigDescription("Metres from the Kist anchor within which crafting stations, smelters, " +
                    "kilns, fires and fermenters may draw on the Kist.",
                    new AcceptableValueRange<float>(1f, 100000f)));
            AnchorRadius = Config.Bind("Range", "anchorRadius", 100f,
                new ConfigDescription(
                    "Every hird member who is online must be within this many metres of the new spot before the " +
                    "Kist can be re-anchored. Server-side setting: only the host's value counts.",
                    new AcceptableValueRange<float>(1f, 100000f)));

            CraftFromKist = Config.Bind("Crafting", "craftFromKist", true,
                "Let crafting stations, the build hammer and repairs consume materials straight out of the Kist. " +
                "Also covers feeding smelters, kilns, fireplaces, cooking stations and fermenters.");

            KistRows = Config.Bind("Vault", "kistRows", 64,
                new ConfigDescription("Rows of 8 slots in the vault. 64 rows = 512 stacks. Server-side setting.",
                    new AcceptableValueRange<int>(4, 256)));

            DepositSound = Config.Bind("Feedback", "depositSound", true, "Play a sound when you store an item.");
            DepositVfx = Config.Bind("Feedback", "depositVfx", true, "Show an effect when you store an item.");
            DepositSfxPrefab = Config.Bind("Feedback", "depositSfxPrefab", "sfx_lootspawn",
                "Name of the game sound prefab played on deposit. Any prefab in ZNetScene works.");
            DepositVfxPrefab = Config.Bind("Feedback", "depositVfxPrefab", "vfx_Potion_eitr_minor",
                "Name of the game effect prefab shown on deposit. Any prefab in ZNetScene works. " +
                "The eitr effect is blue; vfx_Potion_health_medium is red and vfx_Potion_stamina_medium yellow.");
            TakeSound = Config.Bind("Feedback", "takeSound", true,
                "Play the usual pickup sound when an item arrives from the Kist.");
            DepositVolume = Config.Bind("Feedback", "depositVolume", 1.5f,
                new ConfigDescription("Multiplies the deposit sound's volume.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            TakeVolume = Config.Bind("Feedback", "takeVolume", 4f,
                new ConfigDescription("Multiplies the pickup sound's volume. The vanilla pickup effect is quiet " +
                    "by design because you are usually standing on the item.",
                    new AcceptableValueRange<float>(0.1f, 10f)));

            AnchorEffects = Config.Bind("Feedback", "anchorEffects", true,
                "Play the slam when a hird is founded or its Kist is re-anchored.");
            AnchorSfxPrefab = Config.Bind("Feedback", "anchorSfxPrefab", "sfx_frozenking_doubleslam_explosion",
                "Sound prefab for the Kist landing.");
            AnchorVfxPrefab = Config.Bind("Feedback", "anchorVfxPrefab", "vfx_trollsnow_groundslam",
                "Effect prefab for the impact where the Kist lands.");
            AnchorFallVfxPrefab = Config.Bind("Feedback", "anchorFallVfxPrefab", "vfx_Potion_eitr_minor",
                "Effect prefab that falls from the sky before the impact.");
            AnchorVolume = Config.Bind("Feedback", "anchorVolume", 1.5f,
                new ConfigDescription("Multiplies the anchor slam's volume.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            AnchorDropHeight = Config.Bind("Feedback", "anchorDropHeight", 12f,
                new ConfigDescription("How far above the ground the Kist starts its fall, in metres.",
                    new AcceptableValueRange<float>(1f, 60f)));
            AnchorDropSeconds = Config.Bind("Feedback", "anchorDropSeconds", 0.45f,
                new ConfigDescription("How long the fall takes.",
                    new AcceptableValueRange<float>(0.05f, 3f)));

            MapPin = Config.Bind("Interface", "mapPin", true,
                "Mark the Kist on your map. The pin follows the hird and cannot be deleted by hand.");
            MapPinPrefab = Config.Bind("Interface", "mapPinPrefab", "piece_chest_wood",
                "Build piece whose icon the Kist pin borrows. Any prefab with a Piece component works -- piece_chest_treasure, piece_chest_blackmetal and piece_chest_barrel are all real alternatives.");

            UiScale = Config.Bind("Interface", "uiScale", 1f,
                new ConfigDescription("Scales the Kist window's text and rows. Below 1 fits more on screen.",
                    new AcceptableValueRange<float>(0.6f, 1.6f)));
            ScrollRowsPerNotch = Config.Bind("Interface", "scrollRowsPerNotch", 5,
                new ConfigDescription("How many rows one notch of the mouse wheel moves the item list.",
                    new AcceptableValueRange<int>(1, 25)));
            KistSortMode = Config.Bind("Interface", "sortMode", SortMode.Name,
                "How the Kist list is ordered. Set from the buttons in the window; stored here so it " +
                "survives between sessions. Name is A-Z, Quantity puts the biggest hoards first, " +
                "Progression runs Meadows to Deep North, and Category groups wood with wood and ore with ore.");

            AnchorCostPrefab = Config.Bind("Server", "anchorCostPrefab", "SurtlingCore",
                "Item the founder must carry and spend to re-anchor the Kist. A prefab name, not a display name. " +
                "Empty means free. Server-side setting: only the host's value counts.");
            AnchorCostAmount = Config.Bind("Server", "anchorCostAmount", 25,
                new ConfigDescription("How many of anchorCostPrefab a re-anchor costs. Server-side setting.",
                    new AcceptableValueRange<int>(0, 999)));

            MinHirdSize = Config.Bind("Server", "minHirdSize", 2,
                new ConfigDescription(
                    "How many members a hird needs before its Kist unlocks. The point of a hird is that it is shared, " +
                    "so the default is 2. Server-side setting: only the host's value counts.",
                    new AcceptableValueRange<int>(1, 10)));

            MigrateConfig();

            if (!ModEnable.Value)
            {
                Log.LogInfo("ThorsKist disabled by config.");
                return;
            }

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo("ThorsKist " + VERSION + " loaded.");
        }

        /// <summary>
        /// Corrects settings whose defaults have changed since the config file
        /// was written. Changing a default in code does nothing for anyone who
        /// has already run the mod -- BepInEx keeps whatever is on disk -- so
        /// without this the blue deposit effect would only ever reach new
        /// installs. Guarded by configVersion so a deliberate later choice is
        /// never overwritten a second time.
        /// </summary>
        private void MigrateConfig()
        {
            const int current = 1;
            if (ConfigVersion.Value >= current) return;

            if (ConfigVersion.Value < 1 && DepositVfxPrefab.Value == "vfx_Potion_health_medium")
            {
                DepositVfxPrefab.Value = "vfx_Potion_eitr_minor";
                Log.LogInfo("ThorsKist: deposit effect moved from the red health potion to the blue eitr one.");
            }

            ConfigVersion.Value = current;
            Config.Save();
        }

        private void Update()
        {
            if (!ModEnable.Value) return;
            // Before anything else: a station scope that outlived its frame
            // leaked, and must not be allowed to move items outside a station.
            StationScope.FrameReset();
            JournalReservationGuard.Poll();
            JournalGameplaySession.Current?.Tick();
            JournalDirectRpc.Current?.Pump();
            KistConflicts.Tick();
            KistDialog.Tick();
            KistUI.Tick();
            KistClient.Tick();
            KistMap.Tick();
        }

        private void OnDestroy()
        {
            JournalWorldOwner.Stop();
            JournalGameplaySession.Current?.Dispose();
            JournalReservationGuard.Reset();
            JournalDirectRpc.Current?.Dispose();
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
