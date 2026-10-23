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
// to carried and shared inputs. No exclusion rules are enabled in this change.
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
        this.allows = allows ?? ((context, item) => true);
    }

    public bool Applies(Inventory inventory) => client.Ready && inventory == Player.m_localPlayer.GetInventory();
    public bool Allows(StationInputContext context, string itemId) => allows(context, itemId);

    public ItemDrop.ItemData? Select(Inventory inventory, StationInputContext context,
        IEnumerable<string> acceptedPrefabs, string? requestedPrefab = null)
    {
        if (!Applies(inventory)) return null;
        var candidates = new List<string>();
        foreach (var id in acceptedPrefabs)
        {
            if ((requestedPrefab != null && requestedPrefab != id) || !Allows(context, id)) continue;
            candidates.Add(id);
            var local = inventory.GetItem(id, -1, true);
            if (local != null) return local;
        }
        // Complete the entire local pass before considering any shared input.
        if (Game.m_worldLevel > 0) return null; // Shared items normalize to level zero.
        foreach (var id in candidates)
        {
            if (client.Available(id) <= 0) continue;
            var item = Items.Default(id, client.Quality(id));
            if (item == null) continue;
            shared.Add(item, new SharedInput(context, id));
            return item;
        }
        return null;
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
