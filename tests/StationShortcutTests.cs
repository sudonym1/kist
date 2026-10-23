using System;
using System.Collections.Generic;
using SharedInventory;

internal static partial class Program
{
    private static void StationShortcutTests()
    {
        Test("Station shortcut rows use alphabetical display names and combined exact-prefab counts", () =>
        {
            var (client, api) = Setup("TinOre", 7); Prefab("CopperOre");
            var inventory = Player.m_localPlayer.Inventory;
            var tin = Items.Default("TinOre")!; tin.m_stack = 3; inventory.GetAllItems().Add(tin);
            var copper = Items.Default("CopperOre")!; copper.m_stack = 2; inventory.GetAllItems().Add(copper);
            var inputs = new StationInventory(client); var context = Station();
            var options = inputs.Options(inventory, context, new[] { "TinOre", "CopperOre", "TinOre" }, id => id == "TinOre" ? "Tin ore" : "Copper ore");
            Equal(2, options.Count); Equal("CopperOre", options[0].ItemId); Equal(2L, options[0].Count);
            Equal("TinOre", options[1].ItemId); Equal(10L, options[1].Count); Equal(0, api.Calls.Count);
        });
        Test("Equal display names keep a deterministic prefab tie-breaker", () =>
        {
            var (client, _) = Setup("ItemB", 1); Prefab("ItemA"); client.Counts["ItemA"] = 1;
            var inputs = new StationInventory(client); var context = Station();
            foreach (var accepted in new[] { new[] { "ItemB", "ItemA" }, new[] { "ItemA", "ItemB" } })
            {
                var options = inputs.Options(Player.m_localPlayer.Inventory, context, accepted, id => "Same name");
                Equal("ItemA", options[0].ItemId); Equal("ItemB", options[1].ItemId);
            }
        });
        Test("Fresh shortcut lists omit zero stock and sort restocked items alphabetically", () =>
        {
            var (client, _) = Setup("CopperOre", 1); Prefab("TinOre"); client.Counts["TinOre"] = 3;
            var inputs = new StationInventory(client); var context = Station(); var inventory = Player.m_localPlayer.Inventory;
            var accepted = new[] { "TinOre", "CopperOre" };
            var before = inputs.Options(inventory, context, accepted, id => id); Equal("CopperOre", before[0].ItemId);
            inputs.Remove(inventory, context, inputs.Select(inventory, context, accepted, "CopperOre")!, 1);
            var depleted = inputs.Options(inventory, context, accepted, id => id);
            Equal(1, depleted.Count); Equal("TinOre", depleted[0].ItemId);
            client.Counts["CopperOre"] = 2;
            Equal("CopperOre", inputs.Options(inventory, context, accepted, id => id)[0].ItemId);
        });
        Test("A shortcut retains its displayed prefab when a stock update shifts the order", () =>
        {
            var (client, api) = Setup("CopperOre", 1); Prefab("TinOre"); client.Counts["TinOre"] = 3;
            var inputs = new StationInventory(client); var context = Station(); var inventory = Player.m_localPlayer.Inventory;
            var accepted = new[] { "CopperOre", "TinOre" }; var menu = new StationShortcutMenu();
            menu.Show(context, inputs.Options(inventory, context, accepted, id => id));
            client.Counts["CopperOre"] = 0;
            var requested = menu.Item(context, 0);
            Equal("CopperOre", requested);
            Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, context, accepted, requested)); Equal(0, api.Calls.Count);
            menu.Show(context, inputs.Options(inventory, context, accepted, id => id));
            Equal("CopperOre", menu.Item(context, 0)); Equal(0L, menu.Options[0].Count);
            menu.Clear(); // Looking away ends this hover session.
            menu.Show(context, inputs.Options(inventory, context, accepted, id => id));
            Equal("TinOre", menu.Item(context, 0));
        });
        Test("Shortcut mapping belongs to the displayed station and input kind", () =>
        {
            var context = Station(); var menu = new StationShortcutMenu();
            menu.Show(context, new List<StationOption> { new StationOption("TinOre", "Tin ore", 1) });
            Equal("TinOre", menu.Item(context, 0)); Equal<string?>(null, menu.Item(Station(), 0));
            Equal<string?>(null, menu.Item(new StationInputContext(context.Station, context.MachineId, StationInputKind.Fuel), 0));
            menu.Clear(); Equal<string?>(null, menu.Item(context, 0));
        });
        Test("Ten number slots include the zero-key slot and reject unmapped keys", () =>
        {
            var context = Station(); var menu = new StationShortcutMenu(); var options = new List<StationOption>();
            for (var slot = 0; slot < 10; ++slot) options.Add(new StationOption("Item" + slot, "Item " + slot, 1));
            menu.Show(context, options);
            Equal("Item9", menu.Item(context, 9)); Equal<string?>(null, menu.Item(context, 10));
            Equal<string?>(null, menu.Item(context, -1));
        });
        Test("Alt-number edges load once per press without held-key repeats", () =>
        {
            var keys = new StationShortcutInput();
            Equal(1, keys.Press(true, true, 1 << 1));
            Equal(-1, keys.Press(true, true, 1 << 1));
            Equal(-1, keys.Press(true, true, 0)); Equal(1, keys.Press(true, true, 1 << 1));
        });
        Test("Closing a menu or beginning Alt with a held number does not trigger a load", () =>
        {
            var keys = new StationShortcutInput();
            Equal(-1, keys.Press(false, true, 1)); Equal(-1, keys.Press(true, true, 1));
            Equal(-1, keys.Press(true, false, 0)); Equal(-1, keys.Press(true, false, 1));
            Equal(-1, keys.Press(true, true, 1)); Equal(-1, keys.Press(true, true, 0));
            Equal(0, keys.Press(true, true, 1));
        });
        Test("Large shortcut lists page without changing the last visible binding before redraw", () =>
        {
            var context = Station(); var menu = new StationShortcutMenu(); var options = new List<StationOption>();
            for (var slot = 0; slot < 12; ++slot) options.Add(new StationOption("Item" + slot, "Item " + slot, 1));
            menu.Show(context, options); Equal(2, menu.PageCount); Equal("Item0", menu.Item(context, 0));
            Equal(true, menu.Move(context, 1)); Equal("Item0", menu.Item(context, 0));
            menu.Show(context, options); Equal(1, menu.Page); Equal("Item10", menu.Item(context, 0));
            Equal("Item11", menu.Item(context, 1)); Equal<string?>(null, menu.Item(context, 2));
            Equal(true, menu.Move(context, 1)); menu.Show(context, options); Equal(0, menu.Page);
        });
        Test("Shortcut pages retain depleted slots and reset when the target changes", () =>
        {
            var context = Station(); var menu = new StationShortcutMenu(); var options = new List<StationOption>();
            for (var slot = 0; slot < 12; ++slot) options.Add(new StationOption("Item" + slot, "Item " + slot, 1));
            menu.Show(context, options); menu.Move(context, 1); menu.Show(context, options);
            menu.Show(context, new List<StationOption> { options[0] }); Equal(1, menu.Page); Equal(2, menu.PageCount);
            Equal("Item10", menu.Item(context, 0)); Equal(0L, menu.Options[10].Count);
            menu.Show(Station(), options); Equal(0, menu.Page);
        });
        Test("Alt three remains Iron at zero across refreshes until looking away", () =>
        {
            var (client, api) = Setup("IronScrap", 1); Prefab("CopperOre"); Prefab("Bronze");
            client.Counts["CopperOre"] = 5; client.Counts["Bronze"] = 5;
            var inputs = new StationInventory(client); var context = Station(); var menu = new StationShortcutMenu();
            var inventory = Player.m_localPlayer.Inventory; var accepted = new[] { "IronScrap", "CopperOre", "Bronze" };
            string Name(string id) => id == "IronScrap" ? "Iron" : id;
            menu.Show(context, inputs.Options(inventory, context, accepted, Name));
            Equal("IronScrap", menu.Item(context, 2));
            Equal(true, inputs.Remove(inventory, context, inputs.Select(inventory, context, accepted, menu.Item(context, 2))!, 1));
            for (var refresh = 0; refresh < 3; refresh++)
            {
                menu.Show(context, inputs.Options(inventory, context, accepted, Name));
                Equal("IronScrap", menu.Item(context, 2)); Equal("Iron", menu.Options[2].Name); Equal(0L, menu.Options[2].Count);
                Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, context, accepted, menu.Item(context, 2)));
            }
            Equal(1, api.Calls.Count); // Pressing an exhausted binding sends no request.
            client.Counts["IronScrap"] = 2;
            menu.Show(context, inputs.Options(inventory, context, accepted, Name));
            Equal("IronScrap", menu.Item(context, 2)); Equal(2L, menu.Options[2].Count);
            client.Counts["IronScrap"] = 0;
            menu.Clear(); menu.Show(context, inputs.Options(inventory, context, accepted, Name));
            Equal(2, menu.Options.Count); Equal<string?>(null, menu.Item(context, 2));
        });
        Test("Restocked new item types wait for a new hover rather than shifting existing bindings", () =>
        {
            var context = Station(); var menu = new StationShortcutMenu();
            menu.Show(context, new List<StationOption> { new StationOption("TinOre", "Tin", 2) });
            var restocked = new List<StationOption> { new StationOption("CopperOre", "Copper", 3), new StationOption("TinOre", "Tin", 5) };
            menu.Show(context, restocked);
            Equal("TinOre", menu.Item(context, 0)); Equal(5L, menu.Options[0].Count); Equal(1, menu.Options.Count);
            menu.Clear(); menu.Show(context, restocked);
            Equal("CopperOre", menu.Item(context, 0)); Equal("TinOre", menu.Item(context, 1));
        });
        Test("An initially empty hover can populate when its first refresh completes", () =>
        {
            var context = Station(); var menu = new StationShortcutMenu();
            menu.Show(context, new List<StationOption>());
            Equal<string?>(null, menu.Item(context, 0));
            menu.Show(context, new List<StationOption> { new StationOption("TinOre", "Tin", 3) });
            Equal("TinOre", menu.Item(context, 0));
            menu.Show(context, new List<StationOption>());
            Equal("TinOre", menu.Item(context, 0)); Equal(0L, menu.Options[0].Count);
        });
        Test("Observing a different target ends the binding session even without drawing its hints", () =>
        {
            var context = Station(); var menu = new StationShortcutMenu();
            menu.Show(context, new List<StationOption> { new StationOption("IronScrap", "Iron", 1) });
            menu.Observe(context); menu.Show(context, new List<StationOption>());
            Equal("IronScrap", menu.Item(context, 0)); // Input focus alone does not reset.
            menu.Observe(Station()); menu.Observe(context);
            menu.Show(context, new List<StationOption> { new StationOption("TinOre", "Tin", 2) });
            Equal("TinOre", menu.Item(context, 0));
        });
        Test("Kiln excludes fine wood from local, shared, automatic, and exact selection", () =>
        {
            var (client, api) = Setup("FineWood", 10); var inventory = Player.m_localPlayer.Inventory;
            var local = Items.Default("FineWood")!; inventory.GetAllItems().Add(local);
            var context = Station(machine: "charcoal_kiln"); var inputs = new StationInventory(client);
            Equal(false, inputs.Allows(context, "FineWood"));
            Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, context, new[] { "FineWood" }));
            Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, context, new[] { "FineWood" }, "FineWood"));
            Equal(false, inputs.Remove(inventory, context, local, 1));
            Equal(0, inputs.Options(inventory, context, new[] { "FineWood" }, id => id).Count);
            Equal(1, inventory.CountItems("FineWood")); Equal(10L, client.Available("FineWood")); Equal(0, api.Calls.Count);
        });
        Test("Fine wood is still available at other machines and for crafting and building", () =>
        {
            var (client, api) = Setup("FineWood", 10); var inventory = Player.m_localPlayer.Inventory;
            var inputs = new StationInventory(client); var other = Station(machine: "another_machine");
            Equal(true, inputs.Allows(other, "FineWood"));
            Equal(true, inputs.Remove(inventory, other, inputs.Select(inventory, other, new[] { "FineWood" })!, 1));
            Equal(9L, client.Available("FineWood"));
            var crafting = new ResourceInventory(client);
            Equal(9, crafting.Count(inventory, "FineWood", -1, true));
            crafting.Remove(inventory, "FineWood", 2, -1, true);
            Equal(7L, client.Available("FineWood")); Equal(2, api.Calls.Count);
        });
        Test("Kiln prefers shared regular wood over carried alternative woods", () =>
        {
            var (client, _) = Setup("Wood", 10); Prefab("RoundLog"); Prefab("YggdrasilWood");
            var inventory = Player.m_localPlayer.Inventory; inventory.GetAllItems().Add(Items.Default("RoundLog")!);
            var inputs = new StationInventory(client); var context = Station(machine: "charcoal_kiln");
            var accepted = new[] { "RoundLog", "YggdrasilWood", "Wood" };
            Equal("Wood", Items.Id(inputs.Select(inventory, context, accepted)!));
            client.Counts["Wood"] = 0;
            Equal("RoundLog", Items.Id(inputs.Select(inventory, context, accepted)!));
            client.Counts["YggdrasilWood"] = 3; inventory.GetAllItems().Clear();
            Equal("YggdrasilWood", Items.Id(inputs.Select(inventory, context, accepted)!));
        });
        Test("Exact shortcuts may choose other kiln woods without changing alphabetical rows", () =>
        {
            var (client, _) = Setup("Wood", 10); Prefab("RoundLog"); client.Counts["RoundLog"] = 2;
            var inputs = new StationInventory(client); var context = Station(machine: "charcoal_kiln");
            var inventory = Player.m_localPlayer.Inventory; var accepted = new[] { "Wood", "RoundLog" };
            Equal("RoundLog", Items.Id(inputs.Select(inventory, context, accepted, "RoundLog")!));
            var options = inputs.Options(inventory, context, accepted, id => id == "RoundLog" ? "Core wood" : "Wood");
            Equal("RoundLog", options[0].ItemId); Equal("Wood", options[1].ItemId);
        });
        Test("Regular wood priority does not change selection at other machines", () =>
        {
            var (client, _) = Setup("Wood", 10); Prefab("RoundLog");
            Player.m_localPlayer.Inventory.GetAllItems().Add(Items.Default("RoundLog")!);
            var inputs = new StationInventory(client);
            Equal("RoundLog", Items.Id(inputs.Select(Player.m_localPlayer.Inventory, Station(), new[] { "Wood", "RoundLog" })!));
        });
        Test("Station totals saturate Int64 and respect loadable world levels", () =>
        {
            var (client, api) = Setup("Wood", long.MaxValue); var inventory = Player.m_localPlayer.Inventory;
            inventory.GetAllItems().Add(Items.Default("Wood")!); var inputs = new StationInventory(client); var context = Station();
            Equal(long.MaxValue, inputs.Count(inventory, context, "Wood"));
            Game.m_worldLevel = 1;
            Equal(0L, inputs.Count(inventory, context, "Wood")); Equal(0, api.Calls.Count);
        });
        Test("Resolved station targets distinguish processing and fuel for shortcuts", () =>
        {
            var machine = new GameObject(); var smelter = machine.Attach(new Smelter());
            var ore = new GameObject { Parent = machine }; smelter.m_addOreSwitch = ore.Attach(new Switch());
            var fuel = new GameObject { Parent = machine }; smelter.m_addWoodSwitch = fuel.Attach(new Switch());
            Equal(true, StationTargets.TryGet(ore, out var station, out var kind));
            Equal(smelter, station); Equal(StationInputKind.Processing, kind);
            Equal(true, StationTargets.TryGet(fuel, out station, out kind)); Equal(StationInputKind.Fuel, kind);
        });
    }
}
