using System;
using System.Collections.Generic;
using SharedInventory;

internal static partial class Program
{
    private static BatchReply Batch(long id, string item, long total, int quality = 1) => new BatchReply
    {
        RequestId = id,
        Totals = new Dictionary<string, long> { [item] = total },
        Qualities = new Dictionary<string, int> { [item] = quality }
    };

    private static void GameplayTests()
    {
        Test("Exact batch rejects every ingredient if one is missing", () =>
        {
            var db = new CountDatabase();
            db.Deposit("Wood", 10, 3);
            db.Deposit("Stone", 1);
            db.MarkSaved();
            Equal(false, db.Withdrawal(new Dictionary<string, long> { ["Wood"] = 10, ["Stone"] = 2 }));
            Equal(10L, db.Total("Wood")); Equal(3, db.Quality("Wood"));
            Equal(1L, db.Total("Stone")); Equal(false, db.Dirty);
        });
        Test("Competing exact batches cannot partially charge or overdraw", () =>
        {
            var db = new CountDatabase();
            db.Deposit("Wood", 10); db.Deposit("Stone", 2);
            var request = new Dictionary<string, long> { ["Wood"] = 6, ["Stone"] = 2 };
            Equal(true, db.Withdrawal(request)); Equal(false, db.Withdrawal(request));
            Equal(4L, db.Total("Wood")); Equal(0L, db.Total("Stone"));
            Equal(1, db.Quality("Stone"));
        });
        Test("Invalid batch cannot mutate valid preceding ingredients", () =>
        {
            var db = new CountDatabase(); db.Deposit("Wood", long.MaxValue, 4); db.MarkSaved();
            try
            {
                db.Withdrawal(new Dictionary<string, long> { ["Wood"] = 1, ["Stone"] = -1 });
                throw new Exception("Invalid batch accepted");
            }
            catch (ArgumentOutOfRangeException) { }
            Equal(long.MaxValue, db.Total("Wood")); Equal(4, db.Quality("Wood")); Equal(false, db.Dirty);
        });
        Test("Resource checks combine carried and shared ingredients without RPCs", () =>
        {
            var (client, api) = Setup("Wood", 10);
            var carried = Items.Default("Wood")!; carried.m_stack = 3;
            var inventory = Player.m_localPlayer.Inventory; inventory.GetAllItems().Add(carried);
            var resources = new ResourceInventory(client);
            Equal(13, resources.Count(inventory, "Wood", -1, true));
            Equal(true, resources.Have(inventory, "Wood", true)); Equal(0, api.Calls.Count);
        });
        Test("A whole resource operation consumes local first and sends one shared batch", () =>
        {
            var (client, api) = Setup("Wood", 10); Prefab("Stone"); client.Counts["Stone"] = 4;
            var carried = Items.Default("Wood")!; carried.m_stack = 3;
            var inventory = Player.m_localPlayer.Inventory; inventory.GetAllItems().Add(carried);
            var resources = new ResourceInventory(client);
            using (resources.Begin())
            {
                resources.Remove(inventory, "Wood", 7, -1, true);
                resources.Remove(inventory, "Stone", 2, -1, true);
                Equal(6, resources.Count(inventory, "Wood", -1, true));
                Equal(0, api.Calls.Count);
            }
            Equal(0, inventory.CountItems("Wood")); Equal(1, api.Calls.Count);
            Equal(4L, api.Calls[0].Batch!["Wood"]); Equal(2L, api.Calls[0].Batch!["Stone"]);
            Equal(6L, client.Available("Wood")); Equal(2L, client.Available("Stone"));
            Equal(0, inventory.CountItems("Stone"));
        });
        Test("Local-only consumption sends no shared request", () =>
        {
            var (client, api) = Setup("Wood", 10);
            var carried = Items.Default("Wood")!; carried.m_stack = 8;
            var inventory = Player.m_localPlayer.Inventory; inventory.GetAllItems().Add(carried);
            new ResourceInventory(client).Remove(inventory, "Wood", 5, -1, true);
            Equal(3, inventory.CountItems("Wood")); Equal(10L, client.Available("Wood")); Equal(0, api.Calls.Count);
        });
        Test("Shared material consumption does not require backpack space", () =>
        {
            var (client, api) = Setup("Wood", 10); Player.m_localPlayer.Inventory.Slots = 0;
            new ResourceInventory(client).Remove(Player.m_localPlayer.Inventory, "Wood", 5, -1, true);
            Equal(5L, api.Calls[0].Batch!["Wood"]); Equal(0, Player.m_localPlayer.Inventory.GetAllItems().Count);
        });
        Test("Ingredient quality checks and alternative item selection agree", () =>
        {
            var (client, api) = Setup("Wood", 10); client.Qualities["Wood"] = 3;
            var carried = Items.Default("Wood", 2)!; carried.m_stack = 4;
            var inventory = Player.m_localPlayer.Inventory; inventory.GetAllItems().Add(carried);
            var resources = new ResourceInventory(client);
            Equal(4, resources.Count(inventory, "Wood", 2, true));
            Equal(10, resources.Count(inventory, "Wood", 3, true));
            Equal(0, resources.Count(inventory, "Wood", 1, true));
            Equal(3, resources.Get(inventory, "Wood", 3, false)!.m_quality);
            resources.Remove(inventory, "Wood", 6, 3, true);
            Equal(4, inventory.CountItems("Wood", 2)); Equal(6L, api.Calls[0].Batch!["Wood"]);
        });
        Test("World-level and other inventories cannot use shared ingredients", () =>
        {
            var (client, api) = Setup("Wood", 10); var resources = new ResourceInventory(client);
            var inventory = Player.m_localPlayer.Inventory;
            Equal(0, resources.Count(new Inventory(), "Wood", -1, true));
            Game.m_worldLevel = 1;
            Equal(0, resources.Count(inventory, "Wood", -1, true));
            Equal(10, resources.Count(inventory, "Wood", -1, false));
            Equal<ItemDrop.ItemData?>(null, resources.Get(inventory, "Wood", 1, false)); Equal(0, api.Calls.Count);
        });
        Test("Resource counts saturate Int32 rather than overflowing", () =>
        {
            var (client, _) = Setup("Wood", long.MaxValue);
            Equal(int.MaxValue, new ResourceInventory(client).Count(Player.m_localPlayer.Inventory, "Wood", -1, true));
        });
        Test("Repeated resource use deducts the cache before server replies", () =>
        {
            var (client, api) = Setup("Wood", 10); var resources = new ResourceInventory(client);
            var inventory = Player.m_localPlayer.Inventory;
            resources.Remove(inventory, "Wood", 4, -1, true);
            resources.Remove(inventory, "Wood", 4, -1, true);
            Equal(2, resources.Count(inventory, "Wood", -1, true)); Equal(2, api.Calls.Count);
            client.OnBatch(Batch(api.Calls[0].Id, "Wood", 6)); Equal(2L, client.Available("Wood"));
            client.OnBatch(Batch(api.Calls[1].Id, "Wood", 2)); Equal(2L, client.Available("Wood"));
            Equal(2, api.Calls.Count); // Success never downloads a list.
        });
        Test("List arriving during consumption preserves pending deductions", () =>
        {
            var (client, api) = Setup("Wood", 10); client.List();
            client.Consume(new Dictionary<string, long> { ["Wood"] = 4 });
            client.OnList(new ListReply { RequestId = api.Calls[0].Id, Items = new Dictionary<string, long> { ["Wood"] = 10 } });
            Equal(6L, client.Available("Wood"));
            client.OnBatch(Batch(api.Calls[1].Id, "Wood", 6)); Equal(6L, client.Available("Wood"));
        });
        Test("List requested after consumption does not subtract its batch twice", () =>
        {
            var (client, api) = Setup("Wood", 10);
            client.Consume(new Dictionary<string, long> { ["Wood"] = 4 }); client.List();
            client.OnList(new ListReply { RequestId = api.Calls[1].Id, Items = new Dictionary<string, long> { ["Wood"] = 6 } });
            Equal(6L, client.Available("Wood"));
            client.OnBatch(Batch(api.Calls[0].Id, "Wood", 6)); Equal(6L, client.Available("Wood"));
        });
        Test("Stale list cannot restore stock after a newer batch reply", () =>
        {
            var (client, api) = Setup("Wood", 10); client.List();
            client.Consume(new Dictionary<string, long> { ["Wood"] = 10 });
            client.OnBatch(Batch(api.Calls[1].Id, "Wood", 0));
            client.OnList(new ListReply { RequestId = api.Calls[0].Id, Items = new Dictionary<string, long> { ["Wood"] = 10, ["Stone"] = 3 } });
            Equal(0L, client.Available("Wood")); Equal(3L, client.Available("Stone"));
        });
        Test("Rejected optimistic use keeps local consumption and refreshes once", () =>
        {
            var (client, api) = Setup("Wood", 10);
            var carried = Items.Default("Wood")!; carried.m_stack = 2;
            Player.m_localPlayer.Inventory.GetAllItems().Add(carried);
            new ResourceInventory(client).Remove(Player.m_localPlayer.Inventory, "Wood", 6, -1, true);
            var failure = new BatchReply { RequestId = api.Calls[0].Id, Error = "Stock changed" };
            client.OnBatch(failure); client.OnBatch(failure);
            Equal(0, Player.m_localPlayer.Inventory.CountItems("Wood"));
            Equal(2, api.Calls.Count); Equal("List", api.Calls[1].Method);
        });
        Test("Failure during an outstanding list schedules a fresh follow-up", () =>
        {
            var (client, api) = Setup("Wood", 10); client.List();
            client.Consume(new Dictionary<string, long> { ["Wood"] = 4 });
            client.OnBatch(new BatchReply { RequestId = api.Calls[1].Id, Error = "Stock changed" });
            Equal(2, api.Calls.Count);
            client.OnList(new ListReply { RequestId = api.Calls[0].Id });
            Equal(3, api.Calls.Count); Equal("List", api.Calls[2].Method);
        });
        Test("Placing with every menu closed never polls the inventory", () =>
        {
            var (client, api) = Setup("Wood", 10);
            for (var time = 0; time < 60; time++) client.Refresh(false, false, false);
            client.Consume(new Dictionary<string, long> { ["Wood"] = 4 });
            client.OnBatch(Batch(api.Calls[0].Id, "Wood", 6));
            for (var time = 60; time < 120; time++) client.Refresh(false, false, false);
            Equal(1, api.Calls.Count); Equal("Withdrawal", api.Calls[0].Method);
        });
        Test("Open menus poll every two seconds and stop when closed", () =>
        {
            foreach (var menu in new[] { 0, 1, 2 })
            {
                double time = 0;
                var (client, api) = Setup("Wood" + menu, 10, clock: () => time);
                client.Refresh(menu == 0, menu == 1, menu == 2);
                Equal(1, api.Calls.Count); client.OnList(new ListReply { RequestId = api.Calls[0].Id });
                time = 1; client.Refresh(menu == 0, menu == 1, menu == 2); Equal(1, api.Calls.Count);
                time = 2; client.Refresh(menu == 0, menu == 1, menu == 2); Equal(2, api.Calls.Count);
                client.OnList(new ListReply { RequestId = api.Calls[1].Id });
                time = 20; client.Refresh(false, false, false); Equal(2, api.Calls.Count);
                time = 20.1; client.Refresh(menu == 0, menu == 1, menu == 2); Equal(3, api.Calls.Count);
            }
        });
        Test("Empty or partial backpack withdrawals also refresh after stock races", () =>
        {
            var (client, api) = Setup("Wood", 10); client.Withdrawal("Wood", true);
            client.OnWithdrawal(Reply(api.Calls[0].Id, "Wood", 2, 0));
            Equal(2, Player.m_localPlayer.Inventory.CountItems("Wood"));
            Equal(2, api.Calls.Count); Equal("List", api.Calls[1].Method);
        });
    }
}
