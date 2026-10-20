using System;
using System.Collections.Generic;
using System.Linq;

namespace ThorsKist
{
    // Game-thread-only registry. Normal legacy gameplay registers no inventories.
    // The future journal session owner must connect Invalidated to StopRequests
    // and self-suspension. Registering scratch adapters exercises the same hooks.
    internal static class JournalReservationGuard
    {
        private static readonly Dictionary<Inventory, JournalCharacterInventory> owners = new Dictionary<Inventory, JournalCharacterInventory>();
        private static int cursor;
        internal static void Register(Inventory inventory, JournalCharacterInventory adapter)
        {
            if (owners.ContainsKey(inventory)) throw new InvalidOperationException("Inventory already has a journal owner");
            if (owners.Count >= 16) throw new InvalidOperationException("Too many journal inventory owners");
            owners.Add(inventory, adapter);
        }
        internal static void Unregister(Inventory inventory, JournalCharacterInventory adapter)
        { if (owners.TryGetValue(inventory, out var old) && ReferenceEquals(old, adapter)) owners.Remove(inventory); }
        internal static bool Busy(Inventory inventory) => inventory != null && owners.TryGetValue(inventory, out var adapter) && adapter.HasReservations;
        internal static bool Blocks(ItemDrop.ItemData item) => item != null && owners.Values.Any(a => a.IsBlocked(item));
        internal static bool BlocksName(Inventory inventory, string name, int quality, bool worldLevel)
            => Busy(inventory) && inventory.GetAllItems().Any(i => Blocks(i) && i.m_shared.m_name == name
                && (quality < 0 || quality == i.m_quality) && (!worldLevel || i.m_worldLevel >= Game.m_worldLevel));
        internal static void Invalidate(Inventory inventory)
        { if (inventory != null && owners.TryGetValue(inventory, out var adapter)) adapter.Invalidate(); }
        internal static void Poll()
        {
            if (owners.Count == 0) return;
            // One adapter and one reserved snapshot per frame. Direct list edits
            // by mods are detected even when Inventory.Changed was bypassed.
            var adapter = owners.Values.ElementAt(cursor++ % owners.Count);
            if (cursor == int.MaxValue) cursor = 0;
            adapter.ValidateNextReservation();
        }
        internal static void Reset()
        {
            foreach (var adapter in owners.Values.ToArray()) adapter.Invalidate();
            owners.Clear(); cursor = 0;
        }

        // A one-use permit for precisely the verified object removal. There is
        // no broad 'ignore reservations' scope for reentrant inventory callbacks.
        private sealed class Permit
        {
            internal Inventory Inventory;
            internal ItemDrop.ItemData Item;
            internal int Quantity;
            internal bool Entered, WholeConsumed, WholeExpected;
        }
        private static Permit permit;
        internal static bool RemoveVerified(Inventory inventory, ItemDrop.ItemData item, int quantity)
        {
            if (permit != null) return false;
            permit = new Permit { Inventory = inventory, Item = item, Quantity = quantity, WholeExpected = quantity == item.m_stack };
            try { return inventory.RemoveItem(item, quantity); }
            finally { permit = null; }
        }
        internal static bool AllowQuantity(Inventory inventory, ItemDrop.ItemData item, int quantity)
        {
            if (!Blocks(item)) return true;
            if (permit == null || permit.Entered || !ReferenceEquals(permit.Inventory, inventory)
                || !ReferenceEquals(permit.Item, item) || permit.Quantity != quantity) return false;
            permit.Entered = true; return true;
        }
        internal static bool AllowWhole(Inventory inventory, ItemDrop.ItemData item)
        {
            if (!Blocks(item)) return true;
            // Vanilla RemoveItem(item, amount) delegates here for a whole stack.
            if (permit == null || !permit.Entered || !permit.WholeExpected || permit.WholeConsumed || !ReferenceEquals(permit.Inventory, inventory)
                || !ReferenceEquals(permit.Item, item) || permit.Quantity != item.m_stack) return false;
            permit.WholeConsumed = true; return true;
        }
    }
}
