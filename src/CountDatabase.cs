using System;
using System.Collections.Generic;

namespace SharedInventory;

// No game types or per-client state. Called only on the database worker.
internal sealed class CountDatabase
{
    private readonly Dictionary<string, long> counts;
    private readonly Dictionary<string, int> qualities = new Dictionary<string, int>(StringComparer.Ordinal);
    public bool Dirty { get; private set; }

    public CountDatabase(Dictionary<string, long>? initial = null, Dictionary<string, int>? initialQualities = null)
    {
        counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (initial == null) return;
        foreach (var pair in initial)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Value < 0)
                throw new InvalidOperationException("Invalid shared inventory save.");
            var quality = initialQualities != null && initialQualities.TryGetValue(pair.Key, out var q) ? q : 1;
            if (quality < 1) throw new InvalidOperationException("Invalid item quality.");
            if (pair.Value > 0) { counts.Add(pair.Key, pair.Value); qualities.Add(pair.Key, quality); }
        }
    }

    public IReadOnlyDictionary<string, long> List() => counts;
    public long Total(string itemId) => counts.TryGetValue(itemId, out var count) ? count : 0;

    public int Quality(string itemId) => qualities.TryGetValue(itemId, out var quality) ? quality : 1;

    public long Deposit(string itemId, long amount, int quality = 1)
    {
        Validate(itemId, amount);
        if (quality < 1) throw new ArgumentOutOfRangeException(nameof(quality));
        var total = checked(Total(itemId) + amount);
        qualities[itemId] = Math.Max(Quality(itemId), quality);
        counts[itemId] = total;
        Dirty = true;
        return total;
    }

    public long Withdrawal(string itemId, long amount)
    {
        Validate(itemId, amount);
        var total = Total(itemId);
        var taken = Math.Min(total, amount);
        if (taken == 0) return 0;
        if (taken == total) { counts.Remove(itemId); qualities.Remove(itemId); }
        else counts[itemId] = total - taken;
        Dirty = true;
        return taken;
    }

    public void MarkSaved() => Dirty = false;

    private static void Validate(string itemId, long amount)
    {
        if (string.IsNullOrEmpty(itemId)) throw new ArgumentException("Item ID is required.");
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
    }
}
