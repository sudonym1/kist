using System.Globalization;
using SharedInventory;
using UnityEngine;

internal static partial class Program
{
    private static ItemDrop.ItemData BoundEssence(Inventory inventory, long world, Vector3 origin)
    {
        if (Items.Template(ContentIds.DimmedEssence) == null) Prefab(ContentIds.DimmedEssence, 1);
        var item = Items.Default(ContentIds.DimmedEssence)!;
        item.m_customData[EssenceBinding.Key] = new EssenceBinding(world, origin).Encode();
        inventory.AddItem(item);
        return inventory.GetAllItems()[inventory.GetAllItems().Count - 1];
    }

    private static void EssenceBindingTests()
    {
        Test("Binding roundtrips exact coordinates independently of culture", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var original = new EssenceBinding(12345, new Vector3(1250.125f, -4.5f, -830.75f));
                Equal(true, EssenceBinding.TryRead(original.Encode(), out var loaded));
                Equal(original.World, loaded.World);
                Equal(original.Origin.x, loaded.Origin.x); Equal(original.Origin.y, loaded.Origin.y); Equal(original.Origin.z, loaded.Origin.z);
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        Test("Binding rejects missing malformed unsupported and nonfinite data", () =>
        {
            foreach (var value in new string?[] { null, "", "1|0|0|0|0", "2|1|0|0|0", "1|1|NaN|0|0", "1|1|0|Infinity|0", "1|1|0|0", "1|1|0|0|0|extra" })
                Equal(false, EssenceBinding.TryRead(value, out _));
        });
        Test("Bound area is inclusive three dimensional and world specific", () =>
        {
            var binding = new EssenceBinding(1, new Vector3(10, 20, 30));
            Equal(ChestAccess.Radius, EssenceBinding.Radius);
            Equal(true, binding.Covers(1, new Vector3(10 + ChestAccess.Radius, 20, 30)));
            Equal(true, binding.Covers(1, new Vector3(10, 20 + ChestAccess.Radius, 30)));
            Equal(false, binding.Covers(1, new Vector3(10 + ChestAccess.Radius + .001f, 20, 30)));
            Equal(false, binding.Covers(1, new Vector3(10, 20 + ChestAccess.Radius + .001f, 30)));
            Equal(false, binding.Covers(2, binding.Origin));
        });
        Test("Valid dimmed essence wins over regular regardless of inventory order", () =>
        {
            Prefab(ContentIds.Essence);
            var inventory = Player.m_localPlayer.GetInventory();
            inventory.AddItem(Items.Default(ContentIds.Essence)!);
            var bound = BoundEssence(inventory, 1, new Vector3(0, 0, 0));
            Equal(bound, EssenceSelection.Select(inventory, 1, new Vector3(5, 0, 0)));
            inventory.RemoveItem(bound, 1);
            Equal(ContentIds.Essence, Items.Id(EssenceSelection.Select(inventory, 1, new Vector3(5, 0, 0))!));
        });
        Test("Selection finds nearest eligible bound item and consumes only that instance", () =>
        {
            var inventory = Player.m_localPlayer.GetInventory();
            var farther = BoundEssence(inventory, 1, new Vector3(15, 0, 0));
            var nearer = BoundEssence(inventory, 1, new Vector3(5, 0, 0));
            BoundEssence(inventory, 2, new Vector3(0, 0, 0));
            var selected = EssenceSelection.Select(inventory, 1, new Vector3(0, 0, 0));
            Equal(nearer, selected);
            inventory.RemoveItem(selected!, 1);
            Equal(true, inventory.ContainsItem(farther)); Equal(false, inventory.ContainsItem(nearer));
        });
        Test("Outside site wrong world or malformed dimmed items fall back to regular", () =>
        {
            Prefab(ContentIds.Essence);
            var inventory = Player.m_localPlayer.GetInventory();
            var malformed = BoundEssence(inventory, 1, new Vector3(0, 0, 0));
            malformed.m_customData.Clear();
            BoundEssence(inventory, 1, new Vector3(ChestAccess.Radius + 1, 0, 0));
            BoundEssence(inventory, 2, new Vector3(0, 0, 0));
            Equal<ItemDrop.ItemData?>(null, EssenceSelection.Select(inventory, 1, new Vector3(0, 0, 0)));
            inventory.AddItem(Items.Default(ContentIds.Essence)!);
            Equal(ContentIds.Essence, Items.Id(EssenceSelection.Select(inventory, 1, new Vector3(0, 0, 0))!));
        });
        Test("Trading separate nonstackable essences preserves each origin", () =>
        {
            var inventory = Player.m_localPlayer.GetInventory();
            var first = BoundEssence(inventory, 1, new Vector3(0, 0, 0));
            var second = BoundEssence(inventory, 1, new Vector3(500, 0, 0));
            var traded = first.Clone(); inventory.RemoveItem(first);
            var recipient = new Inventory(); recipient.AddItem(traded);
            var received = recipient.GetAllItems()[0];
            Equal(true, EssenceBinding.TryRead(received, out var binding));
            Equal(true, binding.Covers(1, new Vector3(ChestAccess.Radius, 0, 0)));
            Equal(false, binding.Covers(1, new Vector3(ChestAccess.Radius + 20, 0, 0)));
            Equal(true, EssenceBinding.TryRead(second, out var other)); Equal(500f, other.Origin.x);
        });
        Test("Tooltip shows coordinates radius and world status without ingredient priority", () =>
        {
            var binding = new EssenceBinding(1, new Vector3(1250, 4, -830));
            var text = binding.Describe(1, binding.Origin);
            Equal(true, text.Contains("X: 1250, Y: 4, Z: -830"));
            Equal(true, text.Contains(ChestAccess.Radius + " metres")); Equal(true, text.Contains("Valid at your current position"));
            Equal(true, binding.Describe(2, binding.Origin).Contains("another world"));
            Equal(true, binding.Describe(1, new Vector3(0, 0, 0)).Contains("Outside bound area"));
            Equal(false, text.Contains("before")); Equal(false, text.Contains("priority"));
        });
        Test("Dimmed essence cannot be deposited withdrawn or spent from shared counts", () =>
        {
            var (client, api) = Setup(ContentIds.DimmedEssence, 10, 1);
            var inventory = Player.m_localPlayer.GetInventory();
            var bound = BoundEssence(inventory, 1, new Vector3(0, 0, 0));
            client.Deposit(bound); client.Withdrawal(ContentIds.DimmedEssence, true);
            client.Consume(new System.Collections.Generic.Dictionary<string, long> { [ContentIds.DimmedEssence] = 1 });
            Equal(0, api.Calls.Count); Equal(0L, client.Available(ContentIds.DimmedEssence));
            Equal(true, inventory.ContainsItem(bound)); Equal(10L, client.Counts[ContentIds.DimmedEssence]);
        });
    }
}
