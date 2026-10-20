using System.Collections.Generic;
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

        // Materials this craft pulled out of the vault, flushed to the server
        // once the whole cost has been paid.
        public static readonly List<CraftDraw> PendingDraws = new List<CraftDraw>();

        public static void FlushDraws()
        {
            if (PendingDraws.Count == 0) return;

            // Say what the vault actually paid. "It did not deduct" is otherwise
            // impossible to tell apart from "it was never asked to" -- the
            // materials having come from a pack, a backpack or a nearby chest
            // instead -- and guessing between those wastes an evening.
            System.Text.StringBuilder sb = new System.Text.StringBuilder("ThorsKist: Kist paid");
            for (int i = 0; i < PendingDraws.Count; i++)
                sb.Append(i == 0 ? " " : ", ").Append(PendingDraws[i].Amount)
                  .Append(" x ").Append(PendingDraws[i].Name);
            Main.Log.LogInfo(sb.ToString());

            KistClient.ReportCraftConsume(new List<CraftDraw>(PendingDraws));
            PendingDraws.Clear();
        }

        public static void Record(string name, int amount, int quality)
        {
            foreach (CraftDraw d in PendingDraws)
            {
                if (d.Name == name && d.Quality == quality) { d.Amount += amount; return; }
            }
            PendingDraws.Add(new CraftDraw { Name = name, Amount = amount, Quality = quality });
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
            // The single-ingredient branch pays without ConsumeResources, so
            // this is the only flush those crafts ever get. Harmless after a
            // normal craft: FlushDraws does nothing when nothing was drawn.
            CraftContext.FlushDraws();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
        [HarmonyPrefix] public static void ConsumePre() { CraftContext.Push(); }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
        [HarmonyPostfix]
        public static void ConsumePost()
        {
            CraftContext.Pop();
            // Tell the server what the vault actually paid, once, after the
            // whole recipe is settled -- not once per ingredient.
            CraftContext.FlushDraws();
        }
    }

    // ----------------------------------------------------------------------
    // The two widened answers
    // ----------------------------------------------------------------------

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems))]
    public static class Patches_CountItems
    {
        [HarmonyPostfix]
        public static void Postfix(Inventory __instance, string name, int quality, bool matchWorldLevel, ref int __result)
        {
            if (!CraftContext.Active) return;
            if (!CraftContext.IsPlayerInventory(__instance)) return;
            Inventory kist = KistClient.Kist;
            if (kist == null) return;

            CraftContext.EnterRaw();
            try { __result += KistStock.Count(kist, name, quality); }
            finally { CraftContext.ExitRaw(); }
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem),
        new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
    public static class Patches_RemoveItem
    {
        /// <summary>
        /// Spends the player's own stock first and lets vanilla remove exactly
        /// that much, then covers the shortfall from the Kist. Doing it in this
        /// order means a craft the player could already afford never touches the
        /// vault, so shared materials are only ever spent when actually needed.
        /// </summary>
        [HarmonyPrefix]
        public static void Prefix(Inventory __instance, string name, ref int amount, int itemQuality,
                                  bool worldLevelBased, out int __state)
        {
            __state = 0;
            if (!CraftContext.Active) return;
            if (!CraftContext.IsPlayerInventory(__instance)) return;
            if (KistClient.Kist == null) return;

            int localHave;
            CraftContext.EnterRaw();
            try { localHave = __instance.CountItems(name, itemQuality, worldLevelBased); }
            finally { CraftContext.ExitRaw(); }

            if (amount <= localHave) return;

            __state = amount - localHave;
            amount = localHave;
        }

        [HarmonyPostfix]
        public static void Postfix(Inventory __instance, string name, int itemQuality, int __state)
        {
            if (__state <= 0) return;
            Inventory kist = KistClient.Kist;
            if (kist == null) return;

            CraftContext.EnterRaw();
            try
            {
                int available = KistStock.Count(kist, name, itemQuality);
                int take = __state < available ? __state : available;
                if (take <= 0)
                {
                    // We were asked to cover a shortfall and the vault held
                    // nothing matching. This is the exact line that separates
                    // "the Kist refused to pay" from "the Kist was never asked"
                    // -- without it both look identical from in game.
                    Main.Log.LogWarning("ThorsKist: needed " + __state + " x " + name +
                        " (quality " + itemQuality + ") from the Kist, but it holds none that match -- nothing charged.");
                    return;
                }
                KistStock.Take(kist, name, take, itemQuality);
                CraftContext.Record(name, take, itemQuality);
            }
            finally { CraftContext.ExitRaw(); }
        }
    }
}
