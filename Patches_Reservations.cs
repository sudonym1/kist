using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ThorsKist
{
    // Each target has its own patch method so Harmony and the metadata verifier
    // resolve the same explicit overload. Helpers share policy, never targets.
    [HarmonyPatch]
    internal static class Patches_Reservations
    {
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems))]
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void SpendableCount(Inventory __instance, string name, int quality, bool matchWorldLevel, ref int __result)
        {
            if (!CraftContext.EvaluatingMaterials || !JournalReservationGuard.Busy(__instance)) return;
            long blocked = 0;
            foreach (var item in __instance.GetAllItems())
                if (JournalReservationGuard.Blocks(item) && (name == null || item.m_shared.m_name == name)
                    && (quality < 0 || quality == item.m_quality) && (!matchWorldLevel || item.m_worldLevel >= Game.m_worldLevel)) blocked += item.m_stack;
            __result = (int)System.Math.Max(0, (long)__result - blocked);
        }

        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool GridDrop(Inventory ___m_inventory, ItemDrop.ItemData item, ref bool __result)
            => ItemAllowed(___m_inventory, item, ref __result);

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.ConsumeItem))]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool ConsumeBase(ItemDrop.ItemData item, ref bool __result)
            => Result(!JournalReservationGuard.Blocks(item), ref __result);

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeItem))]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool ConsumePlayer(ItemDrop.ItemData item, ref bool __result)
            => Result(!JournalReservationGuard.Blocks(item), ref __result);

        [HarmonyPatch(typeof(Player), nameof(Player.EatFood))]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool Eat(ItemDrop.ItemData item, ref bool __result)
            => Result(!JournalReservationGuard.Blocks(item), ref __result);

        [HarmonyPatch(typeof(Player), "QueueEquipAction")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool QueueEquip(ItemDrop.ItemData item) => !JournalReservationGuard.Blocks(item);

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetAmmoItem))]
        [HarmonyPostfix]
        private static void Ammo(ref ItemDrop.ItemData __result)
        { if (JournalReservationGuard.Blocks(__result)) __result = null; }

        [HarmonyPatch(typeof(Inventory), "RemoveItem", new[] { typeof(ItemDrop.ItemData), typeof(int) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool RemoveQuantity(Inventory __instance, ItemDrop.ItemData item, int amount, ref bool __result) => Result(JournalReservationGuard.AllowQuantity(__instance, item, amount), ref __result);

        [HarmonyPatch(typeof(Inventory), "RemoveItem", new[] { typeof(ItemDrop.ItemData) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool RemoveWhole(Inventory __instance, ItemDrop.ItemData item, ref bool __result) => Result(JournalReservationGuard.AllowWhole(__instance, item), ref __result);

        [HarmonyPatch(typeof(Inventory), "RemoveOneItem")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool RemoveOne(ItemDrop.ItemData item, ref bool __result) => Result(!JournalReservationGuard.Blocks(item), ref __result);

        [HarmonyPatch(typeof(Inventory), "RemoveItem", new[] { typeof(int) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool RemoveIndex(Inventory __instance, int index, ref bool __result)
        {
            var items = __instance.GetAllItems();
            return Result(index < 0 || index >= items.Count || !JournalReservationGuard.Blocks(items[index]), ref __result);
        }

        [HarmonyPatch(typeof(Inventory), "RemoveItem", new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool RemoveName(Inventory __instance, string name, int itemQuality, bool worldLevelBased) => !JournalReservationGuard.BlocksName(__instance, name, itemQuality, worldLevelBased);

        [HarmonyPatch(typeof(Inventory), "RemoveAll")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool BulkRemoveAll(Inventory __instance) => !JournalReservationGuard.Busy(__instance);

        [HarmonyPatch(typeof(Inventory), "RemoveUnequipped")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool BulkRemoveUnequipped(Inventory __instance) => !JournalReservationGuard.Busy(__instance);

        [HarmonyPatch(typeof(Inventory), "SetHeight")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool BulkSetHeight(Inventory __instance) => !JournalReservationGuard.Busy(__instance);

        [HarmonyPatch(typeof(Inventory), "MoveItemToThis", new[] { typeof(Inventory), typeof(ItemDrop.ItemData) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool Move(Inventory __instance, ItemDrop.ItemData item) => !JournalReservationGuard.Busy(__instance) && !JournalReservationGuard.Blocks(item);

        [HarmonyPatch(typeof(Inventory), "MoveItemToThis", new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool MoveAmount(Inventory __instance, ItemDrop.ItemData item, ref bool __result) => ItemAllowed(__instance, item, ref __result);

        [HarmonyPatch(typeof(Inventory), "MoveAll")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool MoveAll(Inventory __instance, Inventory fromInventory) => !JournalReservationGuard.Busy(__instance) && !JournalReservationGuard.Busy(fromInventory);

        [HarmonyPatch(typeof(Inventory), "StackAll")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool StackAll(Inventory __instance, Inventory fromInventory, ref int __result)
        {
            if (!JournalReservationGuard.Busy(__instance) && !JournalReservationGuard.Busy(fromInventory)) return true;
            __result = 0; return false;
        }

        [HarmonyPatch(typeof(Inventory), "CanAddItem", new[] { typeof(ItemDrop.ItemData), typeof(int) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool CanAddItem(Inventory __instance, ItemDrop.ItemData item, ref bool __result) => ItemAllowed(__instance, item, ref __result);

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool AddItem(Inventory __instance, ItemDrop.ItemData item, ref bool __result) => ItemAllowed(__instance, item, ref __result);

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(Vector2i) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool AddAt(Inventory __instance, ItemDrop.ItemData item, ref bool __result) => ItemAllowed(__instance, item, ref __result);

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool AddAmountAt(Inventory __instance, ItemDrop.ItemData item, ref bool __result) => ItemAllowed(__instance, item, ref __result);

        [HarmonyPatch(typeof(Inventory), "CanAddItem", new[] { typeof(GameObject), typeof(int) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool CanAddPrefab(Inventory __instance, ref bool __result) => Result(!JournalReservationGuard.Busy(__instance), ref __result);

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(GameObject), typeof(int) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool AddPrefab(Inventory __instance, ref bool __result) => Result(!JournalReservationGuard.Busy(__instance), ref __result);

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(int), typeof(ItemDrop.ItemData), typeof(bool) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool AddHash(Inventory __instance, ref bool __result) => Result(!JournalReservationGuard.Busy(__instance), ref __result);

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(string), typeof(int), typeof(float), typeof(Vector2i), typeof(bool), typeof(int), typeof(int), typeof(long), typeof(string), typeof(Dictionary<string, string>), typeof(int), typeof(bool), typeof(bool), typeof(bool) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool AddLongName(Inventory __instance, ref bool __result) => Result(!JournalReservationGuard.Busy(__instance), ref __result);

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(int), typeof(int), typeof(float), typeof(Vector2i), typeof(bool), typeof(int), typeof(int), typeof(long), typeof(string), typeof(Dictionary<string, string>), typeof(int), typeof(bool), typeof(bool), typeof(bool) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool AddLongHash(Inventory __instance, ref bool __result) => Result(!JournalReservationGuard.Busy(__instance), ref __result);

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(long), typeof(string), typeof(bool), typeof(bool) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool Create(Inventory __instance, ref ItemDrop.ItemData __result)
        {
            if (!JournalReservationGuard.Busy(__instance)) return true;
            __result = null; return false;
        }

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(long), typeof(string), typeof(Vector2i), typeof(bool), typeof(bool), typeof(bool) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool CreateAt(Inventory __instance, ref ItemDrop.ItemData __result)
        {
            if (!JournalReservationGuard.Busy(__instance)) return true;
            __result = null; return false;
        }

        [HarmonyPatch(typeof(Humanoid), "EquipItem")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool EquipItem(ItemDrop.ItemData item, ref bool __result) => Result(!JournalReservationGuard.Blocks(item), ref __result);

        [HarmonyPatch(typeof(Humanoid), "DropItem")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool DropItem(ItemDrop.ItemData item, ref bool __result) => Result(!JournalReservationGuard.Blocks(item), ref __result);

        [HarmonyPatch(typeof(Humanoid), "UseItem")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool Use(ItemDrop.ItemData item) => !JournalReservationGuard.Blocks(item);

        [HarmonyPatch(typeof(InventoryGui), "SetupDragItem")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool Drag(ItemDrop.ItemData item) => !JournalReservationGuard.Blocks(item);

        [HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool RightClick(ItemDrop.ItemData item) => !JournalReservationGuard.Blocks(item);

        [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static bool Select(ItemDrop.ItemData item, ItemDrop.ItemData ___m_dragItem) => !JournalReservationGuard.Blocks(item) && !JournalReservationGuard.Blocks(___m_dragItem);

        [HarmonyPatch(typeof(Inventory), "Load", new[] { typeof(ZPackage) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static void Load(Inventory __instance) => JournalReservationGuard.Invalidate(__instance);

        [HarmonyPatch(typeof(Inventory), "Load", new[] { typeof(ZPackage), typeof(bool) })]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static void LoadTemporary(Inventory __instance) => JournalReservationGuard.Invalidate(__instance);

        [HarmonyPatch(typeof(Inventory), "MoveInventoryToGrave")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static void Grave(Inventory __instance, Inventory original)
        { JournalReservationGuard.Invalidate(original); JournalReservationGuard.Invalidate(__instance); }

        [HarmonyPatch(typeof(Player), "OnDeath")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static void OnDeath(Player __instance) => JournalReservationGuard.Invalidate(__instance.GetInventory());

        [HarmonyPatch(typeof(Player), "OnDestroy")]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        private static void OnDestroy(Player __instance) => JournalReservationGuard.Invalidate(__instance.GetInventory());

        private static bool Result(bool allowed, ref bool result)
        { if (allowed) return true; result = false; return false; }
        private static bool ItemAllowed(Inventory inventory, ItemDrop.ItemData item, ref bool result)
            => Result(!JournalReservationGuard.Busy(inventory) && !JournalReservationGuard.Blocks(item), ref result);
    }
}
