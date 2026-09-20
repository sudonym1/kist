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
        public const string VERSION = "0.1.0";

        public static Main Instance;
        public static ManualLogSource Log;
        private static Harmony _harmony;

        // ---- General -------------------------------------------------------
        public static ConfigEntry<bool> ModEnable;

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

        // ---- Crafting ------------------------------------------------------
        public static ConfigEntry<bool> CraftFromKist;

        // ---- Vault ---------------------------------------------------------
        public static ConfigEntry<int> KistRows;

        // ---- Feedback ------------------------------------------------------
        public static ConfigEntry<bool> DepositSound;
        public static ConfigEntry<bool> DepositVfx;
        public static ConfigEntry<float> DepositVolume;

        // ---- Server --------------------------------------------------------
        public static ConfigEntry<int> MinHirdSize;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ModEnable = Config.Bind("General", "modEnable", true,
                "Master switch. False disables every part of Thor's Kist.");

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
                new ConfigDescription("Metres from the Kist anchor within which crafting stations may draw on the Kist.",
                    new AcceptableValueRange<float>(1f, 100000f)));

            CraftFromKist = Config.Bind("Crafting", "craftFromKist", true,
                "Let crafting stations, the build hammer and repairs consume materials straight out of the Kist.");

            KistRows = Config.Bind("Vault", "kistRows", 64,
                new ConfigDescription("Rows of 8 slots in the vault. 64 rows = 512 stacks. Server-side setting.",
                    new AcceptableValueRange<int>(4, 256)));

            DepositSound = Config.Bind("Feedback", "depositSound", true, "Play the deposit chime.");
            DepositVfx = Config.Bind("Feedback", "depositVfx", true, "Draw the streaks of light on deposit.");
            DepositVolume = Config.Bind("Feedback", "depositVolume", 0.6f,
                new ConfigDescription("Volume of the deposit chime.", new AcceptableValueRange<float>(0f, 1f)));

            MinHirdSize = Config.Bind("Server", "minHirdSize", 2,
                new ConfigDescription(
                    "How many members a hird needs before its Kist unlocks. The point of a hird is that it is shared, " +
                    "so the default is 2. Server-side setting: only the host's value counts.",
                    new AcceptableValueRange<int>(1, 10)));

            if (!ModEnable.Value)
            {
                Log.LogInfo("ThorsKist disabled by config.");
                return;
            }

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo("ThorsKist " + VERSION + " loaded.");
        }

        private void Update()
        {
            if (!ModEnable.Value) return;
            KistUI.Tick();
            KistClient.Tick();
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
