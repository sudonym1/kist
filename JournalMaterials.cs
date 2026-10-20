using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ThorsKist
{
    internal static class JournalMaterials
    {
        internal static bool NativeCraft;
        // Vanilla charges ingredients only after producing the craft result.
        // Count the real pack, let vanilla remove its share, and pay any missing
        // share from the Kist without materializing ingredients in inventory.
        internal static void PayCraft(Inventory inventory, string name, int amount, int quality, bool worldLevelBased)
        {
            if (!NativeCraft || amount <= 0 || !CraftContext.IsPlayerInventory(inventory) || !KistClient.CanCraft) return;
            int local = inventory.GetAllItems().Where(i => i.m_shared.m_name == name && (quality < 0 || i.m_quality == quality)
                && (!worldLevelBased || i.m_worldLevel >= Game.m_worldLevel) && !JournalReservationGuard.Blocks(i)).Sum(i => i.m_stack);
            int missing = Math.Max(0, amount - local);
            if (missing > 0 && !JournalGameplaySession.Current.PayCraft(name, quality, missing))
                Main.Log.LogWarning("Kist crafting payment unavailable after optimistic craft");
        }
        private static int Local(string name, int quality = -1)
        {
            CraftContext.EnterRaw();
            try { return Player.m_localPlayer.GetInventory().GetAllItems().Where(i => i.m_shared.m_name == name && (quality < 0 || i.m_quality == quality) && !JournalReservationGuard.Blocks(i)).Sum(i => i.m_stack); }
            finally { CraftContext.ExitRaw(); }
        }
        internal static bool Station(Component station, MethodBase method, object[] arguments, out bool handled)
        {
            handled = false;
            if (Player.m_localPlayer == null) return true;
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
                if (method.Name == "Interact" && AccessTools.Field(typeof(CookingStation), "m_addFoodSwitch").GetValue(cooking) is Switch) return true;
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
                if (flags.Length > 0 && flags[0] && (fire.m_holdRepeatInterval <= 0
                    || Time.time - (float)AccessTools.Field(typeof(Fireplace), "m_lastUseTime").GetValue(fire) < fire.m_holdRepeatInterval)) return true;
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
            if (session.AccessEnabled && session.PendingImmediatePayments < 48)
            {
                var item = session.PeekAvailable(input.m_itemData.m_shared.m_name);
                if (item != null && JournalStationInputs.Insert(station, fuel, item))
                {
                    handled = true;
                    // The station owner applies first, then its success reply
                    // triggers payment. There is no inventory delivery/replay.
                }
            }
            return false;
        }
    }
    [HarmonyPatch]
    internal static class Patches_JournalMaterials
    {
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static void CraftPayment(Inventory __instance, string name, int amount, int itemQuality, bool worldLevelBased)
            => JournalMaterials.PayCraft(__instance, name, amount, itemQuality, worldLevelBased);
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static bool Craft(Player player, out bool __state)
        {
            __state = JournalMaterials.NativeCraft;
            if (player == Player.m_localPlayer && JournalReservationGuard.Busy(player.GetInventory())) return false;
            JournalMaterials.NativeCraft = player == Player.m_localPlayer && KistClient.CanCraft;
            return true;
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        [HarmonyFinalizer]
        private static void CraftFinished(bool __state) { JournalMaterials.NativeCraft = __state; }
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
            bool ready = JournalMaterials.Station(instance, method, args, out bool handled);
            if (!ready) result = handled;
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
