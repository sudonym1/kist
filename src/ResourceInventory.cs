using System;
using System.Collections.Generic;

namespace SharedInventory;

// The same view of materials serves requirement checks, alternative ingredient
// selection, and consumption. Game code still chooses amounts and qualities.
internal sealed class ResourceInventory
{
    private readonly Client client;
    private ObjectDB? indexedDatabase;
    private int indexedItems = -1;
    private readonly Dictionary<string, List<string>> prefabs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    private readonly Dictionary<string, long> consuming = new Dictionary<string, long>(StringComparer.Ordinal);
    private int depth;

    public ResourceInventory(Client client) => this.client = client;

    private bool Applies(Inventory inventory) => client.Ready && inventory == Player.m_localPlayer.GetInventory();

    private IEnumerable<string> Matching(string name, int quality, bool matchWorldLevel)
    {
        // Shared items are normalized to world level zero on withdrawal too.
        if (matchWorldLevel && Game.m_worldLevel > 0) yield break;
        var database = ObjectDB.instance;
        if (database == null) yield break;
        if (indexedDatabase != database || indexedItems != database.m_items.Count)
        {
            prefabs.Clear();
            foreach (var prefab in database.m_items)
            {
                var item = prefab.GetComponent<ItemDrop>();
                if (item == null) continue;
                var itemName = item.m_itemData.m_shared.m_name;
                if (!prefabs.TryGetValue(itemName, out var ids)) prefabs[itemName] = ids = new List<string>();
                ids.Add(prefab.name);
            }
            indexedDatabase = database;
            indexedItems = database.m_items.Count;
        }
        if (!prefabs.TryGetValue(name, out var matching)) yield break;
        foreach (var id in matching)
            if ((quality < 0 || client.Quality(id) == quality) && Available(id) > 0) yield return id;
    }

    private long Available(string id) => Math.Max(0, client.Available(id) - (consuming.TryGetValue(id, out var used) ? used : 0));

    public int Count(Inventory inventory, string name, int quality, bool matchWorldLevel)
    {
        long count = inventory.CountItems(name, quality, matchWorldLevel);
        if (Applies(inventory))
            foreach (var id in Matching(name, quality, matchWorldLevel))
                count += Math.Min(Available(id), int.MaxValue - count);
        return (int)count;
    }

    public bool Have(Inventory inventory, string name, bool matchWorldLevel) => Count(inventory, name, -1, matchWorldLevel) > 0;

    public ItemDrop.ItemData? Get(Inventory inventory, string name, int quality, bool isPrefabName)
    {
        var local = inventory.GetItem(name, quality, isPrefabName);
        if (local != null || !Applies(inventory)) return local;
        if (isPrefabName)
        {
            if (Game.m_worldLevel > 0 || Available(name) == 0 || (quality >= 0 && client.Quality(name) != quality)) return null;
            return Items.Default(name, client.Quality(name));
        }
        foreach (var id in Matching(name, quality, true)) return Items.Default(id, client.Quality(id));
        return null;
    }

    public IDisposable Begin()
    {
        depth++;
        return new Scope(this);
    }

    public void Remove(Inventory inventory, string name, int amount, int quality, bool worldLevelBased)
    {
        if (!Applies(inventory)) { inventory.RemoveItem(name, amount, quality, worldLevelBased); return; }
        using var scope = Begin();
        var before = inventory.CountItems(name, quality, worldLevelBased);
        inventory.RemoveItem(name, amount, quality, worldLevelBased);
        var missing = amount - (before - inventory.CountItems(name, quality, worldLevelBased));
        foreach (var id in Matching(name, quality, worldLevelBased))
        {
            if (missing <= 0) break;
            var take = Math.Min(missing, Available(id));
            consuming[id] = (consuming.TryGetValue(id, out var used) ? used : 0) + take;
            missing -= (int)take;
        }
    }

    private sealed class Scope : IDisposable
    {
        private ResourceInventory? owner;
        public Scope(ResourceInventory owner) => this.owner = owner;
        public void Dispose()
        {
            var current = owner;
            if (current == null) return;
            owner = null;
            if (--current.depth != 0) return;
            var amounts = new Dictionary<string, long>(current.consuming, StringComparer.Ordinal);
            current.consuming.Clear();
            current.client.Consume(amounts);
        }
    }
}
