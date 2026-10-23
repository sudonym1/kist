using System;
using System.Collections.Generic;
using SharedInventory;

internal static class Program
{
    private static int passed;
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; got {actual}.");
    }
    private static void Test(string name, Action action)
    {
        ObjectDB.instance = new ObjectDB();
        Player.m_localPlayer = new Player();
        ItemDrop.Dropped.Clear();
        action();
        passed++;
        System.Console.WriteLine("PASS " + name);
    }
    private static ItemDrop.ItemData Prefab(string id, int maximum = 50)
    {
        var prefab = new GameObject { name = id };
        var item = new ItemDrop.ItemData
        {
            m_dropPrefab = prefab,
            m_shared = new ItemDrop.SharedData { m_name = id, m_maxStackSize = maximum }
        };
        prefab.Drop = new ItemDrop { m_itemData = item };
        ObjectDB.instance.Prefabs.Add(id, prefab);
        return item;
    }
    private static (Client, RpcApi) Setup(string item, long total, int maximum = 50)
    {
        Prefab(item, maximum);
        var api = new RpcApi();
        var client = new Client(api);
        client.Counts.Add(item, total);
        return (client, api);
    }
    private static TransferReply Reply(long id, string item, long transferred, long total) =>
        new TransferReply { RequestId = id, ItemId = item, Amount = transferred, Total = total };

    private static int Main()
    {
        Test("Database aggregates unstackable and stackable items without game rules", () =>
        {
            var db = new CountDatabase();
            db.Deposit("SwordIron", 10);
            db.Deposit("Coal", 250);
            db.Deposit("Coal", 150);
            Equal(2, db.List().Count);
            Equal(400L, db.Total("Coal"));
            Equal(10L, db.Withdrawal("SwordIron", 10)); // No server stack-size cap.
            Equal(0L, db.Total("SwordIron"));
        });
        Test("Overlapping withdrawals cannot overdraw", () =>
        {
            var db = new CountDatabase();
            db.Deposit("Coal", 80);
            Equal(50L, db.Withdrawal("Coal", 50));
            Equal(30L, db.Withdrawal("Coal", 50));
            Equal(0L, db.Withdrawal("Coal", 50));
            Equal(0, db.List().Count);
        });
        Test("Counts exceed Int32 and returns are ordinary unrestricted deposits", () =>
        {
            var db = new CountDatabase();
            db.Deposit("unknown-prefab", 5_000_000_000L);
            Equal(5_000_000_000L, db.Total("unknown-prefab"));
            db.Withdrawal("unknown-prefab", 100);
            db.Deposit("unknown-prefab", 1000);
            Equal(5_000_000_900L, db.Total("unknown-prefab"));
        });
        Test("Overflow and invalid quantities leave counts unchanged", () =>
        {
            var db = new CountDatabase();
            db.Deposit("Coal", long.MaxValue);
            try { db.Deposit("Coal", 1); throw new Exception("Overflow was accepted"); }
            catch (OverflowException) { }
            try { db.Withdrawal("Coal", -1); throw new Exception("Negative was accepted"); }
            catch (ArgumentOutOfRangeException) { }
            Equal(long.MaxValue, db.Total("Coal"));
        });
        Test("Database operations and saves run in order on a background thread", () =>
        {
            var mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var jobThread = mainThread;
            var saveThread = mainThread;
            var db = new CountDatabase();
            var saved = 0L;
            var errors = new List<Exception>();
            var worker = new Worker(() =>
            {
                saveThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                saved = db.Total("Coal");
            }, errors.Add, System.Threading.Tasks.Task.CompletedTask);
            for (var i = 0; i < 1000; ++i) worker.Post(() =>
            {
                jobThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                db.Deposit("Coal", 1);
            });
            worker.Post(() => Equal(1000L, db.Withdrawal("Coal", 1200)));
            worker.Post(() => db.Deposit("Coal", 400));
            worker.Stop().GetAwaiter().GetResult();
            Equal(0, errors.Count); Equal(false, mainThread == jobThread);
            Equal(jobThread, saveThread); Equal(400L, saved);
        });
        Test("Stopping a worker returns without waiting for a running database job", () =>
        {
            using var entered = new System.Threading.ManualResetEventSlim();
            using var release = new System.Threading.ManualResetEventSlim();
            var errors = new List<Exception>();
            var worker = new Worker(() => { }, errors.Add, System.Threading.Tasks.Task.CompletedTask);
            worker.Post(() => { entered.Set(); release.Wait(); });
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Worker did not start");
            var completion = worker.Stop();
            var completedBeforeRelease = completion.IsCompleted;
            release.Set();
            completion.GetAwaiter().GetResult();
            Equal(false, completedBeforeRelease); Equal(0, errors.Count);
        });
        Test("List response replaces the cache without main-thread packet parsing", () =>
        {
            var (client, api) = Setup("Coal", 400);
            client.List();
            var result = new ListReply { RequestId = api.Calls[0].Id };
            result.Items["Coal"] = 200;
            result.Items["SwordIron"] = 10;
            client.OnList(result);
            Equal(true, ReferenceEquals(result.Items, client.Counts));
            Equal(200L, client.Counts["Coal"]); Equal(false, client.Listing);
        });
        Test("Default items discard per-instance metadata without modifying the prefab", () =>
        {
            var source = Prefab("SwordIron", 1);
            source.m_quality = 4; source.m_variant = 2; source.m_worldLevel = 3;
            source.m_crafterID = 123; source.m_crafterName = "Alice"; source.m_durability = 7;
            source.m_equipped = source.m_cheated = source.m_pickedUp = true;
            source.m_customData.Add("name", "Excalibur");
            var item = Items.Default("SwordIron")!;
            Equal(1, item.m_quality); Equal(0, item.m_variant); Equal(0, item.m_worldLevel);
            Equal(0L, item.m_crafterID); Equal("", item.m_crafterName); Equal(100f, item.m_durability);
            Equal(false, item.m_equipped); Equal(false, item.m_cheated); Equal(false, item.m_pickedUp);
            Equal(0, item.m_customData.Count);
            Equal(4, source.m_quality); Equal(1, source.m_customData.Count);
        });
        Test("Capacity ignores incompatible quality and world-level stacks", () =>
        {
            Prefab("Coal");
            var inventory = Player.m_localPlayer.Inventory;
            inventory.Slots = 2;
            var upgraded = Items.Default("Coal")!; upgraded.m_quality = 2;
            var world = Items.Default("Coal")!; world.m_worldLevel = 1;
            inventory.GetAllItems().Add(upgraded); inventory.GetAllItems().Add(world);
            Equal(0L, Items.Room(inventory, Items.Default("Coal")!));
        });
        Test("A full inventory sends no withdrawal RPC", () =>
        {
            var (client, api) = Setup("Coal", 400);
            Player.m_localPlayer.Inventory.Slots = 0;
            client.Withdrawal("Coal", true);
            Equal(0, api.Calls.Count);
            Equal(false, client.Busy);
        });
        Test("Nonstackable items request exactly one even for a stack action", () =>
        {
            var (client, api) = Setup("SwordIron", 10, 1);
            client.Withdrawal("SwordIron", true);
            Equal(1L, api.Calls[0].Amount);
        });
        Test("Stack button requests at most a normal stack and available stock", () =>
        {
            var (client, api) = Setup("Coal", 400);
            client.Withdrawal("Coal", true);
            Equal(50L, api.Calls[0].Amount);
            client.OnWithdrawal(Reply(api.Calls[0].Id, "Coal", 50, 350));
            client.Counts["Coal"] = 7;
            client.Withdrawal("Coal", true);
            Equal(7L, api.Calls[1].Amount);
        });
        Test("Capacity lost during an RPC returns the entire grant through Deposit", () =>
        {
            var (client, api) = Setup("Coal", 400);
            client.Withdrawal("Coal", true);
            Player.m_localPlayer.Inventory.Slots = 0;
            client.OnWithdrawal(Reply(api.Calls[0].Id, "Coal", 50, 350));
            Equal(2, api.Calls.Count); Equal("Deposit", api.Calls[1].Method);
            Equal(50L, api.Calls[1].Amount);
            Equal(0L, Items.Count(Player.m_localPlayer.Inventory, "Coal"));
        });
        Test("Partial insertion returns only the quantity that actually failed", () =>
        {
            var (client, api) = Setup("Coal", 400);
            client.Withdrawal("Coal", true);
            Player.m_localPlayer.Inventory.AddLimit = 13;
            client.OnWithdrawal(Reply(api.Calls[0].Id, "Coal", 50, 350));
            Equal(13L, Items.Count(Player.m_localPlayer.Inventory, "Coal"));
            Equal("Deposit", api.Calls[1].Method); Equal(37L, api.Calls[1].Amount);
            client.OnDeposit(Reply(api.Calls[1].Id, "Coal", 37, 387));
            Equal(387L, client.Counts["Coal"]); Equal(false, client.Busy);
        });
        Test("Death while withdrawing returns the grant without adding to the dead inventory", () =>
        {
            var (client, api) = Setup("Coal", 400);
            client.Withdrawal("Coal", true);
            Player.m_localPlayer.Dead = true;
            client.OnWithdrawal(Reply(api.Calls[0].Id, "Coal", 50, 350));
            Equal(0L, Items.Count(Player.m_localPlayer.Inventory, "Coal"));
            Equal(50L, api.Calls[1].Amount);
        });
        Test("Alt-click deposit removes the exact selected stack before sending", () =>
        {
            var (client, api) = Setup("Coal", 400);
            var item = Items.Default("Coal")!; item.m_stack = 23;
            Player.m_localPlayer.Inventory.GetAllItems().Add(item);
            client.Deposit(item);
            Equal(false, Player.m_localPlayer.Inventory.ContainsItem(item));
            Equal("Deposit", api.Calls[0].Method); Equal(23L, api.Calls[0].Amount);
            client.OnDeposit(Reply(api.Calls[0].Id, "Coal", 23, 423));
            Equal(423L, client.Counts["Coal"]); Equal(false, client.Busy);
        });
        Test("A rejected deposit restores the original metadata", () =>
        {
            var (client, api) = Setup("SwordIron", 10, 1);
            var item = Items.Default("SwordIron")!;
            item.m_quality = 4; item.m_customData["name"] = "Excalibur";
            Player.m_localPlayer.Inventory.GetAllItems().Add(item);
            client.Deposit(item);
            client.OnDeposit(new TransferReply { RequestId = api.Calls[0].Id, Error = "Database unavailable" });
            var restored = Player.m_localPlayer.Inventory.GetAllItems()[0];
            Equal(4, restored.m_quality); Equal("Excalibur", restored.m_customData["name"]);
        });
        Test("A rejected deposit drops the original if inventory filled while waiting", () =>
        {
            var (client, api) = Setup("SwordIron", 10, 1);
            var item = Items.Default("SwordIron")!; item.m_quality = 4;
            Player.m_localPlayer.Inventory.GetAllItems().Add(item);
            client.Deposit(item);
            Player.m_localPlayer.Inventory.Slots = 0;
            client.OnDeposit(new TransferReply { RequestId = api.Calls[0].Id, Error = "Database unavailable" });
            Equal(1, ItemDrop.Dropped.Count); Equal(4, ItemDrop.Dropped[0].m_quality);
        });
        System.Console.WriteLine($"{passed} tests passed.");
        return 0;
    }
}
