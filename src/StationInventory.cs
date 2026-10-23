using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SharedInventory;

internal enum StationInputKind { Processing, Fuel }

internal readonly struct StationInputContext
{
    public readonly object Station;
    public readonly string MachineId;
    public readonly StationInputKind Kind;

    public StationInputContext(object station, string machineId, StationInputKind kind)
    {
        Station = station;
        MachineId = machineId;
        Kind = kind;
    }
}

// Selection and policy are shared by automatic loading and future exact-item
// shortcuts. Policies see the machine prefab and input kind, and apply equally
// to carried and shared inputs.
internal sealed class StationInventory
{
    private sealed class SharedInput
    {
        public readonly StationInputContext Context;
        public readonly string ItemId;
        public SharedInput(StationInputContext context, string itemId) { Context = context; ItemId = itemId; }
    }

    private readonly Client client;
    private readonly Func<StationInputContext, string, bool> allows;
    private readonly ConditionalWeakTable<ItemDrop.ItemData, SharedInput> shared = new ConditionalWeakTable<ItemDrop.ItemData, SharedInput>();

    public StationInventory(Client client, Func<StationInputContext, string, bool>? allows = null)
    {
        this.client = client;
        this.allows = allows ?? ((context, itemId) => true);
    }

    public bool Applies(Inventory inventory) => client.Ready && inventory == Player.m_localPlayer.GetInventory();
    public bool Allows(StationInputContext context, string itemId) => StationInputRules.Allows(context, itemId) && allows(context, itemId);

    public ItemDrop.ItemData? Select(Inventory inventory, StationInputContext context,
        IEnumerable<string> acceptedPrefabs, string? requestedPrefab = null)
    {
        if (!Applies(inventory)) return null;
        var candidates = new List<string>();
        foreach (var id in acceptedPrefabs)
        {
            if ((requestedPrefab != null && requestedPrefab != id) || !Allows(context, id)) continue;
            if (!candidates.Contains(id)) candidates.Add(id);
        }
        // Preserve conversion order within each priority. Each priority still
        // tries every local candidate before any shared alternative.
        var priorities = new SortedSet<int>();
        foreach (var id in candidates) priorities.Add(StationInputRules.Priority(context, id));
        foreach (var priority in priorities)
        {
            var selected = SelectPriority(inventory, context, candidates, priority);
            if (selected != null) return selected;
        }
        return null;
    }

    private ItemDrop.ItemData? SelectPriority(Inventory inventory, StationInputContext context, List<string> candidates, int priority)
    {
        foreach (var id in candidates)
        {
            if (StationInputRules.Priority(context, id) != priority) continue;
            var local = inventory.GetItem(id, -1, true);
            if (local != null) return local;
        }
        // Complete the entire local pass before considering any shared input.
        if (Game.m_worldLevel > 0) return null; // Shared items normalize to level zero.
        foreach (var id in candidates)
        {
            if (StationInputRules.Priority(context, id) != priority || client.Available(id) <= 0) continue;
            var item = Items.Default(id, client.Quality(id));
            if (item == null) continue;
            shared.Add(item, new SharedInput(context, id));
            return item;
        }
        return null;
    }

    // Count exact prefabs, as selection does, rather than aggregating different
    // prefabs with the same translated item name. No RPCs or item grants here.
    public long Count(Inventory inventory, StationInputContext context, string itemId)
    {
        if (!Applies(inventory) || !Allows(context, itemId)) return 0;
        long local = 0;
        foreach (var item in inventory.GetAllItems())
            if (item.m_dropPrefab != null && Items.Id(item) == itemId && item.m_worldLevel >= Game.m_worldLevel)
                local += Math.Max(0, item.m_stack);
        var sharedCount = Game.m_worldLevel > 0 ? 0 : Math.Max(0, client.Available(itemId));
        return local + Math.Min(sharedCount, long.MaxValue - local);
    }

    public List<StationOption> Options(Inventory inventory, StationInputContext context,
        IEnumerable<string> acceptedPrefabs, Func<string, string> displayName)
    {
        var options = new List<StationOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in acceptedPrefabs)
        {
            if (!seen.Add(id)) continue;
            var count = Count(inventory, context, id);
            if (count > 0 && Items.Template(id) != null) options.Add(new StationOption(id, displayName(id), count));
        }
        options.Sort((a, b) =>
        {
            var byName = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.ItemId, b.ItemId);
        });
        return options;
    }

    public bool Remove(Inventory inventory, StationInputContext context, ItemDrop.ItemData item, int amount)
    {
        if (amount <= 0 || !Applies(inventory) || item.m_dropPrefab == null || !Allows(context, Items.Id(item))) return false;
        if (inventory.ContainsItem(item)) return inventory.RemoveItem(item, amount);
        // Only descriptions issued by this selector may stand for shared stock.
        // Bind them to the actual station instance and input kind, and use once.
        if (!shared.TryGetValue(item, out var input) || !ReferenceEquals(input.Context.Station, context.Station) ||
            input.Context.Kind != context.Kind || input.Context.MachineId != context.MachineId ||
            input.ItemId != Items.Id(item) || amount > item.m_stack || client.Available(input.ItemId) < amount) return false;
        shared.Remove(item);
        client.Consume(new Dictionary<string, long>(StringComparer.Ordinal) { [input.ItemId] = amount });
        return true;
    }
}
