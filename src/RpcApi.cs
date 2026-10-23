using System;
using System.Collections.Concurrent;
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
    private const string Prefix = "SharedInventory.v2.";
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

    public RpcApi(ZRoutedRpc router, ManualLogSource log,
        Action<ListReply> listReply, Action<TransferReply> depositReply, Action<TransferReply> withdrawalReply)
    {
        this.router = router;
        this.log = log;
        server = ZNet.instance.IsServer();
        // Capture Unity/game state once here; workers never access it.
        databasePath = server ? Path.Combine(Paths.ConfigPath, "SharedInventory",
            ZNet.instance.GetWorldUID() + ".json") : null;
        worker = new Worker(() => file?.Save(), error => log.LogError(error), previousShutdown);
        router.Register<ZPackage>(Prefix + "List", (sender, request) =>
        {
            if (server) worker.Post(() => OnList(sender, request));
        });
        router.Register<ZPackage>(Prefix + "Deposit", (sender, request) =>
        {
            if (server) worker.Post(() => OnTransfer(sender, request, true));
        });
        router.Register<ZPackage>(Prefix + "Withdrawal", (sender, request) =>
        {
            if (server) worker.Post(() => OnTransfer(sender, request, false));
        });
        router.Register<ZPackage>(Prefix + "ListReply", (sender, package) => worker.Post(() =>
        {
            var reply = DecodeList(package);
            main.Enqueue(() => listReply(reply));
        }));
        router.Register<ZPackage>(Prefix + "DepositReply", (sender, package) => worker.Post(() =>
        {
            var reply = DecodeTransfer(package);
            main.Enqueue(() => depositReply(reply));
        }));
        router.Register<ZPackage>(Prefix + "WithdrawalReply", (sender, package) => worker.Post(() =>
        {
            var reply = DecodeTransfer(package);
            main.Enqueue(() => withdrawalReply(reply));
        }));
    }

    public bool Connected => !stopped && ZNet.instance != null &&
        (server || ZNet.instance.GetServerPeer() != null);

    private long ServerId => server ? ZNet.GetUID() : ZNet.instance.GetServerPeer().m_uid;

    public void List(long requestId) => SendRequest("List", requestId, "", 0);
    public void Deposit(long requestId, string itemId, long amount, int quality) => SendRequest("Deposit", requestId, itemId, amount, quality);
    public void Withdrawal(long requestId, string itemId, long amount) => SendRequest("Withdrawal", requestId, itemId, amount);

    private void SendRequest(string method, long requestId, string itemId, long amount, int quality = 1)
    {
        if (!Connected) throw new InvalidOperationException("Not connected to a server.");
        var target = ServerId; // Resolve the connection on the main thread.
        worker.Post(() =>
        {
            var request = new ZPackage();
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
        main.Enqueue(() => router.InvokeRoutedRPC(target, Prefix + method, package));

    // Only the plugin's Update/LateUpdate call this. Never waits on the worker.
    public void Pump()
    {
        if (stopped || main.IsEmpty) return;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
        while (main.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception error) { log.LogError(error); }
            if (Stopwatch.GetTimestamp() >= deadline) break;
        }
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
        Send(sender, deposit ? "DepositReply" : "WithdrawalReply", reply);
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
        previousShutdown = worker.Stop();
    }

    // Shutdown can wait for persistence; gameplay never waits for the worker.
    public static void FinishShutdown() => previousShutdown.GetAwaiter().GetResult();
}
