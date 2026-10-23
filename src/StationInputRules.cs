using System;

namespace SharedInventory;

internal static class StationInputRules
{
    private static bool Kiln(StationInputContext context) => context.Kind == StationInputKind.Processing &&
        string.Equals(context.MachineId, "charcoal_kiln", StringComparison.Ordinal);

    public static bool Allows(StationInputContext context, string itemId) => !Kiln(context) || itemId != "FineWood";

    // Prefer cheap wood even when it is shared and another wood is carried.
    // Exact-item shortcuts bypass priority, but never bypass exclusions.
    public static int Priority(StationInputContext context, string itemId) => Kiln(context) && itemId == "Wood" ? 0 : 1;
}
