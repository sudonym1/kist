using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;

static class ClientBridgeTests
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    public static async Task<int> Run()
    {
        checks = 0; await RoundTrip(); await QueuedDisconnect(); await LargeRoundTrip();
        Console.WriteLine($"PASS {checks} M7 client/server bridge assertions"); return checks;
    }
    static Task Drive(Pair p, Func<bool> complete) => DriveAsync(p, () => Task.FromResult(complete()));
    static async Task DriveAsync(Pair p, Func<Task<bool>> complete)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await complete())
        {
            timeout.Token.ThrowIfCancellationRequested();
            for (int i = 0; i < 2; i++) { var bytes = p.ToServer.Dequeue(); if (bytes == null) break; await p.Connection.ReceiveAsync(JournalWire.DecodeFrame(bytes)); }
            await p.Connection.PumpAsync();
            for (int i = 0; i < 2; i++) { var bytes = p.ToClient.Dequeue(); if (bytes == null) break; await p.Client.ReceiveAsync(JournalWire.DecodeFrame(bytes)); }
            if (p.Client.Failure != null) throw new Exception("Client bridge fault", p.Client.Failure);
            await Task.Delay(1);
        }
    }
    static async Task RoundTrip()
    {
        var p = await Pair.Open();
        try
        {
            var loginId = Guid.NewGuid(); var login = p.Client.LoginAsync(p.Vault, loginId);
            await Drive(p, () => login.IsCompleted && p.Client.AccessEnabled);
            Check((await login).Status == SessionStatus.Ok && p.Client.Sequence == 1 && p.Inventory.Quantity == 10,
                "Wire login activates after barrier without a control/application deadlock");
            var a = p.Client.Address!; var stack = Guid.NewGuid(); var request = Guid.NewGuid();
            p.Inventory.TryReserve(request, Guid.NewGuid(), 2);
            var deposit = new ItemCommand(a, request, stack, ItemCommandKind.Deposit, new ItemRecord("Wood", 2, 110, Array.Empty<byte>()));
            var depositReply = p.Client.ExecuteAsync(deposit);
            await p.Connection.ReceiveAsync(JournalWire.DecodeFrame(p.ToServer.Dequeue()!));
            Check((await p.Server.ReadAsync()).Current.Stacks.Single().Item.Quantity == 2 && p.Inventory.Quantity == 10,
                "Wire command commit cannot remove reserved character items");
            await p.Client.ReceiveAsync(JournalWire.DecodeFrame(p.ToClient.Dequeue()!));
            Check((await depositReply).Command == CommandStatus.Accepted && p.Inventory.Quantity == 10, "Control success alone cannot authorize removal");
            await DriveAsync(p, async () => p.Client.Sequence == 2 && p.Inventory.Quantity == 8
                && (await p.Server.ReadAsync()).Replication.Sessions.Single().Acknowledged == 2);
            await Drive(p, () => p.Client.AccessEnabled);
            Check(p.Inventory.Applied == 1 && (await p.Server.ReadAsync()).Replication.Sessions.Single().Acknowledged == 2,
                "Verified removal precedes persisted acknowledgment over wire");
            // Duplicate result returns the same operation and does not replay a character effect.
            var retry = p.Client.ExecuteAsync(deposit); await Drive(p, () => retry.IsCompleted);
            Check((await retry).Operation == (await depositReply).Operation && p.Inventory.Quantity == 8 && p.Inventory.Applied == 1,
                "Wire retry preserves operation without another character removal");
            var take = new ItemCommand(a, Guid.NewGuid(), stack, ItemCommandKind.Withdraw, deposit.Item);
            var takeReply = p.Client.ExecuteAsync(take);
            await Drive(p, () => takeReply.IsCompleted && p.Client.Sequence == 3 && p.Inventory.Quantity == 10 && p.Inventory.Applied == 2);
            await DriveAsync(p, async () => (await p.Server.ReadAsync()).Replication.Sessions.Single().Acknowledged == 3);
            await Drive(p, () => p.Client.AccessEnabled);
            Check(p.Client.Current!.Stacks.Count == 0 && p.Inventory.Quantity == 10, "Withdrawal delta and exact character delivery both apply");
            var logoutId = Guid.NewGuid(); var logout = p.Client.LogoutAsync(logoutId);
            Check(!p.Client.AccessEnabled, "Logout disables new mutations immediately");
            await Drive(p, () => logout.IsCompleted && p.Client.ProcessingStopped && p.Binding.Session == null);
            Check((await logout).Phase == SessionPhase.LoggingOut, "Logout marker precedes final close acknowledgment");
            var logoutRetry = p.Client.LogoutAsync(logoutId); await Drive(p, () => logoutRetry.IsCompleted);
            Check((await logoutRetry).Phase == SessionPhase.Closed, "Wire logout completion retry reuses closed UUID");
            await p.Client.CloseAsync();
            p.Client = p.NewClient(action => Task.FromResult(action()));
            var rejoin = p.Client.LoginAsync(p.Vault, Guid.NewGuid()); await Drive(p, () => rejoin.IsCompleted && p.Client.AccessEnabled);
            Check(p.Client.Address!.Session != a.Session && p.Inventory.Quantity == 10 && p.Inventory.Applied == 2,
                "Fresh wire login replays old deltas without old character effects");
            p.Clock.Elapsed += TimeSpan.FromSeconds(10); await p.Server.TickAsync();
            await p.Connection.PumpAsync(); var heartbeat = JournalWire.DecodeFrame(p.ToClient.Dequeue()!); var corrupted = heartbeat.CopyBody(); corrupted[corrupted.Length - 1] ^= 1;
            await p.Client.ReceiveAsync(new JournalFrame(heartbeat.Message, heartbeat.Correlation, corrupted));
            await DriveAsync(p, async () => p.Client.RequiresSuspension && p.Client.ProcessingStopped
                && (await p.Server.ReadAsync()).Replication.Sessions.Last().SuspendedUntil.HasValue);
            var state = await p.Server.ReadAsync();
            Check(state.Replication.Sessions.Last().Phase == SessionPhase.Closed && state.Replication.Sessions.Last().SuspendedUntil.HasValue,
                "Checksum mismatch sends self-suspension while control receive continues");
            Check(p.Inventory.Quantity == 10 && !p.Client.AccessEnabled, "Mismatch cannot deliver items or reopen requests");
        }
        finally { await p.Close(); }
    }
    static async Task QueuedDisconnect()
    {
        Func<ReplicaResult>? pending = null;
        TaskCompletionSource<ReplicaResult>? completion = null;
        bool hold = false;
        var p = await Pair.Open(action =>
        {
            if (!hold) return Task.FromResult(action());
            pending = action; completion = new TaskCompletionSource<ReplicaResult>(TaskCreationOptions.RunContinuationsAsynchronously); return completion.Task;
        });
        try
        {
            var login = p.Client.LoginAsync(p.Vault, Guid.NewGuid()); await Drive(p, () => login.IsCompleted && p.Client.AccessEnabled);
            var a = p.Client.Address!; var request = Guid.NewGuid(); var stack = Guid.NewGuid();
            p.Inventory.TryReserve(request, Guid.NewGuid(), 2);
            hold = true;
            var command = p.Client.ExecuteAsync(new ItemCommand(a, request, stack, ItemCommandKind.Deposit, new ItemRecord("Wood", 2, 110, Array.Empty<byte>())));
            await Drive(p, () => command.IsCompleted && pending != null);
            var close = p.Client.SuspendAndCloseAsync();
            Check(!close.IsCompleted && !p.Client.AccessEnabled, "Client shutdown stops requests before queued Unity work completes");
            completion!.TrySetResult(pending!()); await Drive(p, () => close.IsCompleted); await close;
            Check(p.Inventory.Quantity == 10 && p.Inventory.Applied == 0 && p.Client.Sequence == 1,
                "Closing connection prevents an already queued inventory effect and its acknowledgment");
            await p.Connection.CloseAsync();
            Check((await p.Server.ReadAsync()).Current.Stacks.Single().Item.Quantity == 2,
                "Exclusion settles committed deposit without a later character removal");
            Check((await p.Server.ReadAsync()).Replication.Sessions.Single().SuspendedUntil.HasValue,
                "Explicit local invalidation persists suspension before closing control receive");
        }
        finally { await p.Close(); }
    }
    static async Task LargeRoundTrip()
    {
        var p = await Pair.Open();
        try
        {
            var login = p.Client.LoginAsync(p.Vault, Guid.NewGuid()); await Drive(p, () => login.IsCompleted && p.Client.AccessEnabled);
            var request = Guid.NewGuid(); var stack = Guid.NewGuid();
            p.Inventory.TryReserve(request, Guid.NewGuid(), 2);
            var item = new ItemRecord("ModItem", 2, 110, Enumerable.Range(0, JournalWire.MaximumItemBytes).Select(i => (byte)i).ToArray());
            var command = new ItemCommand(p.Client.Address!, request, stack, ItemCommandKind.Deposit, item);
            var accepted = p.Client.ExecuteAsync(command);
            await Drive(p, () => accepted.IsCompleted && p.Client.Sequence == 2);
            Check((await accepted).Command == CommandStatus.Accepted && p.Inventory.Quantity == 8, "256 KiB command and committed effect cross bounded chunk transfers");
            Check(JournalChecksum.SameItem(p.Client.Current!.Stacks.Single().Item, item), "Large delta preserves every opaque payload byte");
            var retry = p.Client.ExecuteAsync(command); await Drive(p, () => retry.IsCompleted);
            Check((await retry).Operation == (await accepted).Operation && p.Inventory.Applied == 1, "Chunked retry retains exact request identity without repeating removal");
            var take = p.Client.ExecuteAsync(new ItemCommand(p.Client.Address!, Guid.NewGuid(), stack, ItemCommandKind.Withdraw, item));
            await Drive(p, () => take.IsCompleted && p.Client.Sequence == 3);
            Check(p.Inventory.Quantity == 10 && p.Client.Current!.Stacks.Count == 0, "Chunked withdrawal applies exactly once");
        }
        finally { await p.Close(); }
    }
    sealed class Pair
    {
        public VaultIdentity Vault = null!; public ReplicationServer Server = null!;
        public JournalServerConnection Connection = null!; public JournalPeerBinding Binding = null!;
        public JournalClientConnection Client = null!;
        public readonly JournalSendQueue ToServer = new(), ToClient = new();
        public readonly Inventory Inventory = new(); public readonly Clock Clock = new();
        public JournalClientConnection NewClient(Func<Func<ReplicaResult>, Task<ReplicaResult>> dispatch)
            => new JournalClientConnection(Inventory, dispatch, ToServer, Clock, new JournalTransferBudget());
        public static async Task<Pair> Open(Func<Func<ReplicaResult>, Task<ReplicaResult>>? dispatch = null)
        {
            var p = new Pair(); p.Vault = new VaultIdentity("m7", "bridge", Guid.NewGuid());
            p.Server = await ReplicationServer.OpenAsync(new Storage(), new VaultState(p.Vault, 2, Array.Empty<StackRecord>()), p.Clock, 4);
            var peer = new JournalPeerIdentity(new object(), "account", 123, p.Vault); p.Binding = new JournalPeerBinding(peer);
            p.Connection = new JournalServerConnection(p.Server, peer, p.Binding, p.ToClient, new JournalTransferBudget());
            p.Client = p.NewClient(dispatch ?? (action => Task.FromResult(action()))); return p;
        }
        public async Task Close() { await Client.CloseAsync(); await Connection.CloseAsync(); Server.Dispose(); ToServer.Dispose(); ToClient.Dispose(); }
    }
    sealed class Clock : IReplicationClock
    {
        public TimeSpan Elapsed { get; set; }
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Elapsed;
        public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
    }
    sealed class Storage : IJournalStorage
    {
        JournalMachineState? state;
        public Task<JournalMachineState?> ReadStateAsync(CancellationToken token) => Task.FromResult(state);
        public Task CommitAsync(StorageCommit commit, CancellationToken token) { state = commit.State; return Task.CompletedTask; }
        public void Dispose() { }
    }
    sealed class Inventory : ICharacterInventory
    {
        public int Quantity = 10, Applied;
        readonly Dictionary<Guid, int> reserved = new();
        public bool TryReserve(Guid request, Guid stack, int quantity) { reserved[request] = quantity; return true; }
        public bool ReleaseRejectedReservation(Guid request) => reserved.Remove(request);
        public InventoryResult Apply(CharacterEffect effect)
        {
            if (effect.Kind == CharacterEffectKind.RemoveReserved)
            { if (!reserved.Remove(effect.RequestId, out var quantity) || quantity != effect.Item.Quantity) return InventoryResult.MissingReservation; Quantity -= quantity; }
            else Quantity += effect.Item.Quantity;
            Applied++; return InventoryResult.Applied;
        }
    }
}
