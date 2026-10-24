using System;
using System.Collections.Generic;
using System.Linq;
using SharedInventory;

internal static partial class Program
{
    private static SharedChest LoadedChest(float x = 0, float y = 0, float z = 0, bool valid = true)
    {
        var instance = new GameObject();
        instance.Attach(new ZNetView { Valid = valid });
        var chest = instance.Attach(new SharedChest());
        chest.transform.position = new UnityEngine.Vector3(x, y, z);
        ChestAccess.Register(chest);
        return chest;
    }

    private static void AccessTests()
    {
        Test("Only loaded chests grant inclusive 100-metre 3D coverage", () =>
        {
            ChestAccess.Clear();
            Equal(false, ChestAccess.Covers(new UnityEngine.Vector3()));
            LoadedChest();
            Equal(true, ChestAccess.Covers(new UnityEngine.Vector3(60, 80, 0)));
            Equal(false, ChestAccess.Covers(new UnityEngine.Vector3(60, 80.001f, 0)));
            Equal(true, ChestAccess.Covers(new UnityEngine.Vector3(0, -100, 0)));
            Equal(false, ChestAccess.Covers(new UnityEngine.Vector3(0, 100.001f, 0)));
        });
        Test("Unloading removes coverage and reloading restores it without a snapshot", () =>
        {
            ChestAccess.Clear();
            var first = LoadedChest(); var second = LoadedChest(50);
            ChestAccess.Unregister(first); Equal(true, ChestAccess.CanAccess);
            ChestAccess.Unregister(second); Equal(false, ChestAccess.CanAccess);
            ChestAccess.Register(second); Equal(true, ChestAccess.CanAccess);
            second.transform.position = new UnityEngine.Vector3(500, 0, 0);
            Equal(false, ChestAccess.CanAccess); // Current transform, no cached position.
            ChestAccess.Clear(); Equal(false, ChestAccess.Covers(second.transform.position));
        });
        Test("Placement ghosts inactive and invalid chests never grant access", () =>
        {
            ChestAccess.Clear();
            var chest = LoadedChest(valid: false); Equal(false, ChestAccess.CanAccess);
            var view = chest.GetComponent<ZNetView>()!; view.Valid = true;
            chest.isActiveAndEnabled = false; Equal(false, ChestAccess.CanAccess);
            chest.isActiveAndEnabled = true; chest.gameObject.activeInHierarchy = false;
            Equal(false, ChestAccess.CanAccess);
            chest.gameObject.activeInHierarchy = true; Equal(true, ChestAccess.CanAccess);
            chest.transform.position = new UnityEngine.Vector3(float.NaN, 0, 0); Equal(false, ChestAccess.CanAccess);
        });
        Test("Local access follows player life position and overlap without multiplying stock", () =>
        {
            var (client, _) = Setup("Wood", 10);
            LoadedChest(50); Equal(10L, client.Available("Wood"));
            Player.m_localPlayer.transform.position = new UnityEngine.Vector3(151, 0, 0);
            Equal(0L, client.Available("Wood"));
            Player.m_localPlayer.transform.position = new UnityEngine.Vector3(50, 0, 0);
            Equal(10L, client.Available("Wood"));
            Player.m_localPlayer.Dead = true; Equal(false, ChestAccess.CanAccess);
            Player.m_localPlayer = null!; Equal(false, ChestAccess.CanAccess);
        });
        Test("New shared mutations fail outside coverage while carried resources remain usable", () =>
        {
            var (initial, api) = Setup("Wood", 10);
            var access = false;
            var client = new Client(api, () => 0, () => access);
            client.Counts["Wood"] = 10;
            var inventory = Player.m_localPlayer.GetInventory();
            var wood = Items.Default("Wood")!; wood.m_stack = 3; inventory.AddItem(wood);
            var local = inventory.GetAllItems()[0];
            client.Deposit(local); client.Withdrawal("Wood", true);
            client.Consume(new Dictionary<string, long> { ["Wood"] = 1 });
            Equal(0, api.Calls.Count); Equal(true, inventory.ContainsItem(local)); Equal(0L, client.Available("Wood"));
            var resources = new ResourceInventory(client);
            Equal(3, resources.Count(inventory, "Wood", -1, true));
            resources.Remove(inventory, "Wood", 2, -1, true);
            Equal(1, inventory.CountItems("Wood")); Equal(0, api.Calls.Count);
            access = true; Equal(10L, client.Available("Wood"));
            client.Withdrawal("Wood", false); Equal(1, api.Calls.Count);
        });
        Test("First opening loads uncached stock with no loaded chest and blocks single and stack withdrawals", () =>
        {
            var (_, api) = Setup("Wood", 10);
            ChestAccess.Clear();
            var client = new Client(api, () => 0);
            Equal(0, client.Counts.Count); Equal(false, client.CanAccessSharedInventory);
            client.List(false); client.Refresh(true, false, false); client.MaintainRefresh();
            Equal(1, api.Calls.Count); Equal("List", api.Calls[0].Method);
            client.OnList(Stock(api.Calls[0].Id, 23, 4));
            Equal(23L, client.Counts["Wood"]); Equal(4, client.Quality("Wood"));
            Equal(0L, client.Available("Wood")); Equal(false, client.Listing);
            client.Withdrawal("Wood", false); client.Withdrawal("Wood", true);
            Equal(1, api.Calls.Count); Equal(false, client.Busy);
            Equal(0, Player.m_localPlayer.GetInventory().CountItems("Wood"));
            Equal("There is no Einherjar Chest in range.", Player.m_localPlayer.MessageText);
            LoadedChest(); Equal(true, client.CanAccessSharedInventory);
            client.Withdrawal("Wood", false); Equal(2, api.Calls.Count);
            Equal("Withdrawal", api.Calls[1].Method); Equal(1L, api.Calls[1].Amount);
        });
        Test("Opening before player or connection readiness queues a first list without chest coverage", () =>
        {
            foreach (var mode in new[] { 0, 1, 2 })
            {
                ObjectDB.instance = new ObjectDB();
                var (_, api) = Setup("Wood", 10);
                ChestAccess.Clear();
                var player = Player.m_localPlayer;
                if (mode != 1) Player.m_localPlayer = null!;
                if (mode != 0) api.Connected = false;
                var client = new Client(api, () => 0);
                client.List(false); client.MaintainRefresh();
                Equal(0, api.Calls.Count); Equal(0, client.Counts.Count);
                Player.m_localPlayer = player; api.Connected = true;
                client.MaintainRefresh(); client.Refresh(true, false, false);
                Equal(1, api.Calls.Count); Equal("List", api.Calls[0].Method);
                client.OnList(Stock(api.Calls[0].Id, 17));
                Equal(17L, client.Counts["Wood"]); Equal(false, client.CanAccessSharedInventory);
                Equal(0L, client.Available("Wood"));
            }
        });
        Test("First outside-range list retries after a timeout and stops after closing the window", () =>
        {
            var (_, api) = Setup("Wood", 10);
            ChestAccess.Clear(); double now = 0;
            var client = new Client(api, () => now);
            client.List(false);
            now = 10; client.MaintainRefresh(); Equal(false, client.Listing);
            now = 11.99; client.Refresh(true, false, false); Equal(1, api.Calls.Count);
            now = 12; client.MaintainRefresh(); Equal(2, api.Calls.Count);
            client.OnList(Stock(api.Calls[1].Id, 9));
            Equal(9L, client.Counts["Wood"]); Equal("", client.RefreshStatus);
            now = 100; client.Refresh(false, true, true, true); client.MaintainRefresh();
            Equal(2, api.Calls.Count); Equal(false, client.CanAccessSharedInventory);
        });
        Test("Station shortcuts keep carried counts and reject stale shared selections on range loss", () =>
        {
            var (_, api) = Setup("Wood", 10);
            var access = true;
            var client = new Client(api, () => 0, () => access); client.Counts["Wood"] = 10;
            var inventory = Player.m_localPlayer.GetInventory();
            var station = new StationInventory(client);
            var context = new StationInputContext(new object(), "fire_pit", StationInputKind.Fuel);
            var selected = station.Select(inventory, context, new[] { "Wood" })!;
            access = false;
            Equal(false, station.Remove(inventory, context, selected, 1));
            Equal(0L, station.Count(inventory, context, "Wood"));
            var local = Items.Default("Wood")!; local.m_stack = 2; inventory.AddItem(local);
            Equal(2L, station.Count(inventory, context, "Wood"));
            var carried = station.Select(inventory, context, new[] { "Wood" })!;
            Equal(true, station.Remove(inventory, context, carried, 1));
            Equal(1, inventory.CountItems("Wood")); Equal(0, api.Calls.Count);
        });
        Test("Accepted withdrawal completes once after leaving coverage", () =>
        {
            var (_, api) = Setup("Wood", 10);
            var access = true;
            var client = new Client(api, () => 0, () => access); client.Counts["Wood"] = 10;
            client.Withdrawal("Wood", true); var id = api.Calls[0].Id;
            access = false;
            var reply = Reply(id, "Wood", 10, 0);
            client.OnWithdrawal(reply); client.OnWithdrawal(reply);
            Equal(10, Player.m_localPlayer.GetInventory().CountItems("Wood"));
            Equal(false, client.Busy); Equal(1, api.Calls.Count);
        });
        Test("Dead withdrawal returns its entire grant outside coverage", () =>
        {
            var (_, api) = Setup("Wood", 10);
            var access = true;
            var client = new Client(api, () => 0, () => access); client.Counts["Wood"] = 10;
            client.Withdrawal("Wood", true); access = false; Player.m_localPlayer.Dead = true;
            client.OnWithdrawal(Reply(api.Calls[0].Id, "Wood", 10, 0));
            Equal(0, Player.m_localPlayer.GetInventory().CountItems("Wood"));
            Equal("Deposit", api.Calls[1].Method); Equal(10L, api.Calls[1].Amount);
            client.OnDeposit(Reply(api.Calls[1].Id, "Wood", 10, 10));
            Equal(false, client.Busy); Equal(10L, client.Counts["Wood"]);
        });
        Test("Capacity returns and rejected deposits reconcile outside coverage", () =>
        {
            var (_, api) = Setup("Wood", 10);
            var access = true;
            var client = new Client(api, () => 0, () => access); client.Counts["Wood"] = 10;
            var inventory = Player.m_localPlayer.GetInventory();
            client.Withdrawal("Wood", true); inventory.Slots = 0; access = false;
            client.OnWithdrawal(Reply(api.Calls[0].Id, "Wood", 10, 0));
            Equal("Deposit", api.Calls[1].Method); Equal(10L, api.Calls[1].Amount);
            client.OnDeposit(Reply(api.Calls[1].Id, "Wood", 10, 10));
            access = true; inventory.Slots = 4;
            var wood = Items.Default("Wood")!; wood.m_stack = 2; inventory.AddItem(wood);
            client.Deposit(inventory.GetAllItems()[0]); var deposit = api.Calls.Last().Id;
            access = false;
            client.OnDeposit(new TransferReply { RequestId = deposit, Error = "rejected" });
            Equal(2, inventory.CountItems("Wood")); Equal(false, client.Busy);
        });
        Test("Outside coverage refreshes the shared window but pauses other menus and preserves reconciliation", () =>
        {
            var (_, api) = Setup("Wood", 10);
            var access = true; double now = 0;
            var client = new Client(api, () => now, () => access); client.Counts["Wood"] = 10;
            client.Refresh(true, false, false);
            client.OnList(Stock(api.Calls[0].Id, 10));
            access = false; now = 100;
            client.Refresh(false, true, true, true); client.MaintainRefresh();
            Equal(1, api.Calls.Count);
            client.Refresh(true, true, true, true); client.List(false); client.MaintainRefresh();
            Equal(2, api.Calls.Count); client.OnList(Stock(api.Calls[1].Id, 8));
            Equal(8L, client.Counts["Wood"]); Equal(0L, client.Available("Wood"));
            now = 101; client.Refresh(true, false, false); Equal(2, api.Calls.Count);
            now = 102; client.Refresh(true, false, false); Equal(3, api.Calls.Count);
            client.OnList(Stock(api.Calls[2].Id, 7));
            access = true; client.Withdrawal("Wood", false); var id = api.Calls.Last().Id; access = false;
            client.OnWithdrawal(new TransferReply { RequestId = id, Error = "stock changed" });
            Equal("List", api.Calls.Last().Method);
            client.OnList(Stock(api.Calls.Last().Id, 4));
            var calls = api.Calls.Count;
            now = 200; client.Refresh(false, true, true, true); client.MaintainRefresh();
            Equal(calls, api.Calls.Count); Equal(4L, client.Counts["Wood"]);
            access = true; Equal(4L, client.Available("Wood"));
        });
        Test("In-flight snapshots preserve newer authoritative totals outside coverage", () =>
        {
            var (_, api) = Setup("Wood", 10);
            var access = true;
            var client = new Client(api, () => 0, () => access); client.Counts["Wood"] = 10;
            client.List(); var list = api.Calls[0].Id;
            client.Withdrawal("Wood", false); var withdraw = api.Calls[1].Id;
            access = false;
            client.OnWithdrawal(Reply(withdraw, "Wood", 1, 9));
            client.OnList(Stock(list, 10));
            Equal(9L, client.Counts["Wood"]); Equal(0L, client.Available("Wood"));
            access = true; Equal(9L, client.Available("Wood"));
        });
    }
}
