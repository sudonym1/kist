using System;
using System.Collections.Generic;
using SharedInventory;

// Small game/transport doubles. Tests compile the actual database, client and item rules.
namespace UnityEngine
{
    public struct Vector3
    {
        public static Vector3 up => default;
        public static Vector3 operator +(Vector3 a, Vector3 b) => default;
    }
    public struct Quaternion { public static Quaternion identity => default; }
    public sealed class Transform { public Vector3 position; }
}

public sealed class GameObject
{
    public string name = "";
    public ItemDrop Drop = null!;
    public T GetComponent<T>() where T : class => (Drop as T)!;
}

public sealed class ItemDrop
{
    public ItemData m_itemData = null!;
    public static readonly List<ItemData> Dropped = new List<ItemData>();
    public static void DropItem(ItemData item, int amount, UnityEngine.Vector3 p, UnityEngine.Quaternion q)
        => Dropped.Add(item);
    public sealed class SharedData
    {
        public string m_name = "";
        public int m_maxStackSize = 50;
    }
    public sealed class ItemData
    {
        public GameObject m_dropPrefab = null!;
        public SharedData m_shared = new SharedData();
        public int m_stack = 1, m_quality = 1, m_variant, m_worldLevel;
        public long m_crafterID;
        public string m_crafterName = "";
        public bool m_equipped, m_cheated, m_pickedUp;
        public float m_lastAttackTime, m_durability = 100;
        public object? m_lastProjectile;
        public Dictionary<string, string> m_customData = new Dictionary<string, string>();
        public ItemData Clone()
        {
            var item = (ItemData)MemberwiseClone();
            item.m_customData = new Dictionary<string, string>(m_customData);
            return item;
        }
        public float GetMaxDurability() => 100;
    }
}

public sealed class ObjectDB
{
    public static ObjectDB instance = new ObjectDB();
    public readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
    public GameObject? GetItemPrefab(string id) => Prefabs.TryGetValue(id, out var p) ? p : null;
}

public sealed class Inventory
{
    public int Slots = 4;
    public int AddLimit = int.MaxValue;
    private readonly List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>();
    public List<ItemDrop.ItemData> GetAllItems() => items;
    public int GetEmptySlots() => Math.Max(0, Slots - items.Count);
    public bool ContainsItem(ItemDrop.ItemData item) => items.Contains(item);
    public bool RemoveItem(ItemDrop.ItemData item) => items.Remove(item);
    public bool CanAddItem(ItemDrop.ItemData item, int amount) => Items.Room(this, item) >= amount;
    public bool AddItem(ItemDrop.ItemData item)
    {
        int remaining = Math.Min(item.m_stack, AddLimit);
        int target = item.m_stack;
        foreach (var existing in items)
        {
            if (existing.m_shared.m_name != item.m_shared.m_name || existing.m_quality != item.m_quality ||
                existing.m_worldLevel != item.m_worldLevel) continue;
            var n = Math.Min(remaining, Math.Max(0, existing.m_shared.m_maxStackSize - existing.m_stack));
            existing.m_stack += n;
            remaining -= n;
        }
        if (remaining > 0 && GetEmptySlots() > 0)
        {
            var copy = item.Clone();
            copy.m_stack = remaining;
            items.Add(copy);
            remaining = 0;
        }
        var added = Math.Min(target, AddLimit) - remaining;
        item.m_stack = target - added;
        return added == target;
    }
}

public sealed class Player
{
    public static Player m_localPlayer = new Player();
    public Inventory Inventory = new Inventory();
    public bool Dead;
    public UnityEngine.Transform transform = new UnityEngine.Transform();
    public Inventory GetInventory() => Inventory;
    public bool IsDead() => Dead;
    public void UnequipItem(ItemDrop.ItemData item, bool effects) => item.m_equipped = false;
}

namespace SharedInventory
{
    internal sealed class RpcApi
    {
        internal sealed class Call
        {
            public string Method = "", Item = "";
            public long Id, Amount;
        }
        public bool Connected = true;
        public readonly List<Call> Calls = new List<Call>();
        public void List(long id) => Calls.Add(new Call { Method = "List", Id = id });
        public void Deposit(long id, string item, long amount)
            => Calls.Add(new Call { Method = "Deposit", Id = id, Item = item, Amount = amount });
        public void Withdrawal(long id, string item, long amount)
            => Calls.Add(new Call { Method = "Withdrawal", Id = id, Item = item, Amount = amount });
    }
}
