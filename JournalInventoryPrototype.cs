using System;
using System.Linq;
using ThorsKist.Sync;

namespace ThorsKist
{
    // Exact-reference reservation adapter. M7 guards vanilla mutations; the
    // live session owner and remaining coverage gates still precede activation.
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
            int remaining = stack.m_stack - quantity;
            var expected = stack.Clone(); expected.m_stack = remaining;
            var bytes = remaining == 0 ? null : Capture(expected);
            if (!JournalReservationGuard.RemoveVerified(inventory, stack, quantity)) return false;
            return remaining == 0 ? !ContainsReference(stack)
                : ContainsReference(stack) && Capture(stack).SequenceEqual(bytes);
        }
    }
}
