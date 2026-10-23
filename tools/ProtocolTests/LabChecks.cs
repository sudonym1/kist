using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using SharedInventory;

internal static class LabChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Test(string name, Action action) { action(); System.Console.WriteLine("PASS " + name); }
    public static int Run()
    {
        Test("Faults default off, even with configured delay and hold/drop armed", () =>
        {
            double time = 0; var delivered = 0;
            var lab = new TestHarness(new TestSettings { RequestDelayMs = 10000 }, () => time);
            lab.HoldNext = lab.DropNext = "Request";
            lab.Schedule(2, "Deposit", 50, () => delivered++); lab.Pump();
            Check(delivered == 1 && lab.Held == 0, "Disabled faults affected delivery");
        });
        Test("Asymmetric delays preserve per-peer reliable order and do not block", () =>
        {
            double time = 0; var delivered = new List<int>();
            var lab = new TestHarness(new TestSettings { Active = true, RequestDelayMs = 100, ReplyDelayMs = 300 }, () => time);
            lab.Schedule(2, "List", 1, () => delivered.Add(1));
            lab.Schedule(2, "ListReply", 1, () => delivered.Add(2));
            lab.Settings.RequestDelayMs = 0;
            lab.Schedule(2, "Deposit", 1, () => delivered.Add(3));
            lab.Pump(); Check(delivered.Count == 0, "Delay blocked or was bypassed");
            time = .1; lab.Pump(); Check(delivered.SequenceEqual(new[] { 1, 3 }), "Request order changed");
            time = .3; lab.Pump(); Check(delivered.SequenceEqual(new[] { 1, 3, 2 }), "Reply delay incorrect");
        });
        Test("Bandwidth is independent for peers and each direction", () =>
        {
            double time = 0; var count = 0;
            var lab = new TestHarness(new TestSettings { Active = true, BytesPerSecond = 100 }, () => time);
            lab.Schedule(2, "List", 100, () => count++);
            lab.Schedule(2, "Deposit", 100, () => count++);
            lab.Schedule(3, "List", 100, () => count++);
            lab.Schedule(2, "ListReply", 100, () => count++);
            time = 1; lab.Pump(); Check(count == 3, "Bandwidth lanes interfere");
            time = 2; lab.Pump(); Check(count == 4, "Bandwidth did not serialize bytes");
        });
        Test("Seeded jitter reproduces scheduled timing", () =>
        {
            object RunSeed()
            {
                var lab = new TestHarness(new TestSettings { Active = true, Seed = 8, RequestDelayMs = 100, JitterMs = 50 }, () => 0);
                for (var i = 0; i < 20; ++i) lab.Schedule(2, "List", 10, () => { });
                return lab.Metrics.Snapshot();
            }
            Check(JToken.DeepEquals(JObject.FromObject(RunSeed()), JObject.FromObject(RunSeed())), "Seeded traces differ");
        });
        Test("Targeted outage does not block another peer, including already queued packets", () =>
        {
            double time = 0; var a = 0; var b = 0;
            var lab = new TestHarness(new TestSettings { Active = true, TargetPeer = 2 }, () => time);
            lab.Schedule(2, "List", 1, () => a++);
            lab.Outage(10);
            lab.Schedule(3, "List", 1, () => b++);
            lab.Pump(); Check(a == 0 && b == 1, "Outage leaked across peers");
            time = 10; lab.Pump(); Check(a == 1, "Outage never recovered");
        });
        Test("Hold, drop, release, disable, and overflow remain explicit and bounded", () =>
        {
            double time = 0; var delivered = 0;
            var lab = new TestHarness(new TestSettings { Active = true, ReplyDelayMs = 5000, MaxScheduled = 2 }, () => time);
            lab.HoldNext = "DepositReply";
            lab.Schedule(2, "DepositReply", 1, () => delivered++);
            lab.DropNext = "Reply";
            lab.Schedule(2, "ListReply", 1, () => delivered++);
            Check(lab.Held == 1 && lab.Queued == 0, "Fault selection wrong");
            lab.Release(); lab.Pump(); Check(delivered == 1, "Held reply not released");
            for (var i = 0; i < 3; ++i) lab.Schedule(2, "ListReply", 1, () => delivered++);
            Check(lab.Queued == 2, "Queue limit ignored");
            lab.DisableFaults(); lab.Pump(); Check(delivered == 3 && !lab.Settings.Active, "Disable did not drain outstanding packets");
            var counts = JObject.FromObject(lab.Metrics.Snapshot())["Counters"]!;
            Check(counts["discarded"]!.Value<int>() == 1 && counts["schedulerOverflow"]!.Value<int>() == 1, "Loss was not reported");
        });
        Test("Cancellation drops old-session callbacks and reports pending operations", () =>
        {
            var delivered = 0;
            var lab = new TestHarness(new TestSettings { Active = true, RequestDelayMs = 1000 }, () => 0);
            lab.Start(1, "Deposit"); lab.Schedule(2, "Deposit", 1, () => delivered++);
            lab.Cancel(); lab.DisableFaults(); lab.Pump();
            Check(delivered == 0 && lab.Pending == 1, "Cancelled transfer was silently reconciled");
        });
        Test("Real RPC reply loss after mutation stays uncertain; late held reply completes once", RpcFaults);
        Test("Save failures retry and shutdown persists acknowledged operations", SaveFailure);
        Test("Expired refresh is terminal while lost mutations stay pending", () =>
        {
            double time = 0;
            var lab = new TestHarness(new TestSettings(), () => time);
            lab.Start(1, "List"); lab.Start(2, "Deposit"); time = 10;
            lab.ExpireRefresh(1, "timeout"); lab.IgnoreRefresh(1); lab.Complete(1, "");
            Check(lab.Pending == 1 && lab.PendingRefreshes == 0 && lab.PendingMutations == 1,
                "Refresh timeout changed uncertain mutation state");
            lab.RefreshRetry(3); lab.Start(3, "List"); time = 12; lab.Complete(3, "");
            var report = JObject.FromObject(lab.Snapshot());
            Check(report["Metrics"]!["Counters"]!["completed"]!.Value<int>() == 1 &&
                report["Metrics"]!["Samples"]!["roundTripMs"]!["Count"]!.Value<int>() == 1,
                "Late expired reply became a successful RTT");
            Check(report["Metrics"]!["Trace"]!.Any(e => e["Kind"]?.Value<string>() == "refreshExpired" &&
                e["Outcome"]?.Value<string>() == "timeout"), "Terminal timeout history missing");
            for (var i = 4; i < 11000; i++) lab.Start(i, "List");
            Check(lab.Pending == 10000 && JObject.FromObject(lab.Snapshot())["Metrics"]!["Counters"]!["correlationEvicted"] != null,
                "Correlation storage grew without bound or silently evicted history");
        });
        Test("Report retains settings and bounded telemetry", () =>
        {
            var lab = new TestHarness(new TestSettings { Active = true }, () => 1);
            lab.Start(1, "List"); lab.Start(2, "list");
            var counters = JObject.FromObject(lab.Metrics.Snapshot(false))["Counters"]!;
            Check(counters.Count() == 1 && counters["requests.list"]!.Value<int>() == 2,
                "Counter keys differ only by case, which prevents Windows PowerShell JSON import");
            for (var i = 0; i < 11000; ++i) { lab.Metrics.Observe("latency", i); lab.Metrics.Event(i); }
            var before = JObject.FromObject(lab.Snapshot());
            lab.DisableFaults();
            Check(before["Settings"]!["Active"]!.Value<bool>(), "Snapshot settings were mutable");
            var metrics = JObject.FromObject(lab.Metrics.Snapshot());
            Check(metrics["Trace"]!.Count() == 10000 && metrics["Samples"]!["latency"]!["Count"]!.Value<int>() == 8192, "Telemetry grew without bound");
        });
        System.Console.WriteLine("11 lab checks passed.");
        return 0;
    }

    private static void Until(RpcApi api, Func<bool> done, int timeout = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) { api.Pump(); if (watch.ElapsedMilliseconds > timeout) throw new Exception("Lab check timed out"); Thread.Sleep(1); }
    }
    private static void WithApi(Action<RpcApi, TestHarness, List<TransferReply>, List<ListReply>, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "sharedinventory-lab-check-" + Guid.NewGuid());
        var oldPath = Paths.ConfigPath;
        Paths.ConfigPath = root;
        Directory.CreateDirectory(root);
        var lab = new TestHarness(new TestSettings { Active = true });
        var transfers = new List<TransferReply>(); var lists = new List<ListReply>();
        var api = new RpcApi(new ZRoutedRpc(), new ManualLogSource(), lists.Add, transfers.Add, transfers.Add, _ => { }, lab);
        try { action(api, lab, transfers, lists, root); }
        finally { api.Stop(); RpcApi.FinishShutdown(); Paths.ConfigPath = oldPath; Directory.Delete(root, true); }
    }
    private static void RpcFaults() => WithApi((api, lab, transfers, lists, root) =>
    {
        lab.DropNext = "DepositReply";
        api.Deposit(1, "Wood", 10, 3);
        Until(api, () => JObject.FromObject(lab.Metrics.Snapshot())["Counters"]?["discarded"]?.Value<int>() == 1);
        api.List(2); Until(api, () => lists.Count == 1);
        Check(transfers.Count == 0 && lists[0].Items["Wood"] == 10 && lab.Pending == 1, "Lost reply did not reflect uncertain committed transfer");
        lab.HoldNext = "WithdrawalReply";
        api.Withdrawal(3, "Wood", 4);
        Until(api, () => lab.Held == 1);
        api.List(4); Until(api, () => lists.Count == 2);
        Check(lists[1].Items["Wood"] == 6 && transfers.Count == 0, "Held reply blocked server mutation");
        lab.Release(); Until(api, () => transfers.Count == 1);
        Check(transfers[0].Amount == 4 && transfers[0].Quality == 3 && lab.Pending == 1, "Late reply did not complete exactly once");
    });
    private static void SaveFailure() => WithApi((api, lab, transfers, lists, root) =>
    {
        lab.Settings.SaveFailures = 1;
        api.Deposit(1, "Stone", 12, 2);
        Until(api, () => transfers.Count == 1);
        Until(api, () => JObject.FromObject(lab.Metrics.Snapshot())["Counters"]?["injectedSaveFailures"]?.Value<int>() == 1);
        lab.DisableFaults();
        api.Deposit(2, "Stone", 7, 4);
        Until(api, () => transfers.Count == 2);
        api.Stop(); RpcApi.FinishShutdown();
        var saved = new DatabaseFile(Path.Combine(root, "SharedInventory", "1.json"));
        Check(saved.Database.Total("Stone") == 19 && saved.Database.Quality("Stone") == 4, "Final save did not retry after failure or drain worker");
    });
}
