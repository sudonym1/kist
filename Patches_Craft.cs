using HarmonyLib;

namespace ThorsKist
{
    /// <summary>
    /// Crafting out of the Kist.
    ///
    /// Vanilla asks two questions about materials -- "how many have I got?"
    /// (Inventory.CountItems) and "take this many" (Inventory.RemoveItem) -- and
    /// every recipe, upgrade, repair and build cost goes through them. So rather
    /// than reimplementing requirement logic, this widens those two answers, and
    /// only while the game is actually asking them about a craft. Outside that
    /// window the player's inventory reads exactly as it always did, which is
    /// what keeps weight, hotbars and loot screens honest.
    /// </summary>
    public static class CraftContext
    {
        private static int _depth;
        private static int _reentry;
        // Raw counting suppresses Kist widening, but must still exclude
        // reserved local stacks when choosing who pays a material cost.
        internal static bool EvaluatingMaterials => _depth > 0;

        /// <summary>True while vanilla is evaluating a recipe's material cost.</summary>
        public static bool Active
        {
            get { return EvaluatingMaterials && _reentry == 0 && KistClient.CanCraft; }
        }

        public static void Push() { _depth++; }
        public static void Pop() { if (_depth > 0) _depth--; }

        /// <summary>Suppresses the widening while this mod does its own counting.</summary>
        public static void EnterRaw() { _reentry++; }
        public static void ExitRaw() { if (_reentry > 0) _reentry--; }

        public static bool IsPlayerInventory(Inventory inv)
        {
            Player p = Player.m_localPlayer;
            return p != null && inv != null && inv == p.GetInventory();
        }

    }

    // ----------------------------------------------------------------------
    // Where the craft window opens and closes
    // ----------------------------------------------------------------------

    [HarmonyPatch]
    public static class Patches_CraftScope
    {
        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirementItems))]
        [HarmonyPrefix] public static void HaveItemsPre() { CraftContext.Push(); }
        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirementItems))]
        [HarmonyPostfix] public static void HaveItemsPost() { CraftContext.Pop(); }

        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
        [HarmonyPrefix] public static void HavePiecePre() { CraftContext.Push(); }
        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
        [HarmonyPostfix] public static void HavePiecePost() { CraftContext.Pop(); }

        [HarmonyPatch(typeof(Player), nameof(Player.GetFirstRequiredItem))]
        [HarmonyPrefix] public static void FirstReqPre() { CraftContext.Push(); }
        [HarmonyPatch(typeof(Player), nameof(Player.GetFirstRequiredItem))]
        [HarmonyPostfix] public static void FirstReqPost() { CraftContext.Pop(); }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
        [HarmonyPrefix] public static void SetupReqPre() { CraftContext.Push(); }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
        [HarmonyPostfix] public static void SetupReqPost() { CraftContext.Pop(); }

        // The whole crafting window, not just ConsumeResources. Two reasons:
        // a recipe flagged m_requireOnlyOneIngredient pays with a direct
        // Inventory.RemoveItem and never reaches ConsumeResources at all, so
        // without this the Kist is counted toward it and never charged; and a
        // foreign transpiler can rewrite DoCrafting's body but cannot stop a
        // prefix running, so opening the scope here keeps payment widened no
        // matter which branch inside pays.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void DoCraftingPre() { CraftContext.Push(); }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        [HarmonyPostfix]
        public static void DoCraftingPost()
        {
            CraftContext.Pop();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
        [HarmonyPrefix] public static void ConsumePre() { CraftContext.Push(); }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
        [HarmonyPostfix]
        public static void ConsumePost()
        {
            CraftContext.Pop();
        }
    }

    // ----------------------------------------------------------------------
    // Widen requirement counts; immediate payments live in JournalMaterials.
    // ----------------------------------------------------------------------

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems))]
    public static class Patches_CountItems
    {
        [HarmonyPostfix]
        public static void Postfix(Inventory __instance, string name, int quality, bool matchWorldLevel, ref int __result)
        {
            if (!CraftContext.Active) return;
            if (!CraftContext.IsPlayerInventory(__instance)) return;
            var session = JournalGameplaySession.Current;
            if (session != null) { __result += session.Count(name, quality); return; }

        }
    }

}
