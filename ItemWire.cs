using System.Collections.Generic;

namespace ThorsKist
{
    /// <summary>
    /// Item transport. Items always travel in the exact byte layout vanilla uses
    /// for a container, so quality, variant, durability, crafter and custom data
    /// survive the trip without this mod having to know what any of them mean --
    /// and a game update that changes the item format changes both ends at once.
    /// </summary>
    public static class ItemWire
    {
        // Matches the constant Inventory.Save writes. Read back through the same
        // vanilla path, so it is only ever compared against itself.
        private const int InventoryVersion = 109;

        public static void WriteItems(ZPackage pkg, List<ItemDrop.ItemData> items)
        {
            pkg.Write(InventoryVersion);
            pkg.Write((ushort)items.Count);
            for (int i = 0; i < items.Count; i++)
                items[i].Save(pkg);
        }

        public static void WriteItem(ZPackage pkg, ItemDrop.ItemData item)
        {
            pkg.Write(InventoryVersion);
            pkg.Write((ushort)1);
            item.Save(pkg);
        }

        /// <summary>
        /// Decodes items written by WriteItems into a throwaway inventory and
        /// hands back the list. The scratch inventory is deliberately huge so a
        /// decode never silently drops an item for want of a slot.
        /// </summary>
        public static List<ItemDrop.ItemData> ReadItems(ZPackage pkg)
        {
            Inventory scratch = new Inventory("tk_scratch", null, 8, 256);
            scratch.Load(pkg);
            // Copy out: the scratch inventory is discarded, but the ItemData
            // instances it built are what we want to keep.
            return new List<ItemDrop.ItemData>(scratch.GetAllItems());
        }
    }
}
