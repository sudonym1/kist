using HarmonyLib;

namespace ThorsKist
{
    /// <summary>
    /// Feeding smelters, kilns, fireplaces, cooking stations and fermenters out
    /// of the Kist.
    ///
    /// Stations do not go through the recipe machinery, so none of the crafting
    /// work reaches them. They ask the player's inventory two different ways and
    /// each needs its own answer widened:
    ///
    ///   Fuel  -- HaveItem(name) to check, then RemoveItem(name, 1) to take.
    ///            That remove is the string overload the Kist already
    ///            intercepts, so widening the CHECK is the whole job.
    ///
    ///   Ore   -- GetItem(name) to find a real stack, then RemoveItem(item, 1)
    ///   and     or RemoveOneItem(item) to take from that exact instance. Both
    ///   food    of those begin with m_inventory.Contains(item), so there is no
    ///            faking it: the item has to genuinely be in the player's
    ///            inventory. One is moved there from the Kist on the spot.
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
        /// The distinction is load bearing. CookingStation.CanUseItems calls
        /// FindCookableItem, which calls GetItem -- so without this, walking up
        /// to a cooking station and looking at it would quietly pull raw meat
        /// out of the Kist and into your pack. Nothing may be moved on a
        /// question; only on a commitment.
        /// </summary>
        public static bool Committing { get { return _commit > 0 && Active; } }

        public static void Enter(bool committing)
        {
            _depth++;
            if (committing) _commit++;
            CraftContext.Push();
        }

        public static void Exit(bool committing)
        {
            CraftContext.Pop();
            if (committing && _commit > 0) _commit--;
            if (_depth > 0) _depth--;
            // Stations never reach Player.ConsumeResources, so the crafting
            // path's flush never fires for them. Report what the Kist paid once
            // the outermost station call is done -- not per nested call, or a
            // single interaction would send several messages.
            if (_depth == 0) CraftContext.FlushDraws();
        }

        /// <summary>
        /// Safety net, called once a frame.
        ///
        /// A Harmony postfix does not run if the original method throws, so one
        /// exception inside a station -- ours or anyone else's -- would leave
        /// the scope open for the rest of the session. An open scope means
        /// GetItem would move items out of the Kist during calls that have
        /// nothing to do with a station, which is exactly the kind of silent
        /// wrongness this mod should not have. A station interaction is always
        /// finished within the frame it began, so anything still open at the
        /// start of the next frame leaked and is forced shut.
        /// </summary>
        public static void FrameReset()
        {
            if (_depth == 0) return;

            Main.Log.LogWarning("ThorsKist: station scope was left open (depth " + _depth +
                                ") -- a station call threw. Forcing it shut.");
            while (_depth > 0) { CraftContext.Pop(); _depth--; }
            _commit = 0;
            CraftContext.FlushDraws();
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
        public static void SmelterOrePre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        [HarmonyPostfix]
        public static void SmelterOrePost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void SmelterFuelPre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        [HarmonyPostfix]
        public static void SmelterFuelPost() { StationScope.Exit(true); }

        // CanUseItems decides whether the station offers the interaction at all.
        // Without it the prompt never appears and OnAdd* is never reached.
        [HarmonyPatch(typeof(Smelter), nameof(Smelter.CanUseItems))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void SmelterCanPre() { StationScope.Enter(false); }
        [HarmonyPatch(typeof(Smelter), nameof(Smelter.CanUseItems))]
        [HarmonyPostfix]
        public static void SmelterCanPost() { StationScope.Exit(false); }

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FireplaceInteractPre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
        [HarmonyPostfix]
        public static void FireplaceInteractPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.UseItem))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FireplaceUsePre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.UseItem))]
        [HarmonyPostfix]
        public static void FireplaceUsePost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.CanUseItems))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FireplaceCanPre() { StationScope.Enter(false); }
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.CanUseItems))]
        [HarmonyPostfix]
        public static void FireplaceCanPost() { StationScope.Exit(false); }

        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookFuelPre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        [HarmonyPostfix]
        public static void CookFuelPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(CookingStation), "OnAddFoodSwitch")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookFoodPre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(CookingStation), "OnAddFoodSwitch")]
        [HarmonyPostfix]
        public static void CookFoodPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.UseItem))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookUsePre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.UseItem))]
        [HarmonyPostfix]
        public static void CookUsePost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.CanUseItems))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookCanPre() { StationScope.Enter(false); }
        [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.CanUseItems))]
        [HarmonyPostfix]
        public static void CookCanPost() { StationScope.Exit(false); }

        // The cooking station finds its food in OnInteract, before UseItem or
        // the food switch is ever reached, so the scope has to be open there.
        [HarmonyPatch(typeof(CookingStation), "OnInteract")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void CookInteractPre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(CookingStation), "OnInteract")]
        [HarmonyPostfix]
        public static void CookInteractPost() { StationScope.Exit(true); }

        // Same for the fermenter: Interact does the looking, AddItem only the
        // taking.
        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Interact))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FermentInteractPre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Interact))]
        [HarmonyPostfix]
        public static void FermentInteractPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Fermenter), "AddItem")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FermentAddPre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(Fermenter), "AddItem")]
        [HarmonyPostfix]
        public static void FermentAddPost() { StationScope.Exit(true); }

        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.UseItem))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void FermentUsePre() { StationScope.Enter(true); }
        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.UseItem))]
        [HarmonyPostfix]
        public static void FermentUsePost() { StationScope.Exit(true); }
    }

    // ----------------------------------------------------------------------
    // The widened answers a station asks for
    // ----------------------------------------------------------------------

    /// <summary>
    /// "Do I have any of this?" -- the gate on every fuel path, and on the
    /// CanUseItems prompt. Saying yes is enough on its own for fuel: the take
    /// that follows is RemoveItem(name, 1), which the crafting patch already
    /// splits between the player and the Kist.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem), new[] { typeof(string), typeof(bool) })]
    public static class Patches_HaveItem
    {
        [HarmonyPostfix]
        public static void Postfix(Inventory __instance, string name, ref bool __result)
        {
            if (__result) return;
            if (!StationScope.Active) return;
            if (!CraftContext.IsPlayerInventory(__instance)) return;
            Inventory kist = KistClient.Kist;
            if (kist == null) return;

            CraftContext.EnterRaw();
            try { if (kist.CountItems(name) > 0) __result = true; }
            finally { CraftContext.ExitRaw(); }
        }
    }

    /// <summary>
    /// The smelter's own "is there anything here I could smelt?" check, which
    /// asks about every conversion at once rather than one name at a time.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItemsByName))]
    public static class Patches_CountItemsByName
    {
        [HarmonyPostfix]
        public static void Postfix(Inventory __instance, string[] names, int quality, ref int __result)
        {
            if (!StationScope.Active) return;
            if (!CraftContext.IsPlayerInventory(__instance)) return;
            if (names == null) return;
            Inventory kist = KistClient.Kist;
            if (kist == null) return;

            CraftContext.EnterRaw();
            try
            {
                foreach (string n in names)
                    if (!string.IsNullOrEmpty(n)) __result += kist.CountItems(n, quality);
            }
            finally { CraftContext.ExitRaw(); }
        }
    }

    /// <summary>
    /// "Hand me the stack." The ore and food paths take from the exact instance
    /// this returns, and both check m_inventory.Contains first, so the only
    /// honest answer is to move one across from the Kist and hand back the item
    /// that is now genuinely in the player's hands.
    ///
    /// Exactly one is moved, never the whole stack: the station consumes one per
    /// interaction, so moving one leaves nothing stranded in the player's pack
    /// afterwards.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetItem),
        new[] { typeof(string), typeof(int), typeof(bool) })]
    public static class Patches_GetItem
    {
        [HarmonyPostfix]
        public static void Postfix(Inventory __instance, string name, int quality, bool isPrefabName,
                                   ref ItemDrop.ItemData __result)
        {
            if (__result != null) return;               // the player already has one
            if (!StationScope.Active) return;
            // The Kist is keyed on the display token, which is what a station
            // asks with. A prefab-name lookup is somebody else's question.
            if (isPrefabName) return;
            if (!CraftContext.IsPlayerInventory(__instance)) return;
            Inventory kist = KistClient.Kist;
            if (kist == null) return;

            CraftContext.EnterRaw();
            try
            {
                ItemDrop.ItemData source = kist.GetItem(name, quality);
                if (source == null) return;

                // Only a question -- "could this station be used?" Answer it
                // with the Kist's own stack and move nothing. Every caller in
                // this path does no more than check the result for null, and
                // the two takes that could follow both begin with
                // m_inventory.Contains, so a borrowed item fails safe rather
                // than being spent twice.
                if (!StationScope.Committing) { __result = source; return; }

                ItemDrop.ItemData one = source.Clone();
                one.m_stack = 1;
                if (!__instance.CanAddItem(one, 1))
                {
                    KistClient.Message("$msg_yourinventoryisfull");
                    return;
                }
                if (!__instance.AddItem(one)) return;

                kist.RemoveItem(name, 1, quality);
                CraftContext.Record(name, 1, quality);

                // Hand back the instance that actually landed in the inventory.
                // AddItem is free to merge into a stack rather than keep ours,
                // and the caller is about to check Contains on whatever it gets.
                __result = __instance.GetItem(name, quality);
            }
            finally { CraftContext.ExitRaw(); }
        }
    }
}
