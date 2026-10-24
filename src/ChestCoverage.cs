using System;
using System.Collections.Generic;

namespace SharedInventory;

// No Unity dependencies: the same inclusive, three-dimensional rule serves
// loaded and unloaded chests. Double arithmetic avoids squared-float overflow.
internal readonly struct ChestPoint : IEquatable<ChestPoint>
{
    public readonly float X, Y, Z;
    public ChestPoint(float x, float y, float z) { X = x; Y = y; Z = z; }
    public bool Valid => Finite(X) && Finite(Y) && Finite(Z);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public bool Equals(ChestPoint other) => X == other.X && Y == other.Y && Z == other.Z;
    public static bool ValidRadius(float radius) => Finite(radius) && radius > 0;
}

internal sealed class ChestCoverage<T> where T : notnull
{
    internal readonly Dictionary<T, ChestPoint> Positions = new Dictionary<T, ChestPoint>();
    public bool Ready { get; set; }
    public float Radius { get; private set; } = 100;
    public void SetRadius(float radius)
    {
        if (!ChestPoint.ValidRadius(radius)) throw new ArgumentOutOfRangeException(nameof(radius));
        Radius = radius;
    }
    public bool Upsert(T id, ChestPoint position)
    {
        if (!position.Valid) return Remove(id);
        if (Positions.TryGetValue(id, out var previous) && previous.Equals(position)) return false;
        Positions[id] = position;
        return true;
    }
    public bool Remove(T id) => Positions.Remove(id);
    public bool Covers(ChestPoint player)
    {
        if (!Ready || !player.Valid) return false;
        var squared = (double)Radius * Radius;
        foreach (var chest in Positions.Values)
        {
            var x = (double)player.X - chest.X;
            var y = (double)player.Y - chest.Y;
            var z = (double)player.Z - chest.Z;
            if (x * x + y * y + z * z <= squared) return true;
        }
        return false;
    }
}
