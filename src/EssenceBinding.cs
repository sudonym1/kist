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
        var rule = "\nRebuild within " + Radius.ToString(CultureInfo.InvariantCulture) + " metres of this site. Range includes height. Rebuilding preserves this binding.";
        if (world != 0 && world != World) return "Bound site: another world." + rule + "\nBound to another world.";
        if (world == 0 || !position.HasValue || !Finite(position.Value.x) || !Finite(position.Value.y) || !Finite(position.Value.z))
            return "Bound site: direction and distance unavailable." + rule + "\nCurrent position validity unavailable.";

        var here = position.Value;
        var x = (double)Origin.x - here.x;
        var y = (double)Origin.y - here.y;
        var z = (double)Origin.z - here.z;
        var horizontal = Math.Sqrt(x * x + z * z);
        var distance = Math.Sqrt(DistanceSquared(here));
        string guidance;
        if (distance < 1) guidance = "Bound site: here (less than 1 metre away).";
        else if (horizontal < 1) guidance = "Bound site: " + Metres(Math.Abs(y)) + (y > 0 ? " above you." : " below you.");
        else
        {
            // Valheim Minimap.WorldToMapPoint maps +X right/east and +Z up/north.
            var sector = (int)Math.Floor(Math.Atan2(x, z) * 4 / Math.PI + 0.5);
            var directions = new[] { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
            guidance = "Bound site: " + Metres(distance) + " " + directions[(sector + 8) % 8] + ".";
            if (Math.Abs(y) >= 1) guidance += "\nSite is " + Metres(Math.Abs(y)) + (y > 0 ? " above you." : " below you.");
        }
        return guidance + rule + (Covers(world, here) ? "\nValid at your current position." : "\nOutside bound area.");
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static string Metres(double distance) => Math.Ceiling(distance).ToString("0", CultureInfo.InvariantCulture) + (Math.Ceiling(distance) == 1 ? " metre" : " metres");
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
