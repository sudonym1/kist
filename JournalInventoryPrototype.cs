using System;
using System.Linq;
using ThorsKist.Sync;

namespace ThorsKist
{
    // M1 prototype, deliberately not wired into the legacy escrow/RPC protocol.
    // M5 must intercept every mutation path before enabling reservations live.
    internal sealed class JournalInventoryPrototype : IReservationInventory<ItemDrop.ItemData>
    {
        private readonly Inventory inventory;
        public JournalInventoryPrototype(Inventory inventory) { this.inventory = inventory; }
        public bool ContainsReference(ItemDrop.ItemData stack) => inventory.GetAllItems().Any(i => ReferenceEquals(i, stack));
        public int Quantity(ItemDrop.ItemData stack) => stack.m_stack;
        public byte[] Capture(ItemDrop.ItemData stack)
        {
            var package = new ZPackage();
            stack.Save(package);
            return package.GetArray();
        }
        public bool RemoveExact(ItemDrop.ItemData stack, int quantity)
        {
            if (!ContainsReference(stack) || quantity <= 0 || stack.m_stack < quantity) return false;
            inventory.RemoveItem(stack, quantity);
            return true; // Reservation engine verifies exact before/after quantity.
        }
    }
}
