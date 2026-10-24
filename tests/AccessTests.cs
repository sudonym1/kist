using System;
using System.Collections.Generic;
using System.Linq;
using SharedInventory;

internal static partial class Program
{
    private static void AccessTests()
    {
        Test("Coverage fails closed until ready and includes exact 3D boundary", () =>
        {
            var map = new ChestCoverage<int>();
            map.Upsert(1, new ChestPoint(0, 0, 0));
            Equal(false, map.Covers(new ChestPoint(0, 0, 0)));
            map.Ready = true;
            Equal(true, map.Covers(new ChestPoint(60, 80, 0)));
            Equal(false, map.Covers(new ChestPoint(60, 80.001f, 0)));
            Equal(true, map.Covers(new ChestPoint(0, -100, 0)));
            Equal(false, map.Covers(new ChestPoint(0, 100.001f, 0)));
        });
        Test("Overlapping and unloaded positions preserve access until final destruction", () =>
        {
            var map = new ChestCoverage<int> { Ready = true };
            map.Upsert(1, new ChestPoint(0, 0, 0)); map.Upsert(2, new ChestPoint(50, 0, 0));
            Equal(true, map.Covers(new ChestPoint(25, 0, 0)));
            map.Remove(1); Equal(true, map.Covers(new ChestPoint(25, 0, 0)));
            Equal(false, map.Upsert(2, new ChestPoint(50, 0, 0)));
            map.Upsert(2, new ChestPoint(500, 0, 0)); Equal(false, map.Covers(new ChestPoint(25, 0, 0)));
            map.Remove(2); Equal(0, map.Positions.Count);
        });
        Test("Coverage validates configuration and rejects nonfinite positions", () =>
        {
            foreach (var radius in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                Equal(false, ChestPoint.ValidRadius(radius));
            var map = new ChestCoverage<int> { Ready = true };
            map.SetRadius(25); map.Upsert(1, new ChestPoint(0, 0, 0));
            Equal(true, map.Covers(new ChestPoint(25, 0, 0)));
            Equal(false, map.Covers(new ChestPoint(25.01f, 0, 0)));
            Equal(false, map.Covers(new ChestPoint(float.NaN, 0, 0)));
            map.Upsert(1, new ChestPoint(float.PositiveInfinity, 0, 0));
            Equal(0, map.Positions.Count);
        });
        Test("All new shared requests fail outside coverage while carried resources remain usable", () =>
        {
            var (initial, api) = Setup("Wood", 10);
            var access = false;
            var client = new Client(api, () => 0, () => access);
            client.Counts["Wood"] = 10;
            var inventory = Player.m_localPlayer.GetInventory();
            var wood = Items.Default("Wood")!; wood.m_stack = 3; inventory.AddItem(wood);
            var local = inventory.GetAllItems()[0];
            client.List(); client.Refresh(true, true, true, true); client.MaintainRefresh();
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
        Test("Outside coverage pauses ordinary polling but preserves failure recovery", () =>
        {
            var (_, api) = Setup("Wood", 10);
            var access = true; double now = 0;
            var client = new Client(api, () => now, () => access); client.Counts["Wood"] = 10;
            client.Refresh(true, false, false);
            client.OnList(Stock(api.Calls[0].Id, 10));
            access = false; now = 100;
            client.Refresh(true, true, true, true); client.List(); client.MaintainRefresh();
            Equal(1, api.Calls.Count);
            access = true; client.Withdrawal("Wood", false); var id = api.Calls.Last().Id; access = false;
            client.OnWithdrawal(new TransferReply { RequestId = id, Error = "stock changed" });
            Equal("List", api.Calls.Last().Method);
            client.OnList(Stock(api.Calls.Last().Id, 4));
            var calls = api.Calls.Count;
            now = 200; client.Refresh(true, true, true, true); client.MaintainRefresh();
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
