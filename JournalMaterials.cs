using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    internal static class JournalMaterials
    {
        internal static bool Executing;
        internal static bool NativeCraft;
        private static int Local(string name, int quality = -1)
        {
            CraftContext.EnterRaw();
            try { return Player.m_localPlayer.GetInventory().GetAllItems().Where(i => i.m_shared.m_name == name && (quality < 0 || i.m_quality == quality) && !JournalReservationGuard.Blocks(i)).Sum(i => i.m_stack); }
            finally { CraftContext.ExitRaw(); }
        }
        internal static bool Requirements(Piece.Requirement[] resources, int quality, int multiplier, bool single, Action replay)
        {
            if (Executing || Player.m_localPlayer == null || Player.m_localPlayer.NoCostCheat()) return true;
            if (JournalReservationGuard.Busy(Player.m_localPlayer.GetInventory())) return false;
            var session = JournalGameplaySession.Current;
            if (session == null || !KistClient.CanCraft) return true;
            var needs = new List<(string Name, int Quality, int Quantity)>();
            var station = Player.m_localPlayer.GetCurrentCraftingStation();
            foreach (var req in resources)
            {
                if (req.m_resItem == null || (station != null ? station.m_upgrader != req.m_upgraderResource : req.m_upgraderResource)) continue;
                int amount = req.GetAmount(quality) * multiplier; if (amount <= 0) continue;
                string name = req.m_resItem.m_itemData.m_shared.m_name;
                if (single)
                {
                    for (int q = 0; q <= req.m_resItem.m_itemData.m_shared.m_maxQuality; q++)
                    {
                        int missing = Math.Max(0, amount - Local(name, q));
                        if (missing == 0) return true;
                        if (session.Count(name, q) >= missing) { needs.Add((name, q, missing)); break; }
                    }
                    if (needs.Count > 0) break;
                }
                else
                {
                    int missing = Math.Max(0, amount - Local(name));
                    if (session.Count(name) < missing) return false;
                    if (missing > 0) needs.Add((name, -1, missing));
                }
            }
            if (needs.Count == 0) return !single;
            if (!session.Procuring && session.Procure(needs, ItemCommandPurpose.Craft, () => Run(replay))) KistClient.Message("Withdrawing Kist materials...");
            return false;
        }
        private static void Run(Action action)
        { Executing = true; try { action(); } finally { Executing = false; } }
        internal static bool Station(Component station, MethodBase method, object[] arguments)
        {
            if (Executing || Player.m_localPlayer == null) return true;
            var user = arguments.OfType<Humanoid>().FirstOrDefault();
            if (user != Player.m_localPlayer) return true;
            // Station vanilla code sometimes ignores failed RemoveItem. Freeze
            // the interaction while any deposit reservation is outstanding.
            if (JournalReservationGuard.Busy(user.GetInventory())) return false;
            var session = JournalGameplaySession.Current;
            if (session == null || !KistClient.CanCraft) return true;
            if (arguments.OfType<ItemDrop.ItemData>().Any()) return true;
            if (method.Name == "Interact" && arguments.OfType<bool>().FirstOrDefault() && !(station is Fireplace)) return true;
            if (!PrivateArea.CheckAccess(station.transform.position)) return true;
            if (station is Smelter smelter)
            {
                if (method.Name == "OnAddOre" && (int)AccessTools.Method(typeof(Smelter), "GetQueueSize").Invoke(smelter, null) >= smelter.m_maxOre) return true;
                if (method.Name == "OnAddFuel" && (float)AccessTools.Method(typeof(Smelter), "GetFuel").Invoke(smelter, null) > smelter.m_maxFuel - 1) return true;
            }
            if (station is CookingStation cooking)
            {
                bool addingFuel = method.Name.IndexOf("Fuel", StringComparison.Ordinal) >= 0;
                if (!addingFuel && (bool)AccessTools.Method(typeof(CookingStation), "HaveDoneItem").Invoke(cooking, null)) return true;
                if (!addingFuel && (int)AccessTools.Method(typeof(CookingStation), "GetFreeSlot").Invoke(cooking, null) < 0) return true;
                if (!addingFuel && cooking.m_requireFire && !(bool)AccessTools.Method(typeof(CookingStation), "IsFireLit").Invoke(cooking, null)) return true;
                if (addingFuel && (float)AccessTools.Method(typeof(CookingStation), "GetFuel").Invoke(cooking, null) > cooking.m_maxFuel - 1) return true;
            }
            if (station is Fermenter fermenter && AccessTools.Method(typeof(Fermenter), "GetStatus").Invoke(fermenter, null).ToString() != "Empty") return true;
            if (station is Fireplace fire)
            {
                if (!fire.m_canRefill || fire.m_infiniteFuel) return true;
                var view = (ZNetView)AccessTools.Field(typeof(Fireplace), "m_nview").GetValue(fire);
                if (view == null || view.GetZDO() == null) return true;
                float heldFuel = view.GetZDO().GetFloat(ZDOVars.s_fuel);
                var flags = arguments.OfType<bool>().ToArray();
                if (fire.m_canTurnOff && flags.Length >= 2 && !flags[0] && !flags[1] && heldFuel > 0) return true;
                if (Mathf.CeilToInt(heldFuel) >= fire.m_maxFuel) return true;
            }
            bool fuel = method.Name.IndexOf("Fuel", StringComparison.Ordinal) >= 0 || station is Fireplace;
            var candidates = new List<ItemDrop>();
            if (fuel)
            {
                var field = AccessTools.Field(station.GetType(), "m_fuelItem");
                if (field?.GetValue(station) is ItemDrop item) candidates.Add(item);
            }
            else if (AccessTools.Field(station.GetType(), "m_conversion")?.GetValue(station) is System.Collections.IEnumerable conversions)
            {
                foreach (var conversion in conversions)
                    if (AccessTools.Field(conversion.GetType(), "m_from")?.GetValue(conversion) is ItemDrop item) candidates.Add(item);
            }
            // Preserve vanilla conversion order and prefer any locally usable
            // input before withdrawing one matching input from the journal.
            if (candidates.Any(i => Local(i.m_itemData.m_shared.m_name) > 0)) return true;
            var input = candidates.FirstOrDefault(i => session.Count(i.m_itemData.m_shared.m_name) > 0);
            if (input == null) return true;
            if (!session.Procuring)
            {
                var player = Player.m_localPlayer; var args = (object[])arguments.Clone();
                session.Procure(new[] { (input.m_itemData.m_shared.m_name, -1, 1) }, ItemCommandPurpose.Station, () =>
                {
                    if (station != null && player == Player.m_localPlayer && !player.IsDead()
                        && Vector3.Distance(station.transform.position, player.transform.position) <= 5f)
                        Run(() => method.Invoke(station, args));
                });
            }
            return false;
        }
    }
    [HarmonyPatch]
    internal static class Patches_JournalMaterials
    {
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Craft(InventoryGui __instance, Player player, Recipe ___m_craftRecipe, ItemDrop.ItemData ___m_craftUpgradeItem, bool ___m_multiCrafting, int ___m_multiCraftAmount)
        {
            if (___m_craftRecipe == null) return true;
            var recipe = ___m_craftRecipe; var upgrade = ___m_craftUpgradeItem;
            int multiplier = ___m_multiCrafting ? ___m_multiCraftAmount : 1;
            bool ready = JournalMaterials.Requirements(recipe.m_resources, upgrade == null ? 1 : upgrade.m_quality + 1, multiplier, recipe.m_requireOnlyOneIngredient, () =>
            {
                // A selection changed while awaiting materials cancels the
                // craft; delivered inputs stay available in the character.
                if (__instance != null && player == Player.m_localPlayer
                    && ReferenceEquals(AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").GetValue(__instance), recipe)
                    && ReferenceEquals(AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem").GetValue(__instance), upgrade))
                    AccessTools.Method(typeof(InventoryGui), "DoCrafting").Invoke(__instance, new object[] { player });
            });
            JournalMaterials.NativeCraft = ready;
            return ready;
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        [HarmonyFinalizer]
        private static void CraftFinished() { JournalMaterials.NativeCraft = false; }
        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Build(Player __instance, Piece piece, ref bool __result)
        {
            if (__instance != Player.m_localPlayer) return true;
            // Keep vanilla's single-click placement. Its successful placement
            // is followed by ConsumeResources, where the missing Kist share is
            // queued without waiting for journal delivery.
            bool ready = !JournalReservationGuard.Busy(__instance.GetInventory());
            if (!ready) __result = false;
            return ready;
        }
        private static bool Station(Component instance, MethodBase method, object[] args, ref bool result)
        {
            bool ready = JournalMaterials.Station(instance, method, args);
            if (!ready) result = false;
            return ready;
        }
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Station0(Component __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
            => Station(__instance, __originalMethod, __args, ref __result);
        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Station1(Component __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
            => Station(__instance, __originalMethod, __args, ref __result);
        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Station2(Component __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
            => Station(__instance, __originalMethod, __args, ref __result);
        [HarmonyPatch(typeof(CookingStation), "OnAddFoodSwitch")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Station3(Component __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
            => Station(__instance, __originalMethod, __args, ref __result);
        [HarmonyPatch(typeof(CookingStation), "Interact")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Station4(Component __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
            => Station(__instance, __originalMethod, __args, ref __result);
        [HarmonyPatch(typeof(Fireplace), "Interact")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Station5(Component __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
            => Station(__instance, __originalMethod, __args, ref __result);
        [HarmonyPatch(typeof(Fermenter), "Interact")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Station6(Component __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
            => Station(__instance, __originalMethod, __args, ref __result);
    }
}
