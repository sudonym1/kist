using System;
using System.Collections.Generic;
using SharedInventory;

internal static partial class Program
{
    private static ListReply Stock(long id, long amount, int quality = 3) => new ListReply
    {
        RequestId = id, Items = new Dictionary<string, long> { ["Wood"] = amount },
        Qualities = new Dictionary<string, int> { ["Wood"] = quality }
    };

    private static void RefreshTests()
    {
        Test("Persistent refresh loss follows 2 4 8 16 30 second capped backoff", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
            client.List();
            foreach (var delay in new[] { 2, 4, 8, 16, 30, 30, 30 })
            {
                var attempts = api.Calls.Count;
                time += 10; client.MaintainRefresh(); Equal(false, client.Listing);
                Equal(10L, client.Available("Wood"));
                for (var frame = 0; frame < 100; ++frame)
                {
                    client.Refresh(true, true, true, true); client.List(false); client.MaintainRefresh();
                }
                Equal(attempts, api.Calls.Count);
                time += delay - .01; client.MaintainRefresh(); Equal(attempts, api.Calls.Count);
                time += .01; client.MaintainRefresh(); Equal(attempts + 1, api.Calls.Count);
            }
            client.OnList(Stock(api.Calls[api.Calls.Count - 1].Id, 8));
            Equal("", client.RefreshStatus);
            time += 2; client.Refresh(true, false, false); var count = api.Calls.Count;
            time += 10; client.MaintainRefresh(); time += 2;
            client.Refresh(true, false, false); Equal(count + 1, api.Calls.Count);
        });
        Test("Manual refresh bypasses backoff once and preserves accumulated failures", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
            client.List(); time = 10; client.MaintainRefresh(); client.List();
            for (var i = 0; i < 100; ++i) client.List();
            Equal(2, api.Calls.Count);
            time = 20; client.MaintainRefresh(); time = 23.99; client.MaintainRefresh();
            Equal(2, api.Calls.Count); time = 24; client.MaintainRefresh(); Equal(3, api.Calls.Count);
        });
        Test("Explicit demand coalesced with an active poll survives closing its menus", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
            client.Refresh(true, false, false); client.List(false); Equal(1, api.Calls.Count);
            time = 10; client.MaintainRefresh(); time = 12; client.MaintainRefresh();
            Equal(2, api.Calls.Count); client.OnList(Stock(api.Calls[1].Id, 9));
            time = 60; client.MaintainRefresh(); Equal(2, api.Calls.Count);
        });
        Test("Retired replies before during and after replacement leave its deadline and stock intact", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
            client.List(); var old = api.Calls[0].Id;
            time = 10; client.MaintainRefresh(); var version = client.Version;
            client.OnList(Stock(old, 100, 1)); Equal(version, client.Version); Equal(false, client.Listing);
            time = 12; client.MaintainRefresh(); var current = api.Calls[1].Id;
            time = 21; client.OnList(Stock(old, 200, 1)); Equal(true, client.Listing);
            time = 22; client.MaintainRefresh(); Equal(false, client.Listing);
            time = 26; client.MaintainRefresh(); current = api.Calls[2].Id;
            client.OnList(Stock(current, 6, 4)); version = client.Version;
            client.OnList(Stock(old, 300, 1)); client.OnList(Stock(current, 500, 1));
            Equal(6L, client.Available("Wood")); Equal(4, client.Quality("Wood")); Equal(version, client.Version);
            Equal(3, api.Calls.Count);
        });
        Test("A reply at the deadline is expired even before the maintenance tick", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
            client.List(); time = 10; client.OnList(Stock(api.Calls[0].Id, 100));
            Equal(false, client.Listing); Equal(10L, client.Available("Wood"));
            time = 12; client.MaintainRefresh(); Equal(2, api.Calls.Count);
        });
        Test("Polling demand ends with menus while explicit refresh demand survives", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
            client.Refresh(true, false, false); time = 10; client.MaintainRefresh();
            time = 60; client.Refresh(false, false, false); client.MaintainRefresh(); Equal(1, api.Calls.Count);
            client.Refresh(false, false, false, true); Equal(2, api.Calls.Count);
            client.OnList(Stock(api.Calls[1].Id, 9)); client.List();
            time = 70; client.MaintainRefresh(); time = 72; client.MaintainRefresh();
            Equal(4, api.Calls.Count);
        });
        Test("Maintenance retires refreshes during death missing player and disconnect without dispatch", () =>
        {
            foreach (var mode in new[] { 0, 1, 2 })
            {
                ObjectDB.instance = new ObjectDB();
                double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
                var player = Player.m_localPlayer; client.List();
                if (mode == 0) player.Dead = true;
                if (mode == 1) Player.m_localPlayer = null!;
                if (mode == 2) api.Connected = false;
                client.MaintainRefresh(); Equal(false, client.Listing);
                time = 60; client.MaintainRefresh(); Equal(1, api.Calls.Count);
                Player.m_localPlayer = player; player.Dead = false; api.Connected = true;
                client.MaintainRefresh(); Equal(2, api.Calls.Count);
                client.OnList(Stock(api.Calls[1].Id, 8)); Equal(false, client.Listing);
            }
        });
        Test("Dispatch exceptions and error replies release the client with bounded retries", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
            api.ThrowList = true; client.List(); Equal(false, client.Listing);
            for (var i = 0; i < 100; ++i) client.MaintainRefresh(); Equal(0, api.Calls.Count);
            time = 2; api.ThrowList = false; client.MaintainRefresh(); Equal(1, api.Calls.Count);
            client.OnList(new ListReply { RequestId = api.Calls[0].Id, Error = "Storage unavailable" });
            Equal(false, client.Listing); Equal(10L, client.Available("Wood"));
            time = 5.99; client.MaintainRefresh(); Equal(1, api.Calls.Count);
            time = 6; client.MaintainRefresh(); Equal(2, api.Calls.Count);
            client.OnList(Stock(api.Calls[1].Id, 7)); Equal(7L, client.Available("Wood"));
        });
        Test("Failure demand before replacement is consumed but a newer failure requires one follow-up", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
            client.List(); client.Consume(new Dictionary<string, long> { ["Wood"] = 2 });
            client.OnBatch(new BatchReply { RequestId = api.Calls[1].Id, Error = "Stock changed" });
            time = 10; client.MaintainRefresh();
            client.Consume(new Dictionary<string, long> { ["Wood"] = 2 });
            client.OnBatch(new BatchReply { RequestId = api.Calls[2].Id, Error = "Stock changed" });
            Equal(3, api.Calls.Count);
            time = 12; client.MaintainRefresh(); Equal(4, api.Calls.Count);
            client.Consume(new Dictionary<string, long> { ["Wood"] = 2 });
            client.OnBatch(new BatchReply { RequestId = api.Calls[4].Id, Error = "Stock changed" });
            client.OnList(Stock(api.Calls[3].Id, 10)); Equal(6, api.Calls.Count);
            client.OnList(Stock(api.Calls[5].Id, 10)); client.MaintainRefresh(); Equal(6, api.Calls.Count);
        });
        Test("Replacement snapshots preserve pending batches newer quality and uncertain transfers", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 20, clock: () => time);
            client.Withdrawal("Wood", false); // Unconfirmed mutation stays pending.
            client.List(); var old = api.Calls[1].Id;
            client.Consume(new Dictionary<string, long> { ["Wood"] = 4 }); var batch = api.Calls[2].Id;
            time = 10; client.MaintainRefresh(); Equal(true, client.Busy); Equal(1, client.PendingBatches);
            time = 12; client.MaintainRefresh(); var replacement = api.Calls[3].Id;
            client.Consume(new Dictionary<string, long> { ["Wood"] = 3 }); var newer = api.Calls[4].Id;
            client.OnList(Stock(replacement, 15, 4)); Equal(12L, client.Available("Wood"));
            client.OnList(Stock(old, 20, 1)); Equal(12L, client.Available("Wood"));
            client.OnBatch(Batch(batch, "Wood", 15, 2)); Equal(12L, client.Available("Wood"));
            client.OnBatch(Batch(newer, "Wood", 12, 4)); Equal(12L, client.Available("Wood"));
            Equal(4, client.Quality("Wood")); Equal(true, client.Busy); Equal(0, client.PendingBatches);
            time = 100; client.MaintainRefresh(); Equal(5, api.Calls.Count); Equal(true, client.Busy);
        });
        Test("A newer transfer total and quality survive a replacement followed by an old reply", () =>
        {
            double time = 0; var (client, api) = Setup("Wood", 10, clock: () => time);
            client.List(); time = 10; client.MaintainRefresh(); time = 12; client.MaintainRefresh();
            client.Withdrawal("Wood", false);
            var reply = Reply(api.Calls[2].Id, "Wood", 1, 5); reply.Quality = 4;
            client.OnWithdrawal(reply); client.OnList(Stock(api.Calls[1].Id, 10, 1));
            client.OnList(Stock(api.Calls[0].Id, 20, 1));
            Equal(5L, client.Available("Wood")); Equal(4, client.Quality("Wood")); Equal(false, client.Busy);
        });
    }
}
