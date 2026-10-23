using System;
using System.Collections.Generic;

namespace SharedInventory;

internal sealed class StationOption
{
    public readonly string ItemId;
    public readonly string Name;
    public readonly long Count;
    public StationOption(string itemId, string name, long count) { ItemId = itemId; Name = name; Count = count; }
}

internal sealed class StationShortcutMenu
{
    public const int Slots = 10;
    private StationInputContext context;
    private readonly List<StationOption> displayed = new List<StationOption>();
    private bool bound;
    private int requestedPage;
    public IReadOnlyList<StationOption> Options => displayed;
    public int Page { get; private set; }
    public int PageCount => Math.Max(1, (displayed.Count + Slots - 1) / Slots);

    public void Show(StationInputContext context, List<StationOption> options)
    {
        Observe(context);
        if (!bound)
        {
            displayed.Clear();
            displayed.AddRange(options);
            // A cold cache may be empty before its first refresh completes.
            // Bind once there is something to show, then hold all slots fixed.
            bound = displayed.Count != 0;
        }
        else
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var option in options) counts[option.ItemId] = option.Count;
            for (var slot = 0; slot < displayed.Count; ++slot)
            {
                var previous = displayed[slot];
                displayed[slot] = new StationOption(previous.ItemId, previous.Name,
                    counts.TryGetValue(previous.ItemId, out var count) ? count : 0);
            }
        }
        requestedPage = Math.Min(requestedPage, PageCount - 1);
        Page = requestedPage;
    }

    public bool Matches(StationInputContext context) => ReferenceEquals(this.context.Station, context.Station) &&
        this.context.Kind == context.Kind && this.context.MachineId == context.MachineId;

    public void Observe(StationInputContext context)
    {
        if (Matches(context)) return;
        Clear();
        this.context = context;
    }

    public string? Item(StationInputContext context, int slot) => Matches(context) && slot >= 0 && slot < Slots &&
        Page * Slots + slot < displayed.Count ? displayed[Page * Slots + slot].ItemId : null;

    public bool Move(StationInputContext context, int direction)
    {
        if (!Matches(context) || PageCount <= 1) return false;
        requestedPage = (requestedPage + direction + PageCount) % PageCount;
        // Item() remains bound to the visible page until HoverText calls Show.
        return true;
    }

    public void Clear()
    {
        displayed.Clear();
        context = default;
        bound = false;
        requestedPage = Page = 0;
    }
}

// Track held keys rather than frame-dependent GetKeyDown. Consume edges even
// in menus so closing a menu or changing targets cannot load a held shortcut.
internal sealed class StationShortcutInput
{
    private int previous;
    public int Press(bool enabled, bool alt, int held)
    {
        var pressed = held & ~previous;
        previous = held;
        if (!enabled || !alt) return -1;
        for (var slot = 0; slot < StationShortcutMenu.Slots; slot++) if ((pressed & (1 << slot)) != 0) return slot;
        return -1;
    }
}
