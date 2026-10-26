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
        Test("Compass guidance uses all eight world bearings relative to player", () =>
        {
            var directions = new[] { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
            var dx = new[] { 0, 3, 3, 3, 0, -3, -3, -3 };
            var dz = new[] { 3, 3, 0, -3, -3, -3, 0, 3 };
            for (var i = 0; i < 8; i++)
            {
                var binding = new EssenceBinding(1, new Vector3(1250 + dx[i], 4, -830 + dz[i]));
                var text = binding.Describe(1, new Vector3(1250, 4, -830));
                Equal(true, text.StartsWith("Bound site: " + (i % 2 == 0 ? "3" : "5") + " metres " + directions[i] + "."));
                Equal(false, text.Contains("X:")); Equal(false, text.Contains("Y:")); Equal(false, text.Contains("Z:"));
            }
            var site = new EssenceBinding(1, new Vector3(0, 0, 0));
            Equal(true, site.Describe(1, new Vector3(-240, 0, 0)).StartsWith("Bound site: 240 metres east."));
            Equal(true, site.Describe(1, new Vector3(240, 0, 0)).StartsWith("Bound site: 240 metres west."));
            foreach (var angle in new[] { 22.4, 22.6, -22.4, -22.6, 157.4, 157.6, -157.4, -157.6 })
            {
                var radians = angle * System.Math.PI / 180;
                var binding = new EssenceBinding(1, new Vector3((float)(100 * System.Math.Sin(radians)), 0, (float)(100 * System.Math.Cos(radians))));
                var expected = System.Math.Abs(angle) < 22.5 ? "north" : System.Math.Abs(angle) > 157.5 ? "south" : angle > 0 ? (angle < 90 ? "northeast" : "southeast") : (angle > -90 ? "northwest" : "southwest");
                Equal(true, binding.Describe(1, new Vector3(0, 0, 0)).Contains(" metres " + expected + "."));
            }
        });
        Test("Guidance handles height proximity and inclusive spherical boundary", () =>
        {
            var binding = new EssenceBinding(1, new Vector3(0, 0, 0));
            Equal(true, binding.Describe(1, binding.Origin).StartsWith("Bound site: here (less than 1 metre away)."));
            Equal(true, binding.Describe(1, new Vector3(.5f, .5f, 0)).Contains("here (less than 1 metre away)"));
            Equal(true, binding.Describe(1, new Vector3(0, -120, 0)).StartsWith("Bound site: 120 metres above you."));
            Equal(true, binding.Describe(1, new Vector3(.5f, 120, 0)).StartsWith("Bound site: 120 metres below you."));
            var text = binding.Describe(1, new Vector3(-60, -80, 0));
            Equal(true, text.StartsWith("Bound site: 100 metres east."));
            Equal(true, text.Contains("Site is 80 metres above you."));
            Equal(true, text.Contains("Valid at your current position."));
            Equal(true, text.Contains("Rebuild within 100 metres of this site. Range includes height. Rebuilding preserves this binding."));
            foreach (var position in new[] { new Vector3(-100, 0, 0), new Vector3(0, 100, 0) })
                Equal(true, binding.Describe(1, position).Contains("Valid at your current position."));
            foreach (var position in new[] { new Vector3(-100.001f, 0, 0), new Vector3(0, 100.001f, 0), new Vector3(-60, -80.001f, 0) })
                Equal(true, binding.Describe(1, position).Contains("Outside bound area."));
            Equal(true, binding.Describe(1, new Vector3(-1, 0, 0)).StartsWith("Bound site: 1 metre east."));
        });
        Test("Unusable positions and other worlds suppress direction and validity claims", () =>
        {
            var binding = new EssenceBinding(1, new Vector3(1250, 4, -830));
            Equal(true, binding.Describe(2, binding.Origin).StartsWith("Bound site: another world."));
            foreach (var text in new[] { binding.Describe(0, binding.Origin), binding.Describe(1, null), binding.Describe(1, new Vector3(float.NaN, 0, 0)), binding.Describe(1, new Vector3(0, float.PositiveInfinity, 0)) })
            {
                Equal(true, text.StartsWith("Bound site: direction and distance unavailable."));
                Equal(true, text.Contains("Current position validity unavailable."));
                Equal(false, text.Contains("Valid at your current position"));
                Equal(false, text.Contains("1250"));
            }
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
