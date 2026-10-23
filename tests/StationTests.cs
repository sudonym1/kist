using System.Collections.Generic;
using SharedInventory;

internal static partial class Program
{
    private static StationInputContext Station(StationInputKind kind = StationInputKind.Processing, string machine = "smelter") =>
        new StationInputContext(new object(), machine, kind);

    private static void StationTests()
    {
        Test("Station selection searches every local input before shared alternatives", () =>
        {
            var (client, api) = Setup("CopperOre", 10); Prefab("TinOre");
            var inventory = Player.m_localPlayer.Inventory;
            var local = Items.Default("TinOre")!; local.m_stack = 2; inventory.GetAllItems().Add(local);
            var inputs = new StationInventory(client);
            var context = Station(); var accepted = new[] { "CopperOre", "TinOre" };
            Equal(local, inputs.Select(inventory, context, accepted));
            Equal(true, inputs.Remove(inventory, context, local, 1));
            Equal(local, inputs.Select(inventory, context, accepted));
            Equal(true, inputs.Remove(inventory, context, local, 1));
            var shared = inputs.Select(inventory, context, accepted)!;
            Equal("CopperOre", Items.Id(shared)); Equal(0, api.Calls.Count);
            Equal(true, inputs.Remove(inventory, context, shared, 1));
            Equal(1L, api.Calls[0].Batch!["CopperOre"]); Equal(9L, client.Available("CopperOre"));
        });
        Test("Shared station input preserves conversion order and uses no backpack slots", () =>
        {
            var (client, api) = Setup("CopperOre", 10); Prefab("TinOre"); client.Counts["TinOre"] = 10;
            var inventory = Player.m_localPlayer.Inventory; inventory.Slots = 0;
            var inputs = new StationInventory(client); var context = Station();
            var item = inputs.Select(inventory, context, new[] { "TinOre", "CopperOre" })!;
            Equal("TinOre", Items.Id(item)); Equal(0, inventory.GetAllItems().Count); Equal(0, api.Calls.Count);
            Equal(true, inputs.Remove(inventory, context, item, 1)); Equal(1L, api.Calls[0].Batch!["TinOre"]);
        });
        Test("Selecting station inputs and availability never charges shared stock", () =>
        {
            var (client, api) = Setup("Coal", 10); var inputs = new StationInventory(client);
            for (var i = 0; i < 100; ++i)
                Equal("Coal", Items.Id(inputs.Select(Player.m_localPlayer.Inventory, Station(StationInputKind.Fuel), new[] { "Coal" })!));
            Equal(10L, client.Available("Coal")); Equal(0, api.Calls.Count);
        });
        Test("Shared input descriptions can be consumed only once at their station", () =>
        {
            var (client, api) = Setup("Coal", 10); var inputs = new StationInventory(client);
            var context = Station(StationInputKind.Fuel); var inventory = Player.m_localPlayer.Inventory;
            var item = inputs.Select(inventory, context, new[] { "Coal" })!;
            Equal(false, inputs.Remove(inventory, Station(StationInputKind.Fuel), item, 1));
            Equal(false, inputs.Remove(inventory, new StationInputContext(context.Station, "smelter", StationInputKind.Processing), item, 1));
            Equal(false, inputs.Remove(inventory, new StationInputContext(context.Station, "kiln", StationInputKind.Fuel), item, 1));
            Equal(true, inputs.Remove(inventory, context, item, 1));
            Equal(false, inputs.Remove(inventory, context, item, 1)); Equal(1, api.Calls.Count);
        });
        Test("Unissued and cloned station input descriptions cannot spend shared stock", () =>
        {
            var (client, api) = Setup("Coal", 10); var inputs = new StationInventory(client);
            var context = Station(); var inventory = Player.m_localPlayer.Inventory;
            var item = inputs.Select(inventory, context, new[] { "Coal" })!;
            Equal(false, inputs.Remove(inventory, context, item.Clone(), 1));
            Equal(false, inputs.Remove(inventory, context, Items.Default("Coal")!, 1));
            Equal(false, inputs.Remove(inventory, context, item, 2)); Equal(0, api.Calls.Count);
        });
        Test("Repeated station loading exhausts cached stock before acknowledgements", () =>
        {
            var (client, api) = Setup("Coal", 2); var inputs = new StationInventory(client);
            var context = Station(StationInputKind.Fuel); var inventory = Player.m_localPlayer.Inventory;
            for (var i = 0; i < 2; ++i)
                Equal(true, inputs.Remove(inventory, context, inputs.Select(inventory, context, new[] { "Coal" })!, 1));
            Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, context, new[] { "Coal" }));
            Equal(2, api.Calls.Count); Equal(0, inventory.GetAllItems().Count);
        });
        Test("Future exact-item requests do not choose another local or shared input", () =>
        {
            var (client, api) = Setup("CopperOre", 5); Prefab("TinOre"); client.Counts["TinOre"] = 5;
            var inventory = Player.m_localPlayer.Inventory; inventory.GetAllItems().Add(Items.Default("TinOre")!);
            var inputs = new StationInventory(client); var context = Station(); var accepted = new[] { "TinOre", "CopperOre" };
            Equal("CopperOre", Items.Id(inputs.Select(inventory, context, accepted, "CopperOre")!));
            Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, context, accepted, "IronOre"));
            Equal(0, api.Calls.Count);
        });
        Test("Future machine policy can protect an input from both inventory sources", () =>
        {
            var (client, api) = Setup("FineWood", 10); Prefab("Wood"); client.Counts["Wood"] = 10;
            var inventory = Player.m_localPlayer.Inventory; inventory.GetAllItems().Add(Items.Default("FineWood")!);
            var inputs = new StationInventory(client, (context, id) => context.MachineId != "charcoal_kiln" || id != "FineWood");
            var context = Station(machine: "charcoal_kiln"); var accepted = new[] { "FineWood", "Wood" };
            var item = inputs.Select(inventory, context, accepted)!;
            Equal("Wood", Items.Id(item));
            Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, context, accepted, "FineWood"));
            Equal(true, inputs.Remove(inventory, context, item, 1));
            Equal(10L, client.Available("FineWood")); Equal(1, inventory.CountItems("FineWood"));
            Equal("FineWood", Items.Id(inputs.Select(inventory, Station(machine: "another_machine"), accepted)!));
            Equal(1L, api.Calls[0].Batch!["Wood"]);
        });
        Test("Policy is rechecked when consuming a previously selected shared input", () =>
        {
            var (client, api) = Setup("Wood", 10); var allow = true;
            var inputs = new StationInventory(client, (context, id) => allow); var context = Station();
            var item = inputs.Select(Player.m_localPlayer.Inventory, context, new[] { "Wood" })!;
            allow = false;
            Equal(false, inputs.Remove(Player.m_localPlayer.Inventory, context, item, 1)); Equal(0, api.Calls.Count);
        });
        Test("Unavailable, disconnected, and other inventories cannot select shared inputs", () =>
        {
            var (client, api) = Setup("Wood", 10); var inputs = new StationInventory(client);
            var context = Station(); var inventory = Player.m_localPlayer.Inventory;
            Equal<ItemDrop.ItemData?>(null, inputs.Select(new Inventory(), context, new[] { "Wood" }));
            Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, context, new[] { "MissingPrefab" }));
            api.Connected = false;
            Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, context, new[] { "Wood" })); Equal(0, api.Calls.Count);
        });
        Test("Station shared selection retains authoritative quality and world rules", () =>
        {
            var (client, _) = Setup("Wood", 10); client.Qualities["Wood"] = 3;
            var inputs = new StationInventory(client); var inventory = Player.m_localPlayer.Inventory;
            Equal(3, inputs.Select(inventory, Station(), new[] { "Wood" })!.m_quality);
            Game.m_worldLevel = 1;
            Equal<ItemDrop.ItemData?>(null, inputs.Select(inventory, Station(), new[] { "Wood" }));
        });
        Test("Hover polling shares one two-second timer with menus and stops on look-away", () =>
        {
            double time = 0;
            var (client, api) = Setup("Wood", 10, clock: () => time);
            client.Refresh(false, false, false, stationInput: true);
            Equal(1, api.Calls.Count); client.OnList(new ListReply { RequestId = api.Calls[0].Id });
            time = 1; client.Refresh(true, false, false, stationInput: true); Equal(1, api.Calls.Count);
            time = 1.1; client.Refresh(false, false, false);
            time = 1.2; client.Refresh(false, false, false, stationInput: true); Equal(1, api.Calls.Count);
            time = 2; client.Refresh(false, false, false, stationInput: true); Equal(2, api.Calls.Count);
            client.OnList(new ListReply { RequestId = api.Calls[1].Id });
            time = 20; client.Refresh(false, false, false); Equal(2, api.Calls.Count);
            time = 21; client.Refresh(false, false, false, stationInput: true); Equal(3, api.Calls.Count);
        });
        Test("Smelter hover detects ore and fuel controls but excludes its output", () =>
        {
            var machine = new GameObject(); var smelter = machine.Attach(new Smelter());
            var ore = new GameObject { Parent = machine }; smelter.m_addOreSwitch = ore.Attach(new Switch());
            var fuel = new GameObject { Parent = machine }; smelter.m_addWoodSwitch = fuel.Attach(new Switch());
            var output = new GameObject { Parent = machine }; smelter.m_emptyOreSwitch = output.Attach(new Switch());
            Equal(true, StationTargets.IsInputTarget(ore)); Equal(true, StationTargets.IsInputTarget(new GameObject { Parent = fuel }));
            Equal(false, StationTargets.IsInputTarget(output)); Equal(false, StationTargets.IsInputTarget(machine));
        });
        Test("Cooking hover recognizes direct loading and the separate input switches", () =>
        {
            var machine = new GameObject(); var station = machine.Attach(new CookingStation());
            Equal(true, StationTargets.IsInputTarget(machine));
            var food = new GameObject { Parent = machine }; station.m_addFoodSwitch = food.Attach(new Switch());
            var fuel = new GameObject { Parent = machine }; station.m_addFuelSwitch = fuel.Attach(new Switch());
            Equal(true, StationTargets.IsInputTarget(food)); Equal(true, StationTargets.IsInputTarget(fuel));
            Equal(false, StationTargets.IsInputTarget(machine));
            Equal(false, StationTargets.IsInputTarget(new GameObject { Parent = machine }.Attach(new Switch()).gameObject));
        });
        Test("Fermenter and refillable fireplace hover activate refresh without unrelated targets", () =>
        {
            var barrel = new GameObject(); barrel.Attach(new Fermenter()); Equal(true, StationTargets.IsInputTarget(barrel));
            var fireObject = new GameObject(); var fire = fireObject.Attach(new Fireplace());
            Equal(true, StationTargets.IsInputTarget(fireObject)); fire.m_infiniteFuel = true;
            Equal(false, StationTargets.IsInputTarget(fireObject)); fire.m_infiniteFuel = false; fire.m_canRefill = false;
            Equal(false, StationTargets.IsInputTarget(fireObject));
            Equal(false, StationTargets.IsInputTarget(new GameObject())); Equal(false, StationTargets.IsInputTarget(null));
        });
    }
}
