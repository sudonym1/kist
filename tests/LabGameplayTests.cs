using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SharedInventory;

internal static partial class Program
{
    // A virtual clock gives repeatable client/gameplay tests without sleeping.
    // Real network transport, Unity, and Harmony are outside these tests' scope.
    private sealed class GameplayLab
    {
        public double Time;
        public readonly TestHarness Network;
        public readonly CountDatabase Database = new CountDatabase();
        private readonly List<(Client Client, RpcApi Api)> clients = new List<(Client, RpcApi)>();
        private readonly Dictionary<RpcApi, int> offsets = new Dictionary<RpcApi, int>();
        public GameplayLab() => Network = new TestHarness(new TestSettings
        { Active = true, RequestDelayMs = 100, ReplyDelayMs = 100 }, () => Time);
        public (Client, RpcApi) AddClient(string item, long amount)
        {
            var api = new RpcApi { Testing = new TestHarness(new TestSettings(), () => Time) };
            var client = new Client(api, () => Time);
            client.Counts[item] = amount; client.Qualities[item] = 1;
            clients.Add((client, api)); offsets[api] = 0;
            return (client, api);
        }
        public void Tick(double seconds)
        {
            Time += seconds;
            foreach (var pair in clients) pair.Client.MaintainRefresh();
            for (var pass = 0; pass < 5; ++pass)
            {
                foreach (var pair in clients)
                {
                    while (offsets[pair.Api] < pair.Api.Calls.Count)
                    {
                        var call = pair.Api.Calls[offsets[pair.Api]++];
                        var peer = clients.IndexOf(pair) + 2;
                        Network.Schedule(peer, call.Method, 64, () => Process(peer, pair.Client, call));
                    }
                }
                Network.Pump();
            }
        }
        private void Process(long peer, Client client, RpcApi.Call call)
        {
            if (call.Method == "List")
            {
                var reply = new ListReply { RequestId = call.Id, Items = new Dictionary<string, long>(Database.List()) };
                foreach (var item in reply.Items.Keys) reply.Qualities[item] = Database.Quality(item);
                Network.Schedule(peer, "ListReply", 64, () =>
                {
                    client.OnList(reply);
                    clients.Find(p => p.Client == client).Api.Testing?.Complete(reply.RequestId, reply.Error);
                });
            }
            else if (call.Batch != null)
            {
                var reply = new BatchReply { RequestId = call.Id };
                if (!Database.Withdrawal(call.Batch)) reply.Error = "Stock changed";
                else foreach (var item in call.Batch.Keys)
                { reply.Totals[item] = Database.Total(item); reply.Qualities[item] = Database.Quality(item); }
                Network.Schedule(peer, "WithdrawalReply", 64, () => client.OnBatch(reply));
            }
            else
            {
                var quality = call.Method == "Deposit" ? call.Quality : Database.Quality(call.Item);
                var amount = call.Amount;
                if (call.Method == "Deposit") Database.Deposit(call.Item, amount, quality);
                else amount = Database.Withdrawal(call.Item, amount);
                var reply = Reply(call.Id, call.Item, amount, Database.Total(call.Item)); reply.Quality = quality;
                Network.Schedule(peer, call.Method + "Reply", 64, () =>
                { if (call.Method == "Deposit") client.OnDeposit(reply); else client.OnWithdrawal(reply); });
            }
        }
    }

    private static void LabGameplayTests()
    {
        Test("Lost ListReply expires and recovers without reconnecting", () =>
        {
            Prefab("Wood"); var lab = new GameplayLab(); lab.Database.Deposit("Wood", 20, 3);
            var (client, api) = lab.AddClient("Wood", 10);
            lab.Network.DropNext = "ListReply";
            client.List(); lab.Tick(0); lab.Tick(.11); lab.Tick(.11);
            lab.Network.DisableFaults(); lab.Tick(10); lab.Tick(2);
            client.Refresh(true, false, false);
            lab.Tick(0); lab.Tick(.11); lab.Tick(.11);
            Equal(2, api.Calls.Count); Equal(false, client.Listing);
            Equal(20L, client.Available("Wood")); Equal(3, client.Quality("Wood"));
        });
        Test("Lost List request recovers after faults are disabled", () =>
        {
            var lab = new GameplayLab(); lab.Database.Deposit("Wood", 9, 4);
            var (client, api) = lab.AddClient("Wood", 3);
            lab.Network.DropNext = "List"; client.List(); lab.Tick(0);
            lab.Tick(10); Equal(false, client.Listing); Equal(3L, client.Available("Wood"));
            lab.Network.DisableFaults(); lab.Tick(2);
            Equal(9L, client.Available("Wood")); Equal(4, client.Quality("Wood"));
            Equal(false, client.Listing); Equal(2, api.Calls.Count);
        });
        Test("Held old snapshot cannot overwrite replacement or optimistic consumption", () =>
        {
            var lab = new GameplayLab(); lab.Database.Deposit("Wood", 10, 4);
            var (client, api) = lab.AddClient("Wood", 10);
            lab.Network.HoldNext = "ListReply";
            client.List(); lab.Tick(0); lab.Tick(.11);
            client.Consume(new Dictionary<string, long> { ["Wood"] = 4 });
            lab.Tick(0); lab.Tick(.11); lab.Tick(.11);
            lab.Tick(10); lab.Tick(2); lab.Tick(.11); lab.Tick(.11);
            Equal(6L, client.Available("Wood")); Equal(4, client.Quality("Wood"));
            lab.Network.Release(); lab.Tick(0);
            Equal(6L, client.Available("Wood")); Equal(4, client.Quality("Wood"));
            Equal(false, client.Listing); Equal(3, api.Calls.Count);
            var report = Newtonsoft.Json.Linq.JObject.FromObject(api.Testing!.Snapshot());
            Equal(0, report["PendingRefreshes"]!.Value<int>());
            var metrics = report["Metrics"]!;
            Equal(1L, metrics["Counters"]!["refreshTimeouts"]!.Value<long>());
            Equal(1L, metrics["Counters"]!["refreshRetries"]!.Value<long>());
            Equal(1L, metrics["Counters"]!["ignoredRefreshReplies"]!.Value<long>());
            Equal(1, metrics["Samples"]!["roundTripMs"]!["Count"]!.Value<int>());
        });
        Test("Ten-second outage crossing deadline recovers and ignores the expired reply", () =>
        {
            var lab = new GameplayLab(); lab.Database.Deposit("Wood", 7);
            var (client, api) = lab.AddClient("Wood", 1);
            client.List(); lab.Tick(0); lab.Tick(.11);
            lab.Network.Outage(10); lab.Tick(10);
            Equal(false, client.Listing); Equal(1L, client.Available("Wood"));
            lab.Tick(2); lab.Tick(.11); lab.Tick(.11);
            Equal(false, client.Listing); Equal(7L, client.Available("Wood")); Equal(2, api.Calls.Count);
        });
        Test("Delayed withdrawal after death returns awarded items exactly once", () =>
        {
            Prefab("Wood"); var lab = new GameplayLab(); lab.Database.Deposit("Wood", 20, 3);
            var (client, api) = lab.AddClient("Wood", 20);
            client.Withdrawal("Wood", false); lab.Tick(0); lab.Tick(.11);
            Player.m_localPlayer.Dead = true;
            lab.Tick(.11); lab.Tick(.11); lab.Tick(.11);
            Equal(0L, Items.Count(Player.m_localPlayer.Inventory, "Wood"));
            Equal(20L, lab.Database.Total("Wood")); Equal(3, lab.Database.Quality("Wood"));
            Equal(false, client.Busy); Equal(2, api.Calls.Count);
        });
        Test("Outage and capacity race retain a pending grant then return the part that does not fit", () =>
        {
            Prefab("Wood"); var lab = new GameplayLab(); lab.Database.Deposit("Wood", 50);
            var (client, api) = lab.AddClient("Wood", 50);
            client.Withdrawal("Wood", true); lab.Tick(0); lab.Tick(.11);
            lab.Network.Outage(10); Player.m_localPlayer.Inventory.AddLimit = 7;
            lab.Tick(5); Equal(true, client.Busy); Equal(0L, Items.Count(Player.m_localPlayer.Inventory, "Wood"));
            lab.Tick(6); lab.Tick(.11); lab.Tick(.11);
            Equal(7L, Items.Count(Player.m_localPlayer.Inventory, "Wood"));
            Equal(43L, lab.Database.Total("Wood")); Equal(false, client.Busy);
            Equal(43L, api.Calls[1].Amount);
        });
        Test("Competing delayed crafting batches record deliberate unpaid shared costs", () =>
        {
            Prefab("Wood"); var lab = new GameplayLab(); lab.Database.Deposit("Wood", 5);
            var (first, _) = lab.AddClient("Wood", 5); var (second, secondApi) = lab.AddClient("Wood", 5);
            first.Consume(new Dictionary<string, long> { ["Wood"] = 4 });
            second.Consume(new Dictionary<string, long> { ["Wood"] = 4 });
            lab.Tick(0); lab.Tick(.11); lab.Tick(.11); lab.Tick(.11); lab.Tick(.11);
            Equal(1L, lab.Database.Total("Wood")); Equal(1L, first.Available("Wood"));
            Equal(1L, second.Available("Wood")); Equal("List", secondApi.Calls[1].Method);
            Equal(true, second.Status.Contains("failed"));
        });
        Test("Deposit confirmation lost after commit remains pending without an unsafe item restore", () =>
        {
            Prefab("Wood"); var lab = new GameplayLab();
            var (client, api) = lab.AddClient("Wood", 0);
            var item = Items.Default("Wood")!; item.m_stack = 8;
            Player.m_localPlayer.Inventory.GetAllItems().Add(item);
            lab.Network.DropNext = "DepositReply";
            client.Deposit(item); lab.Tick(0); lab.Tick(.11); lab.Tick(60);
            Equal(true, client.Busy); Equal(8L, lab.Database.Total("Wood"));
            Equal(0L, Items.Count(Player.m_localPlayer.Inventory, "Wood"));
            api.Connected = false; lab.Network.Cancel();
            var (reconnected, _) = lab.AddClient("Wood", 0);
            reconnected.List(); lab.Tick(0); lab.Tick(.11); lab.Tick(.11);
            Equal(8L, reconnected.Available("Wood")); Equal(false, reconnected.Busy);
        });
    }
}
