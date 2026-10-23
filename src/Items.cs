using System;
using System.Collections.Generic;

namespace SharedInventory;

// Client-side item rules. Nothing in this file is used by the database handlers.
internal static class Items
{
    public static ItemDrop.ItemData? Template(string itemId)
    {
        var prefab = ObjectDB.instance?.GetItemPrefab(itemId);
        return prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData : null;
    }

    public static ItemDrop.ItemData? Default(string itemId, int quality = 1)
    {
        var prefab = ObjectDB.instance?.GetItemPrefab(itemId);
        var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
        if (drop == null) return null;
        var item = drop.m_itemData.Clone();
        item.m_dropPrefab = prefab;
        item.m_stack = 1;
        item.m_quality = Math.Max(1, quality);
        item.m_variant = 0;
        item.m_worldLevel = 0;
        item.m_crafterID = 0;
        item.m_crafterName = "";
        item.m_customData = new Dictionary<string, string>();
        item.m_equipped = false;
        item.m_cheated = false;
        item.m_pickedUp = false;
        item.m_lastAttackTime = 0;
        item.m_lastProjectile = null;
        item.m_durability = item.GetMaxDurability();
        return item;
    }

    public static string Id(ItemDrop.ItemData item) => item.m_dropPrefab.name;

    // Match Inventory.AddItem's stacking rules. CanAddItem alone can overestimate
    // room by counting stacks with a different quality.
    public static long Room(Inventory inventory, ItemDrop.ItemData item)
    {
        var maximum = Math.Max(1, item.m_shared.m_maxStackSize);
        long room = (long)inventory.GetEmptySlots() * maximum;
        if (maximum == 1) return room;
        foreach (var existing in inventory.GetAllItems())
        {
            if (existing.m_shared.m_name == item.m_shared.m_name &&
                existing.m_quality == item.m_quality && existing.m_worldLevel == item.m_worldLevel)
                room += Math.Max(0, maximum - existing.m_stack);
        }
        return room;
    }

    public static long Count(Inventory inventory, string itemId)
    {
        long count = 0;
        foreach (var item in inventory.GetAllItems())
            if (item.m_dropPrefab != null && Id(item) == itemId) count += item.m_stack;
        return count;
    }

    // AddItem can partially succeed. Measure what actually entered the inventory.
    public static int Add(Inventory inventory, ItemDrop.ItemData item, int amount)
    {
        var id = Id(item);
        var before = Count(inventory, id);
        item.m_stack = amount;
        inventory.AddItem(item);
        return (int)Math.Max(0, Math.Min(amount, Count(inventory, id) - before));
    }
}
