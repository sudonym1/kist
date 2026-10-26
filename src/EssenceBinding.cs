using System;
using System.Globalization;
using UnityEngine;

namespace SharedInventory;

// Item-local data: ordinary inventory saves, trades and world drops preserve it.
internal readonly struct EssenceBinding
{
    internal const string Key = "local.sharedinventory.essenceBinding";
    internal const float Radius = ChestAccess.Radius;
    internal readonly long World;
    internal readonly Vector3 Origin;
    internal EssenceBinding(long world, Vector3 origin) { World = world; Origin = origin; }

    internal string Encode() => "1|" + World.ToString(CultureInfo.InvariantCulture) + "|" +
        Origin.x.ToString("R", CultureInfo.InvariantCulture) + "|" + Origin.y.ToString("R", CultureInfo.InvariantCulture) + "|" +
        Origin.z.ToString("R", CultureInfo.InvariantCulture);

    internal static bool TryRead(string? value, out EssenceBinding binding)
    {
        binding = default;
        var parts = value?.Split('|');
        if (parts == null || parts.Length != 5 || parts[0] != "1" ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var world) || world == 0 ||
            !Coordinate(parts[2], out var x) || !Coordinate(parts[3], out var y) || !Coordinate(parts[4], out var z)) return false;
        binding = new EssenceBinding(world, new Vector3(x, y, z));
        return true;
    }

    private static bool Coordinate(string value, out float coordinate) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate) &&
        !float.IsNaN(coordinate) && !float.IsInfinity(coordinate);

    internal static bool TryRead(ItemDrop.ItemData item, out EssenceBinding binding) =>
        TryRead(item.m_customData.TryGetValue(Key, out var value) ? value : null, out binding);

    internal double DistanceSquared(Vector3 position)
    {
        var x = (double)position.x - Origin.x;
        var y = (double)position.y - Origin.y;
        var z = (double)position.z - Origin.z;
        return x * x + y * y + z * z;
    }

    internal bool Covers(long world, Vector3 position) => world == World && DistanceSquared(position) <= Radius * Radius;

    internal string Describe(long world, Vector3? position)
    {
        var coordinates = string.Format(CultureInfo.InvariantCulture, "Bound site: X: {0:0}, Y: {1:0}, Z: {2:0}", Origin.x, Origin.y, Origin.z);
        var status = world == 0 ? "" : world != World ? "\nBound to another world." : !position.HasValue ? "" :
            Covers(world, position.Value) ? "\nValid at your current position." : "\nOutside bound area.";
        return coordinates + "\nRebuild within " + Radius + " metres of this site. Rebuilding preserves this binding." + status;
    }
}

internal static class EssenceSelection
{
    // Inventory order breaks ties. Never select by name alone: each dimmed item
    // has its own origin, and the chosen instance must be the one consumed.
    internal static ItemDrop.ItemData? Select(Inventory inventory, long world, Vector3 position)
    {
        ItemDrop.ItemData? dimmed = null, regular = null;
        var nearest = double.PositiveInfinity;
        foreach (var item in inventory.GetAllItems())
        {
            if (item.m_stack <= 0 || item.m_dropPrefab == null) continue;
            if (Items.Id(item) == ContentIds.Essence && regular == null) regular = item;
            if (Items.Id(item) != ContentIds.DimmedEssence || !EssenceBinding.TryRead(item, out var binding) ||
                !binding.Covers(world, position)) continue;
            var distance = binding.DistanceSquared(position);
            if (distance < nearest) { nearest = distance; dimmed = item; }
        }
        return dimmed ?? regular;
    }
}
