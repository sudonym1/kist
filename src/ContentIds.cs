namespace SharedInventory;

// These names are persisted in inventories and world ZDOs. Never rename them.
internal static class ContentIds
{
    public const string Essence = "EinherjarEssence";
    public const string Chest = "EinherjarChest";
    public static bool CanShare(string id) => id != Essence;
}
