using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using SharedInventory;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--load") return LoadRunner.Run(args);
            if (args.Length > 0 && args[0] == "--lab-tests") return LabChecks.Run();
            if (args.Length == 3 && args[0] == "--verify-harness") return PatchChecks.CheckHarness(args[1], bool.Parse(args[2]));
            if (args.Length == 3 && args[0] == "--verify-release") return PatchChecks.CheckRelease(args[1], args[2]);
            if (args.Length == 3) return PatchChecks.Run(args[0], args[1], args[2]);
            ProtocolChecks();
            return EssenceChecks();
        }
        catch (Exception error)
        {
            System.Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static int ProtocolChecks()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sharedinventory-protocol-" + Guid.NewGuid()));
        Paths.ConfigPath = root;
        Directory.CreateDirectory(root);
        RpcApi? api = null;
        try
        {
            var deposits = new List<TransferReply>();
            var withdrawals = new List<TransferReply>();
            var batches = new List<BatchReply>();
            var lists = new List<ListReply>();
            var log = new ManualLogSource();
            api = new RpcApi(new ZRoutedRpc(), log, lists.Add, deposits.Add, withdrawals.Add, batches.Add);
            void Until(Func<bool> done)
            {
                var timer = Stopwatch.StartNew();
                while (!done())
                {
                    api.Pump();
                    if (timer.ElapsedMilliseconds > 5000) throw new Exception("RPC completion timed out");
                    Thread.Sleep(1);
                }
            }
            api.Deposit(1, "Wood", 20, 3); api.Deposit(2, "Stone", 2, 1);
            Until(() => deposits.Count == 2);
            Check(deposits[0].Total == 20 && deposits[0].Quality == 3, "Deposit round trip");

            api.Withdrawal(3, "Wood", 1);
            Until(() => withdrawals.Count == 1);
            Check(withdrawals[0].RequestId == 3 && withdrawals[0].Amount == 1 && withdrawals[0].Total == 19 && withdrawals[0].Quality == 3, "Single withdrawal envelope");

            api.Withdrawal(4, new Dictionary<string, long> { ["Wood"] = 4, ["Stone"] = 2 });
            Until(() => batches.Count == 1);
            Check(batches[0].Error.Length == 0 && batches[0].Totals["Wood"] == 15 && batches[0].Qualities["Wood"] == 3 && batches[0].Totals["Stone"] == 0, "Batch reply round trip");

            api.Withdrawal(5, new Dictionary<string, long> { ["Wood"] = 4, ["Stone"] = 2 });
            Until(() => batches.Count == 2);
            Check(batches[1].RequestId == 5 && batches[1].Error.Length > 0, "Rejected batch envelope");
            api.List(6); Until(() => lists.Count == 1);
            Check(lists[0].Items["Wood"] == 15 && !lists[0].Items.ContainsKey("Stone"), "Rejected batch charged no ingredients");

            api.Withdrawal(7, new Dictionary<string, long> { ["Wood"] = 1, ["Stone"] = -1 });
            Until(() => batches.Count == 3);
            Check(batches[2].Error.Length > 0, "Invalid batch rejection");
            api.Withdrawal(8, "Wood", -1); Until(() => withdrawals.Count == 2);
            Check(withdrawals[1].RequestId == 8 && withdrawals[1].Error.Length > 0, "Single error envelope");

            api.Withdrawal(9, new Dictionary<string, long> { ["Wood"] = 15 });
            api.Withdrawal(10, new Dictionary<string, long> { ["Wood"] = 15 });
            Until(() => batches.Count == 5);
            Check(batches[3].Error.Length == 0 && batches[4].Error.Length > 0, "Ordered competing batches");
            api.Stop(); RpcApi.FinishShutdown(); api = null;
            var saved = new DatabaseFile(Path.Combine(root, "SharedInventory", "1.json"));
            Check(saved.Database.List().Count == 0, "Final counts persisted");
            Check(log.Errors.Count == 4, "Unexpected worker/codec/dispatch error");
            System.Console.WriteLine("PASS production RPC round trips, exact batch competition, failure envelopes, main-thread dispatch, and shutdown persistence");
            return 0;
        }
        finally
        {
            api?.Stop(); RpcApi.FinishShutdown();
            if (root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    private static int EssenceChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "einherjar-essence-" + Guid.NewGuid());
        Paths.ConfigPath = root;
        var path = Path.Combine(root, "SharedInventory", "1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new DatabaseFile(path);
        file.Database.Deposit(ContentIds.Essence, 7);
        file.Database.Deposit("Wood", 9);
        file.Save();
        var deposits = new List<TransferReply>();
        var withdrawals = new List<TransferReply>();
        var batches = new List<BatchReply>();
        var lists = new List<ListReply>();
        var api = new RpcApi(new ZRoutedRpc(), new ManualLogSource(), lists.Add, deposits.Add, withdrawals.Add, batches.Add);
        try
        {
            api.Deposit(1, ContentIds.Essence, 1, 1);
            api.Withdrawal(2, ContentIds.Essence, 1);
            api.Withdrawal(3, new Dictionary<string, long> { ["Wood"] = 2, [ContentIds.Essence] = 1 });
            api.List(4);
            var timer = Stopwatch.StartNew();
            while (lists.Count == 0)
            {
                api.Pump();
                if (timer.ElapsedMilliseconds > 5000) throw new Exception("Essence protocol check timed out");
                Thread.Sleep(1);
            }
            Check(deposits.Count == 1 && deposits[0].Error.Length > 0, "Essence deposit must be rejected before mutation");
            Check(withdrawals.Count == 1 && withdrawals[0].Error.Length > 0, "Legacy essence cannot be withdrawn");
            Check(batches.Count == 1 && batches[0].Error.Length > 0, "Mixed essence batch must be rejected atomically");
            Check(lists[0].Items[ContentIds.Essence] == 7 && lists[0].Items["Wood"] == 9, "Rejected essence operations must preserve all stock");
            api.Stop(); RpcApi.FinishShutdown();
            var saved = new DatabaseFile(path);
            Check(saved.Database.Total(ContentIds.Essence) == 7 && saved.Database.Total("Wood") == 9, "Legacy essence must survive save and reload");
            System.Console.WriteLine("PASS essence RPC exclusion, mixed-batch atomicity, and legacy stock preservation across restart");
            return 0;
        }
        finally
        {
            api.Stop(); RpcApi.FinishShutdown();
            Directory.Delete(root, true);
        }
    }
}
