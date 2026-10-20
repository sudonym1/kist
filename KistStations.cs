using HarmonyLib;

namespace ThorsKist
{
    /// <summary>
    /// Feeding smelters, kilns, fireplaces, cooking stations and fermenters out
    /// of the Kist.
    ///
    /// Query scopes widen inventory checks using verified Kist stock. Actual
    /// interactions prefer local items. JournalMaterials sends Kist-only inputs
    /// straight to the station and settles payment asynchronously, without an
    /// inventory item. Committing scopes never expose synthetic inventory items.
    ///
    /// Everything patched here is a vanilla method. Nothing in this file knows
    /// about any other mod. The scope prefixes claim Priority.First so that the
    /// Kist is part of the transaction before anyone else's prefix can displace
    /// the original -- see [[KistConflicts]] for what happens when one does.
    /// </summary>
    public static class StationScope
    {
        private static int _depth;
        private static int _commit;

        /// <summary>
        /// True while a station is asking the player's inventory for something.
        /// Rides on CraftContext so the radius, the craftFromKist switch and the
        /// re-entry guard are all the ones the crafting path already uses.
        /// </summary>
        public static bool Active { get { return _depth > 0 && CraftContext.Active; } }

        /// <summary>
        /// True only while a station is actually TAKING, never while it is
        /// merely asking whether it could be used.
        ///
        /// CookingStation.CanUseItems calls FindCookableItem/GetItem. Hover
        /// queries can inspect Kist metadata, but vanilla item consumption must
        /// see only real pack items; Kist-only insertion has its own path.
        /// </summary>
        public static bool Committing { get { return _commit > 0 && Active; } }

        /// <summary>Raw counters, for diagnostics only.</summary>
        public static string State
        {
            get
            {
                return "depth=" + _depth + " commit=" + _commit +
                       " craftActive=" + CraftContext.Active +
                       " canCraft=" + KistClient.CanCraft;
            }
        }

        /// <summary>
        /// Names the station call that opened the outermost scope, so a
        /// diagnostic can say which one was in force rather than just a number.
        /// </summary>
        public static string Label = "";

        public static void Enter(bool committing, string label = "?")
        {
            if (_depth == 0) Label = label;
            else Label += ">" + label;
            _depth++;
            if (committing) _commit++;
            CraftContext.Push();
        }

        public static void Exit(bool committing)
        {
            CraftContext.Pop();
            if (committing && _commit > 0) _commit--;
            if (_depth > 0) _depth--;
        }

        /// <summary>
        /// Safety net, called once a frame.
        ///
        /// A Harmony postfix does not run if the original method throws, so one
        /// exception inside a station -- ours or anyone else's -- would leave
        /// the scope open for the rest of the session. An open scope means
        /// inventory queries would stay widened during unrelated calls. A station
        /// interaction finishes within one frame, so anything still open at the
        /// start of the next frame leaked and is forced shut.
        /// </summary>
        public static void FrameReset()
        {
            if (_depth == 0) return;

            Main.Log.LogWarning("ThorsKist: station scope was left open (depth " + _depth +
                                ") -- a station call threw. Forcing it shut.");
            while (_depth > 0) { CraftContext.Pop(); _depth--; }
            _commit = 0;
        }
    }

    // ----------------------------------------------------------------------
    // Where a station is asking
    //
    // Written out one target at a time rather than through TargetMethods so
    // tools/VerifyPatches can resolve every one against the real assembly. A
    // station that silently stopped being covered would look exactly like a
    // station nobody had tested.
    // ----------------------------------------------------------------------

    [HarmonyPatch]
    public static class Patches_StationScope
    {
        // Smelter covers the charcoal kiln, the blast furnace and the smelter:
        // they are all Smelter with different conversions.
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void SmelterOrePre() { StationScope.Enter(true, "Smelter.OnAddOre"); }
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        [HarmonyPostfix]
        public static void SmelterOrePost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void SmelterFuelPre() { StationScope.Enter(true, "Smelter.OnAddFuel"); }
        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        [HarmonyPostfix]
        public static void SmelterFuelPost() { StationScope.Exit(true); }

        // CanUseItems decides whether the station offers the interaction at all.
        // Without it the prompt never appears and OnAdd* is never reached.
        [HarmonyPatch(typeof(Smelter), nameof(Smelter.CanUseItems))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void SmelterCanPre() { StationScope.Enter(false, "Smelter.CanUseItems"); }
        [HarmonyPatch(typeof(Smelter), nameof(Smelter.CanUseItems))]
        [HarmonyPostfix]
        public static void SmelterCanPost() { StationScope.Exit(false); }

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FireplaceInteractPre() { StationScope.Enter(true, "Fireplace.Interact"); }
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
        [HarmonyPostfix]
        public static void FireplaceInteractPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.UseItem))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FireplaceUsePre() { StationScope.Enter(true, "Fireplace.UseItem"); }
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.UseItem))]
        [HarmonyPostfix]
        public static void FireplaceUsePost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.CanUseItems))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FireplaceCanPre() { StationScope.Enter(false, "Fireplace.CanUseItems"); }
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.CanUseItems))]
        [HarmonyPostfix]
        public static void FireplaceCanPost() { StationScope.Exit(false); }

        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookFuelPre() { StationScope.Enter(true, "CookingStation.OnAddFuelSwitch"); }
        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        [HarmonyPostfix]
        public static void CookFuelPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(CookingStation), "OnAddFoodSwitch")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookFoodPre() { StationScope.Enter(true, "CookingStation.OnAddFoodSwitch"); }
        [HarmonyPatch(typeof(CookingStation), "OnAddFoodSwitch")]
        [HarmonyPostfix]
        public static void CookFoodPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.UseItem))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookUsePre() { StationScope.Enter(true, "CookingStation.UseItem"); }
        [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.UseItem))]
        [HarmonyPostfix]
        public static void CookUsePost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.CanUseItems))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookCanPre() { StationScope.Enter(false, "CookingStation.CanUseItems"); }
        [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.CanUseItems))]
        [HarmonyPostfix]
        public static void CookCanPost() { StationScope.Exit(false); }

        // The cooking station finds its food in OnInteract, before UseItem or
        // the food switch is ever reached, so the scope has to be open there.
        [HarmonyPatch(typeof(CookingStation), "OnInteract")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookInteractPre() { StationScope.Enter(true, "CookingStation.OnInteract"); }
        [HarmonyPatch(typeof(CookingStation), "OnInteract")]
        [HarmonyPostfix]
        public static void CookInteractPost() { StationScope.Exit(true); }

        // Same for the fermenter: Interact does the looking, AddItem only the
        // taking.
        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Interact))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FermentInteractPre() { StationScope.Enter(true, "Fermenter.Interact"); }
        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Interact))]
        [HarmonyPostfix]
        public static void FermentInteractPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Fermenter), "AddItem")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FermentAddPre() { StationScope.Enter(true, "Fermenter.AddItem"); }
        [HarmonyPatch(typeof(Fermenter), "AddItem")]
        [HarmonyPostfix]
        public static void FermentAddPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.UseItem))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FermentUsePre() { StationScope.Enter(true, "Fermenter.UseItem"); }
        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.UseItem))]
        [HarmonyPostfix]
        public static void FermentUsePost() { StationScope.Exit(true); }
    }

    // ----------------------------------------------------------------------
    // Query widening uses verified display records. Commit prefixes insert
    // directly into stations; these hooks never move or spend a mirror.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem), new[] { typeof(string), typeof(bool) })]
    public static class Patches_HaveItem
    {
        [HarmonyPostfix]
        public static void Postfix(Inventory __instance, string name, ref bool __result)
        {
            if (!__result && StationScope.Active && !StationScope.Committing && CraftContext.IsPlayerInventory(__instance))
                __result = JournalGameplaySession.Current?.Count(name) > 0;
        }
    }
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItemsByName))]
    public static class Patches_CountItemsByName
    {
        [HarmonyPostfix]
        public static void Postfix(Inventory __instance, string[] names, int quality, ref int __result)
        {
            if (!StationScope.Active || StationScope.Committing || !CraftContext.IsPlayerInventory(__instance) || names == null) return;
            var session = JournalGameplaySession.Current; if (session == null) return;
            foreach (var name in names) if (!string.IsNullOrEmpty(name)) __result += session.Count(name, quality);
        }
    }
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetItem), new[] { typeof(string), typeof(int), typeof(bool) })]
    public static class Patches_GetItem
    {
        [HarmonyPostfix]
        public static void Postfix(Inventory __instance, string name, int quality, ref ItemDrop.ItemData __result)
        {
            if (__result != null || !CraftContext.Active || StationScope.Committing || !CraftContext.IsPlayerInventory(__instance)) return;
            __result = JournalGameplaySession.Current?.Peek(name, quality)?.Clone();
        }
    }
}
