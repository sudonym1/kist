using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;

namespace SharedInventory;

// Valheim routing is main-thread-only. All package construction/parsing,
// database operations, and persistence run on the endpoint's ordered worker.
internal sealed class RpcApi
{
    private const string Prefix = "SharedInventory.v3.";
    private static Task previousShutdown = Task.CompletedTask;
    private readonly ZRoutedRpc router;
    private readonly ManualLogSource log;
    private readonly Worker worker;
    private readonly ConcurrentQueue<Action> main = new ConcurrentQueue<Action>();
    private readonly string? databasePath;
    private readonly bool server;
    private DatabaseFile? file; // Accessed only on the worker.
    private Exception? loadError;
    private bool stopped;
#if SHAREDINVENTORY_TESTING
    public readonly TestHarness? Testing;
    public int MainQueueDepth => main.Count;
    public int WorkerQueueDepth => worker.QueueDepth;
#endif

    public RpcApi(ZRoutedRpc router, ManualLogSource log,
        Action<ListReply> listReply, Action<TransferReply> depositReply, Action<TransferReply> withdrawalReply,
        Action<BatchReply> batchReply
#if SHAREDINVENTORY_TESTING
        , TestHarness? testing = null
#endif
        )
    {
        this.router = router;
        this.log = log;
#if SHAREDINVENTORY_TESTING
        Testing = testing;
#endif
        server = ZNet.instance.IsServer();
        // Capture Unity/game state once here; workers never access it.
        databasePath = server ? Path.Combine(Paths.ConfigPath, "SharedInventory",
            ZNet.instance.GetWorldUID() + ".json") : null;
        worker = new Worker(Save, error => log.LogError(error), previousShutdown);
#if SHAREDINVENTORY_TESTING
        worker.Testing = testing;
#endif
        router.Register<ZPackage>(Prefix + "List", (sender, request) =>
        {
            if (server) Receive(sender, "List", request, () => OnList(sender, request));
        });
        router.Register<ZPackage>(Prefix + "Deposit", (sender, request) =>
        {
            if (server) Receive(sender, "Deposit", request, () => OnTransfer(sender, request, true));
        });
        router.Register<ZPackage>(Prefix + "Withdrawal", (sender, request) =>
        {
            if (server) Receive(sender, "Withdrawal", request, () => OnWithdrawal(sender, request));
        });
        router.Register<ZPackage>(Prefix + "ListReply", (sender, package) => worker.Post(() =>
        {
            var reply = DecodeList(package);
            main.Enqueue(() =>
            {
#if SHAREDINVENTORY_TESTING
                Testing?.Complete(reply.RequestId, reply.Error);
#endif
                listReply(reply);
            });
        }));
        router.Register<ZPackage>(Prefix + "DepositReply", (sender, package) => worker.Post(() =>
        {
            var reply = DecodeTransfer(package);
            main.Enqueue(() =>
            {
#if SHAREDINVENTORY_TESTING
                Testing?.Complete(reply.RequestId, reply.Error);
#endif
                depositReply(reply);
            });
        }));
        router.Register<ZPackage>(Prefix + "WithdrawalReply", (sender, package) => worker.Post(() =>
        {
            if (package.ReadBool())
            {
                var batch = DecodeBatch(package);
                main.Enqueue(() =>
                {
#if SHAREDINVENTORY_TESTING
                    Testing?.Complete(batch.RequestId, batch.Error);
#endif
                    batchReply(batch);
                });
                return;
            }
            var reply = DecodeTransfer(new ZPackage(package.ReadByteArray()));
            main.Enqueue(() =>
            {
#if SHAREDINVENTORY_TESTING
                Testing?.Complete(reply.RequestId, reply.Error);
#endif
                withdrawalReply(reply);
            });
        }));
    }

    public bool Connected => !stopped && ZNet.instance != null &&
        (server || ZNet.instance.GetServerPeer() != null);

    private long ServerId => server ? ZNet.GetUID() : ZNet.instance.GetServerPeer().m_uid;

    public void List(long requestId) => SendRequest("List", requestId, "", 0);
    public void Deposit(long requestId, string itemId, long amount, int quality) => SendRequest("Deposit", requestId, itemId, amount, quality);
    public void Withdrawal(long requestId, string itemId, long amount) => SendRequest("Withdrawal", requestId, itemId, amount);

    public void Withdrawal(long requestId, IReadOnlyDictionary<string, long> amounts)
    {
        if (!Connected) throw new InvalidOperationException("Not connected to a server.");
        var target = ServerId;
#if SHAREDINVENTORY_TESTING
        Testing?.Start(requestId, "BatchWithdrawal");
#endif
        worker.Post(() =>
        {
            var request = new ZPackage();
            request.Write(true); // Exact batch, rather than a backpack withdrawal.
            request.Write(requestId);
            request.Write(amounts.Count);
            foreach (var pair in amounts) { request.Write(pair.Key); request.Write(pair.Value); }
            Send(target, "Withdrawal", request);
        });
    }

    private void SendRequest(string method, long requestId, string itemId, long amount, int quality = 1)
    {
        if (!Connected) throw new InvalidOperationException("Not connected to a server.");
        var target = ServerId; // Resolve the connection on the main thread.
#if SHAREDINVENTORY_TESTING
        Testing?.Start(requestId, method);
#endif
        worker.Post(() =>
        {
            var request = new ZPackage();
            if (method == "Withdrawal") request.Write(false);
            request.Write(requestId);
            if (method != "List")
            {
                request.Write(itemId);
                request.Write(amount);
                if (method == "Deposit") request.Write(quality);
            }
            Send(target, method, request);
        });
    }

    private void Send(long target, string method, ZPackage package) =>
        main.Enqueue(() =>
        {
#if SHAREDINVENTORY_TESTING
            if (Testing != null && (!server || method.EndsWith("Reply", StringComparison.Ordinal)))
            {
                Testing.Schedule(target, method, package.Size(),
                    () => router.InvokeRoutedRPC(target, Prefix + method, package));
                return;
            }
#endif
            router.InvokeRoutedRPC(target, Prefix + method, package);
        });

    private void Receive(long sender, string method, ZPackage request, Action action)
    {
#if SHAREDINVENTORY_TESTING
        if (Testing != null)
        {
            Testing.Schedule(sender, method, request.Size(), () =>
            {
                if (Testing.AdmitServerRequest == null || Testing.AdmitServerRequest(sender, method, request.GetArray())) worker.Post(action);
            });
            return;
        }
#endif
        worker.Post(action);
    }

    // Only the plugin's Update/LateUpdate call this. Never waits on the worker.
    public void Pump()
    {
#if SHAREDINVENTORY_TESTING
        if (!stopped && Testing != null)
        {
            Testing.Metrics.Observe("mainQueueDepth", main.Count);
            Testing.Metrics.Observe("workerQueueDepth", worker.QueueDepth);
            Testing.Pump();
        }
#endif
        if (stopped || main.IsEmpty) return;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
        while (main.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception error) { log.LogError(error); }
            if (Stopwatch.GetTimestamp() >= deadline) break;
        }
    }

    private void Save()
    {
        if (file == null) return;
#if SHAREDINVENTORY_TESTING
        if (Testing == null) { file.Save(); return; }
        var timer = Stopwatch.StartNew();
        try
        {
            if (Testing?.Settings.Active == true)
            {
                var delay = System.Threading.Volatile.Read(ref Testing.Settings.SaveDelayMs);
                if (delay > 0) System.Threading.Thread.Sleep(delay);
                if (System.Threading.Volatile.Read(ref Testing.Settings.SaveFailures) > 0)
                {
                    System.Threading.Interlocked.Decrement(ref Testing.Settings.SaveFailures);
                    Testing.Metrics.Add("injectedSaveFailures");
                    throw new IOException("Injected test save failure");
                }
            }
            file.Save();
        }
        finally { Testing?.Metrics.Observe("saveMs", timer.Elapsed.TotalMilliseconds); }
#else
        file.Save();
#endif
    }

    private CountDatabase Database
    {
        get
        {
            if (loadError != null) throw new IOException("Shared inventory could not be loaded.", loadError);
            if (file == null)
            {
                try { file = new DatabaseFile(databasePath!); }
                catch (Exception error) { loadError = error; throw; }
            }
            return file.Database;
        }
    }

    private void OnList(long sender, ZPackage request)
    {
        var id = request.ReadLong();
        var reply = new ZPackage();
        reply.Write(id);
        try
        {
            var items = Database.List();
            reply.Write("");
            reply.Write(items.Count);
            foreach (var item in items)
            {
                reply.Write(item.Key);
                reply.Write(item.Value);
                reply.Write(Database.Quality(item.Key));
            }
        }
        catch (Exception error) { reply = ErrorReply(id, error); }
        Send(sender, "ListReply", reply);
    }

    private void OnTransfer(long sender, ZPackage request, bool deposit)
    {
        var id = request.ReadLong();
        var reply = new ZPackage();
        reply.Write(id);
        try
        {
            var item = request.ReadString();
            var amount = request.ReadLong();
            var db = Database;
            long transferred;
            var quality = deposit ? request.ReadInt() : db.Quality(item);
            if (deposit) { db.Deposit(item, amount, quality); quality = db.Quality(item); transferred = amount; }
            else transferred = db.Withdrawal(item, amount);
            reply.Write("");
            reply.Write(item);
            reply.Write(transferred);
            reply.Write(db.Total(item));
            reply.Write(quality);
        }
        catch (Exception error) { reply = ErrorReply(id, error); }
        if (!deposit)
        {
            var envelope = new ZPackage();
            envelope.Write(false);
            envelope.Write(reply.GetArray());
            Send(sender, "WithdrawalReply", envelope);
        }
        else Send(sender, "DepositReply", reply);
    }

    private void OnWithdrawal(long sender, ZPackage request)
    {
        if (!request.ReadBool()) { OnTransfer(sender, request, false); return; }
        var id = request.ReadLong();
        var reply = new ZPackage();
        try
        {
            var count = request.ReadInt();
            if (count <= 0) throw new ArgumentException("A withdrawal must contain items.");
            var amounts = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var i = 0; i < count; ++i)
            {
                var item = request.ReadString();
                var amount = request.ReadLong();
                if (amount <= 0) throw new ArgumentOutOfRangeException("amount");
                amounts[item] = checked((amounts.TryGetValue(item, out var previous) ? previous : 0) + amount);
            }
            var db = Database;
            if (!db.Withdrawal(amounts)) throw new InvalidOperationException("Shared materials are no longer available.");
            reply.Write(id);
            reply.Write("");
            reply.Write(amounts.Count);
            foreach (var pair in amounts)
            {
                reply.Write(pair.Key);
                reply.Write(db.Total(pair.Key));
                reply.Write(db.Quality(pair.Key));
            }
        }
        catch (Exception error) { reply = ErrorReply(id, error); }
        var envelope = new ZPackage();
        envelope.Write(true);
        envelope.Write(reply.GetArray());
        Send(sender, "WithdrawalReply", envelope);
    }

    private static BatchReply DecodeBatch(ZPackage envelope)
    {
        var package = new ZPackage(envelope.ReadByteArray());
        var reply = new BatchReply { RequestId = package.ReadLong(), Error = package.ReadString() };
        if (reply.Error.Length != 0) return reply;
        var count = package.ReadInt();
        for (var i = 0; i < count; ++i)
        {
            var item = package.ReadString();
            reply.Totals[item] = package.ReadLong();
            reply.Qualities[item] = package.ReadInt();
        }
        return reply;
    }

    private ZPackage ErrorReply(long id, Exception error)
    {
        log.LogError(error);
        var reply = new ZPackage();
        reply.Write(id);
        reply.Write(error.Message);
        return reply;
    }

    private static ListReply DecodeList(ZPackage package)
    {
        var reply = new ListReply { RequestId = package.ReadLong(), Error = package.ReadString() };
        if (reply.Error.Length != 0) return reply;
        var count = package.ReadInt();
        for (var i = 0; i < count; ++i)
        {
            var item = package.ReadString();
            reply.Items[item] = package.ReadLong();
            reply.Qualities[item] = package.ReadInt();
        }
        return reply;
    }

    private static TransferReply DecodeTransfer(ZPackage package)
    {
        var reply = new TransferReply { RequestId = package.ReadLong(), Error = package.ReadString() };
        if (reply.Error.Length == 0)
        {
            reply.ItemId = package.ReadString();
            reply.Amount = package.ReadLong();
            reply.Total = package.ReadLong();
            reply.Quality = package.ReadInt();
        }
        return reply;
    }

    public void Stop()
    {
        stopped = true;
#if SHAREDINVENTORY_TESTING
        Testing?.Cancel();
#endif
        previousShutdown = worker.Stop();
    }

    // Shutdown can wait for persistence; gameplay never waits for the worker.
    public static void FinishShutdown() => previousShutdown.GetAwaiter().GetResult();
}
