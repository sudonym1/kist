using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

static class SessionTests
{
    static int checks;
    static readonly VaultIdentity Vault = new("world", "hird", Guid.Parse("3416fe35-f974-4d44-8f1b-5e2efc59c941"));
    static readonly Guid Stack = Guid.Parse("2816fe35-f974-4d44-8f1b-5e2efc59c941");
    static ItemRecord Item(int q) => new("unknown", q, 1, new byte[] { 0, 255 });
    static VaultState Initial(int capacity = 1) => new(Vault, capacity, new[] { new StackRecord(Stack, 0, Item(10)) });
    static ItemCommand Command(SessionAddress a, ItemCommandKind kind, int q, Guid? stack = null) => new(a, Guid.NewGuid(), stack ?? Stack, kind, Item(q));
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("M4: " + message); }
    static int Total(JournalMachineState s) => s.Current.Stacks.Sum(x => x.Item.Quantity);
    sealed class Clock : IReplicationClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public TimeSpan Elapsed { get; private set; }
        readonly List<(TimeSpan due, TaskCompletionSource<bool> done)> waits = new();
        public void Advance(int ms) { Elapsed += TimeSpan.FromMilliseconds(ms); UtcNow += TimeSpan.FromMilliseconds(ms); foreach (var w in waits.Where(w => w.due <= Elapsed).ToArray()) { w.done.TrySetResult(true); waits.Remove(w); } }
        public Task Delay(TimeSpan delay, CancellationToken token) { var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); token.Register(() => done.TrySetCanceled(token)); waits.Add((Elapsed + delay, done)); return done.Task; }
        public int Waits => waits.Count;
    }
    sealed class Memory : IJournalStorage
    {
        public JournalMachineState? State;
        public TaskCompletionSource<bool>? Block, Entered;
        public Task<JournalMachineState?> ReadStateAsync(CancellationToken t) => Task.FromResult(State);
        public async Task CommitAsync(StorageCommit c, CancellationToken t)
        {
            if (Block != null) { Entered!.TrySetResult(true); await Block.Task; }
            c.State.Replication.Validate(c.State);
            JournalMachineState.Recover(c.State.Checkpoint, c.State.Current, c.State.Head, c.State.Journal, c.State.Results, c.State.UsedStackIds, c.State.Replication);
            State = c.State;
        }
        public void Dispose() { }
    }
    sealed class Inventory : ICharacterInventory
    {
        public int Added, Removed, Calls;
        public bool Fail;
        public bool TryReserve(Guid r, Guid s, int q) => true;
        public InventoryResult Apply(CharacterEffect e) { Calls++; if (Fail) return InventoryResult.Failed; if (e.Kind == CharacterEffectKind.Add) Added += e.Item.Quantity; else Removed += e.Item.Quantity; return InventoryResult.Applied; }
        public bool ReleaseRejectedReservation(Guid r) => true;
    }
    static async Task<SessionReply> Active(ReplicationServer server, string player = "alice")
    {
        var login = await server.LoginAsync(player, Guid.NewGuid());
        Check(login.Status == SessionStatus.Ok && login.Session!.Phase == SessionPhase.LoggingIn, "login protected before activation");
        var ack = await server.AcknowledgeAsync(login.Session!.Address, login.Session.LoginMarker);
        Check(ack.Session!.Phase == SessionPhase.Active, "login barrier activates same UUID"); return login;
    }
    static async Task AckHead(ReplicationServer server, SessionAddress a)
    {
        var s = await server.ReadAsync(); var session = s.Replication.Sessions.Single(x => x.Address.Session == a.Session);
        await server.QueueDeliveryAsync(a, session.Acknowledged, 1000);
        var ack = await server.AcknowledgeAsync(a, s.Head); Check(ack.Status == SessionStatus.Ok, "cumulative processing ack");
    }
    public static async Task<int> Run()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("THORSKIST_SYNC_ARTIFACTS") ?? Path.GetTempPath(), "thorskist-m4-" + Guid.NewGuid());
        Directory.CreateDirectory(root); bool passed = false;
        try
        {
            await Barriers(); await Admission(); await Deadlines(); await Effects(); await FailedEffects(); await LostMessages(); await ClientOrdering(); await SlowObserver(); await Races(); await ModelSchedules(); await SeededSchedules();
            await Durable(Path.Combine(root, "durable.db"));
            await Uncertain(Path.Combine(root, "uncertain.db"));
            await CorruptProtocol(Path.Combine(root, "corrupt.db"));
            foreach (var op in new[] { "login", "ack", "suspend", "logout", "checkpoint", "restart" })
                foreach (var point in new[] { StorageBoundary.AfterCurrentWrite, StorageBoundary.AfterJournalWrite, StorageBoundary.BeforeCommit, StorageBoundary.AfterCommit })
                    await Crash(root, op, point);
            passed = true; Console.WriteLine($"PASS {checks} M4 session/fault assertions; 24 forced child-process terminations"); return checks;
        }
        finally { if (passed) Directory.Delete(root, true); else Console.Error.WriteLine("M4 retained artifacts: " + root); }
    }
    static async Task Barriers()
    {
        using var server = await ReplicationServer.OpenAsync(new Memory(), Initial(), new Clock(), 4);
        var id = Guid.NewGuid(); var login = await server.LoginAsync("alice", id); var a = login.Session!.Address;
        var retry = await server.LoginAsync("alice", id);
        Check(retry.Session!.Address.Session == a.Session && retry.Session.LoginMarker == login.Session.LoginMarker, "duplicate login stable UUID/barrier");
        Check((await server.LoginAsync("alice", Guid.NewGuid())).Status == SessionStatus.SessionExists, "no concurrent login");
        Check((await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 1))).Status == SessionStatus.InvalidSession, "catchup cannot mutate");
        Check((await server.CheckpointAsync()).Checkpoint.Sequence == 0, "login with no active clients protects C");
        await server.AcknowledgeAsync(a, login.Session.LoginMarker);
        var b = (await Active(server, "bob")).Session!.Address;
        var take = await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 2));
        await AckHead(server, b);
        Check((await server.CheckpointAsync()).Checkpoint.Sequence == 1, "participant minimum despite other ack");
        var logoutId = Guid.NewGuid(); var logout = await server.LogoutAsync(a, logoutId);
        Check((await server.ExecuteAsync(Command(a, ItemCommandKind.Deposit, 1))).Status == SessionStatus.InvalidSession, "logout orders after accepted work and rejects later work");
        Check((await server.CheckpointAsync()).Checkpoint.Sequence == 1, "logout keeps participant");
        Check((await server.LogoutAsync(a, logoutId)).Session!.LogoutMarker == logout.Session!.LogoutMarker, "duplicate logout stable barrier");
        Check((await server.AcknowledgeAsync(a, logout.Session.LogoutMarker)).Status == SessionStatus.InvalidAcknowledgment, "cannot acknowledge unqueued logout");
        await AckHead(server, a);
        var closed = await server.LogoutAsync(a, logoutId);
        Check(closed.Session!.Phase == SessionPhase.Closed, "lost logout completion retry stays closed");
        Check((await server.AcknowledgeAsync(a, logout.Session.LogoutMarker)).Status == SessionStatus.InvalidSession, "closed UUID cannot ack");
        await AckHead(server, b); var final = await server.CheckpointAsync();
        Check(final.Checkpoint.Sequence == final.Head && Total(final) == 8, "logout drains effects before pruning");
        Check((await server.ExecuteAsync(take.Command!.Result!.Command)).Command!.IsRetry, "closed-session known result remains queryable");
        var next = await server.LoginAsync("alice", Guid.NewGuid());
        Check(next.Session!.Address.Session != a.Session && next.Session.Baseline.Sequence == final.Head, "ordinary replacement captures new checkpoint");
    }
    static async Task Admission()
    {
        var memory = new Memory(); var clock = new Clock();
        using var server = await ReplicationServer.OpenAsync(memory, Initial(), clock, 1);
        var a = (await Active(server)).Session!.Address;
        var cmd = Command(a, ItemCommandKind.Withdraw, 2);
        memory.Block = new(TaskCreationOptions.RunContinuationsAsynchronously); memory.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = server.ExecuteAsync(cmd); await memory.Entered.Task;
        Check(!first.IsCompleted, "no result before durable commit");
        Check(ReferenceEquals(first, server.ExecuteAsync(cmd)), "queued retry shares admission slot");
        var other = Command(a, ItemCommandKind.Withdraw, 1);
        Check((await server.ExecuteAsync(other)).Status == SessionStatus.TransactionDepthExceeded, "executing work counts toward depth");
        memory.Block.SetResult(true); var accepted = await first; memory.Block = null;
        var before = await server.ReadAsync();
        var depthReply = await server.ExecuteAsync(other);
        Check(depthReply.Status == SessionStatus.TransactionDepthExceeded, "commit alone does not release depth");
        Check((await server.ReadAsync()).Head == before.Head && (await server.ReadAsync()).Results.Count == 1, "depth error changes no journal/result/stock");
        Check((await server.ExecuteAsync(cmd)).Command!.Result!.Entry.OperationId == accepted.Command!.Result!.Entry.OperationId, "committed retry bypasses full depth");
        var altered = new ItemCommand(a, cmd.RequestId, Stack, ItemCommandKind.Withdraw, Item(1));
        Check((await server.ExecuteAsync(altered)).Command!.Status == CommandStatus.RequestConflict, "altered retry rejected");
        var retryClock = new Clock(); int sends = 0;
        var retry = AdmissionRetry.ExecuteAsync(other, c => { sends++; Check(ReferenceEquals(c, other), "backoff preserves exact request"); return sends < 3 ? Task.FromResult(depthReply!) : server.ExecuteAsync(c); }, retryClock);
        // Async method reaches first fake delay synchronously.
        Check(sends == 1 && retryClock.Waits == 1, "depth retry waits asynchronously");
        retryClock.Advance(99); Check(sends == 1, "99ms cannot retry");
        await AckHead(server, a); retryClock.Advance(1);
        await Spin(() => sends == 2 && retryClock.Waits == 1);
        retryClock.Advance(100); var retried = await retry;
        Check(retried.Command!.Status == CommandStatus.Accepted && sends == 3, "repeated 100ms backoff then same request admitted");
        await server.ExcludeAsync(a, false);
        var b = (await Active(server, "bob")).Session!.Address;
        var rejection = await server.ExecuteAsync(Command(b, ItemCommandKind.Withdraw, 100));
        Check(rejection.Command!.Status == CommandStatus.InsufficientStock, "definitive rejection recorded");
        Check((await server.ExecuteAsync(Command(b, ItemCommandKind.Withdraw, 1))).Status == SessionStatus.Ok, "definitive rejection releases admission");
    }
    static async Task Spin(Func<bool> ready)
    {
        var stop = Stopwatch.StartNew(); while (!ready()) { if (stop.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("M4 scheduler stalled"); await Task.Yield(); }
    }
    static async Task Deadlines()
    {
        var clock = new Clock(); using var server = await ReplicationServer.OpenAsync(new Memory(), Initial(), clock, 10);
        var a = (await Active(server)).Session!.Address;
        clock.Advance(9999); Check((await server.TickAsync()).Head == 1, "no early heartbeat");
        clock.Advance(1); Check((await server.TickAsync()).Head == 2, "heartbeat at 10s");
        await server.QueueDeliveryAsync(a, 1, 1);
        await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 1));
        clock.Advance(10000); var busy = await server.TickAsync(); Check(busy.Journal.Last().Kind == JournalEntryKind.NoOp, "heartbeat during item activity");
        await server.QueueDeliveryAsync(a, 1, 100);
        clock.UtcNow += TimeSpan.FromDays(2); // Live expiry uses elapsed, not wall time.
        clock.Advance(19999); await server.TickAsync();
        Check((await server.ReadAsync()).Replication.Sessions.Single().Phase == SessionPhase.Active, "no early timeout and UTC jump ignored");
        clock.Advance(1); var timed = await server.TickAsync();
        var old = timed.Replication.Sessions.Single();
        Check(old.Phase == SessionPhase.Closed && Total(timed) == 10 && old.SuspendedUntil.HasValue, "40s idle detection compensates pending withdrawal");
        var expiry = old.SuspendedUntil; var head = timed.Head;
        await server.ExcludeAsync(a, true); Check((await server.ReadAsync()).Head == head && (await server.ReadAsync()).Replication.Sessions.Single().SuspendedUntil == expiry, "duplicate self-ban cannot return twice/extend cooldown");
        Check((await server.LoginAsync("alice", Guid.NewGuid())).Status == SessionStatus.Suspended, "reconnecting does not bypass identity cooldown");
        clock.Advance(29999); Check((await server.LoginAsync("alice", Guid.NewGuid())).Status == SessionStatus.Suspended, "cooldown before exact boundary");
        clock.Advance(1); var rejoin = await server.LoginAsync("alice", Guid.NewGuid()); Check(rejoin.Status == SessionStatus.Ok, "ordinary login at cooldown expiry");
        Check((await server.AcknowledgeAsync(a, head)).Status == SessionStatus.InvalidSession, "late old ack ignored");
        clock.Advance(29999); await server.TickAsync(); Check((await server.ReadAsync()).Replication.Sessions.Last().Phase == SessionPhase.LoggingIn, "login 29999ms protected");
        await server.LoginAsync("alice", rejoin.Session!.LoginRequest); clock.Advance(1); await server.TickAsync();
        Check((await server.ReadAsync()).Replication.Sessions.Last().Phase == SessionPhase.Closed, "retry cannot extend login deadline");
        Check((await server.CheckpointAsync()).Checkpoint.Sequence == (await server.ReadAsync()).Head, "timed out login cannot pin checkpoint");
        var clock2 = new Clock(); using var other = await ReplicationServer.OpenAsync(new Memory(), Initial(), clock2, 3);
        var b = (await Active(other)).Session!.Address;
        await other.ExecuteAsync(Command(b, ItemCommandKind.Withdraw, 1)); await other.QueueDeliveryAsync(b, 1, 10);
        clock2.Advance(20000); var logout = await other.LogoutAsync(b, Guid.NewGuid());
        clock2.Advance(10000); await other.TickAsync();
        Check((await other.ReadAsync()).Replication.Sessions.Single().Phase == SessionPhase.Closed, "logout cannot extend earlier ack deadline");
    }
    static async Task Effects()
    {
        var clock = new Clock(); using var server = await ReplicationServer.OpenAsync(new Memory(), Initial(), clock, 3);
        var login = await server.LoginAsync("alice", Guid.NewGuid()); var a = login.Session!.Address;
        var inventory = new Inventory(); var replica = new JournalReplica(inventory); int dispatches = 0;
        var client = new ReplicationClient(replica, f => { dispatches++; return Task.FromResult(f()); }, server.AcknowledgeAsync, address => server.ExcludeAsync(address, true), 16);
        Check(await client.BeginAsync(login), "client installs verified checkpoint");
        Check(await client.ProcessBatchAsync(a, login.Entries) == 1 && client.AccessEnabled, "client replays/acks login before enabling requests");
        var deposit = await server.ExecuteAsync(Command(a, ItemCommandKind.Deposit, 2));
        Check(inventory.Removed == 0, "response never authorizes removal");
        var delivery = await server.QueueDeliveryAsync(a, replica.Sequence, 16);
        Check(await client.ProcessBatchAsync(a, delivery.Entries) == 1 && inventory.Removed == 2, "verified committed deposit removes exact quantity");
        await client.ProcessBatchAsync(a, delivery.Entries); Check(inventory.Removed == 2 && inventory.Calls == 1, "duplicate delivery only reacks");
        var take = await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 3));
        delivery = await server.QueueDeliveryAsync(a, replica.Sequence, 16);
        await client.ProcessBatchAsync(a, delivery.Entries);
        Check(inventory.Added == 3, "later successful processing applies once");
        await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 1)); delivery = await server.QueueDeliveryAsync(a, replica.Sequence, 16);
        var e = delivery.Entries.Single(); var corrupt = new JournalEntry(e.Identity, e.Sequence, e.OperationId, e.RequestId, e.Kind, e.Actor, e.ChangedStack, e.DeletedStack, e.Effect, "bad");
        Check(await client.ProcessBatchAsync(a, new[] { corrupt }) == 0 && client.Stopped && !client.AccessEnabled, "checksum mismatch stops client immediately");
        Check(inventory.Added == 3 && Total(await server.ReadAsync()) == 9, "mismatch awards nothing and self-ban returns pending only");
        await client.RetrySuspensionAsync(); var settled = await server.CheckpointAsync();
        clock.Advance(30000); var rejoin = await server.LoginAsync("alice", Guid.NewGuid());
        Check(await client.BeginAsync(rejoin), "rejoin after cooldown needs no deposit reconciliation");
        await client.ProcessBatchAsync(rejoin.Session!.Address, rejoin.Entries);
        Check(client.AccessEnabled && inventory.Removed == 2 && inventory.Added == 3, "old settled effects never replay into new character session");
        Check(await client.ProcessBatchAsync(a, delivery.Entries) == 0, "stale delivery cannot damage replacement");
        // Out-of-order and loss defense: later sequence cannot be acknowledged/applied.
        await server.ExecuteAsync(Command(rejoin.Session.Address, ItemCommandKind.Withdraw, 1));
        await server.ExecuteAsync(Command(rejoin.Session.Address, ItemCommandKind.Withdraw, 1));
        delivery = await server.QueueDeliveryAsync(rejoin.Session.Address, replica.Sequence, 16);
        await client.ProcessBatchAsync(rejoin.Session.Address, new[] { delivery.Entries.Last() });
        Check(client.Stopped && inventory.Added == 3 && Total(await server.ReadAsync()) == 9, "gap/reordering fails closed and compensates");
        Check(dispatches >= 4, "verified effects use supplied bounded game dispatcher");
    }
    static async Task FailedEffects()
    {
        foreach (var kind in new[] { ItemCommandKind.Deposit, ItemCommandKind.Withdraw })
        {
            var clock = new Clock(); using var server = await ReplicationServer.OpenAsync(new Memory(), Initial(), clock, 3);
            var login = await server.LoginAsync("alice", Guid.NewGuid()); var a = login.Session!.Address;
            var inventory = new Inventory(); var replica = new JournalReplica(inventory);
            var client = new ReplicationClient(replica, f => Task.FromResult(f()), server.AcknowledgeAsync, x => server.ExcludeAsync(x, true));
            await client.BeginAsync(login); await client.ProcessBatchAsync(a, login.Entries);
            await server.ExecuteAsync(Command(a, kind, 2));
            var delivery = await server.QueueDeliveryAsync(a, 1, 10); inventory.Fail = true;
            Check(await client.ProcessBatchAsync(a, delivery.Entries) == 0 && client.Stopped && !client.AccessEnabled && replica.Sequence == 1, "failed effect stops processing/requests without ack");
            Check((await server.ReadAsync()).Replication.Settlements.Count == 0, "failed effect keeps admission pending");
            clock.Advance(30000); await server.TickAsync();
            Check(Total(await server.ReadAsync()) == (kind == ItemCommandKind.Deposit ? 12 : 10), "failed effect timeout retains deposits/returns withdrawals");
            clock.Advance(30000); var rejoin = await server.LoginAsync("alice", Guid.NewGuid());
            inventory.Fail = false; await client.BeginAsync(rejoin); await client.ProcessBatchAsync(rejoin.Session!.Address, rejoin.Entries);
            Check(client.AccessEnabled && inventory.Added == 0 && inventory.Removed == 0, "failed effect rejoin has no reconciliation gate or old removal");
        }
    }
    static async Task LostMessages()
    {
        var clock = new Clock(); using var server = await ReplicationServer.OpenAsync(new Memory(), Initial(), clock, 4);
        var login = await server.LoginAsync("alice", Guid.NewGuid()); var a = login.Session!.Address;
        var inventory = new Inventory(); var replica = new JournalReplica(inventory); bool loseAck = false, loseBan = false;
        var client = new ReplicationClient(replica, f => Task.FromResult(f()), async (address, sequence) =>
        {
            var reply = await server.AcknowledgeAsync(address, sequence);
            if (loseAck) { loseAck = false; throw new IOException("lost ack completion"); } return reply;
        }, async address =>
        {
            if (loseBan) { loseBan = false; throw new IOException("lost suspension request"); }
            return await server.ExcludeAsync(address, true);
        });
        await client.BeginAsync(login); await client.ProcessBatchAsync(a, login.Entries);
        await server.ExecuteAsync(Command(a, ItemCommandKind.Deposit, 1)); var delivery = await server.QueueDeliveryAsync(a, 1, 10);
        loseAck = true;
        try { await client.ProcessBatchAsync(a, delivery.Entries); throw new Exception("missing transport error"); } catch (IOException) { checks++; }
        await client.ProcessBatchAsync(a, delivery.Entries);
        Check(inventory.Removed == 1 && (await server.ReadAsync()).Replication.Settlements.Count == 1, "lost ack completion duplicate only reacks");
        clock.Advance(10000); await server.TickAsync(); delivery = await server.QueueDeliveryAsync(a, replica.Sequence, 10);
        var e = delivery.Entries.Single(); var bad = new JournalEntry(e.Identity, e.Sequence, e.OperationId, e.RequestId, e.Kind, e.Actor, null, null, null, "bad");
        loseBan = true;
        try { await client.ProcessBatchAsync(a, new[] { bad }); throw new Exception("missing suspension error"); } catch (IOException) { checks++; }
        Check(client.Stopped && (await server.ReadAsync()).Replication.Sessions.Single().Phase == SessionPhase.Active, "lost self-ban request stays locally stopped");
        await client.RetrySuspensionAsync(); var state = await server.ReadAsync(); var due = state.Replication.Sessions.Single().SuspendedUntil;
        clock.Advance(1000); await client.RetrySuspensionAsync();
        Check((await server.ReadAsync()).Replication.Sessions.Single().SuspendedUntil == due, "self-ban retry doesn't extend cooldown");
        clock.Advance(29000); var next = await server.LoginAsync("alice", Guid.NewGuid());
        var snapshot = next.Session!.Baseline;
        var brokenSnapshot = new JournalSnapshot(snapshot.Sequence, snapshot.State, "bad");
        var broken = new JournalReplica(new Inventory());
        Check(!broken.Install(next.Session.Address, brokenSnapshot, next.SettledEffects), "corrupt login snapshot fails closed");
        await server.ExcludeAsync(next.Session.Address, true);
        Check((await server.CheckpointAsync()).Checkpoint.Sequence == (await server.ReadAsync()).Head, "mismatch during login releases retention");
    }
    static async Task ClientOrdering()
    {
        using var server = await ReplicationServer.OpenAsync(new Memory(), Initial(), new Clock(), 3);
        var login = await server.LoginAsync("alice", Guid.NewGuid()); var a = login.Session!.Address;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool block = false; var inventory = new Inventory(); var replica = new JournalReplica(inventory);
        var client = new ReplicationClient(replica, async f => { if (block) { entered.TrySetResult(true); await release.Task; } return f(); }, server.AcknowledgeAsync, x => server.ExcludeAsync(x, true));
        await client.BeginAsync(login); await client.ProcessBatchAsync(a, login.Entries);
        await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 1)); await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 1));
        var delivery = await server.QueueDeliveryAsync(a, 1, 10); block = true;
        var first = client.ProcessBatchAsync(a, new[] { delivery.Entries[0] }); await entered.Task;
        var second = client.ProcessBatchAsync(a, new[] { delivery.Entries[1] });
        Check(!second.IsCompleted && inventory.Added == 0, "client invocation order survives asynchronous dispatcher");
        client.StopRequests(); release.SetResult(true); await Task.WhenAll(first, second);
        Check(inventory.Added == 2 && replica.Sequence == 3 && !client.AccessEnabled && !client.Stopped, "bounded ordered batches process while logout request latch stays disabled");
        Check(await client.BeginAsync(login) && replica.Sequence == 3 && !client.AccessEnabled, "duplicate login response cannot reset live replica or request latch");
        await server.ExcludeAsync(a, false);
        Check(!await client.BeginAsync(await server.LoginAsync("alice", login.Session.LoginRequest)) && client.Stopped, "closed duplicate login response stops the old client");
    }
    static async Task SlowObserver()
    {
        var clock = new Clock(); using var server = await ReplicationServer.OpenAsync(new Memory(), Initial(), clock, 2);
        var a = (await Active(server)).Session!.Address; var b = (await Active(server, "bob")).Session!.Address;
        await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 1)); await server.QueueDeliveryAsync(a, 1, 10);
        for (int i = 0; i < 8; i++)
        {
            Check((await server.ExecuteAsync(Command(b, ItemCommandKind.Withdraw, 1))).Command!.Status == CommandStatus.Accepted, "healthy client continues under slow observer's pending depth");
            await AckHead(server, b);
        }
        Check((await server.CheckpointAsync()).Checkpoint.Sequence == 1, "slow observer only pins retention");
        clock.Advance(30000); var state = await server.TickAsync();
        Check(state.Replication.Sessions.Single(s => s.Address.Session == b.Session).Phase == SessionPhase.Active && Total(state) == 2,
            "slow observer timeout returns only its pending stock");
        Check((await server.CheckpointAsync()).Checkpoint.Sequence < state.Head, "healthy observer must acknowledge compensation before pruning");
        await AckHead(server, b); Check((await server.CheckpointAsync()).Checkpoint.Sequence == (await server.ReadAsync()).Head, "healthy client eventually reaches compensated head");
    }
    static async Task Uncertain(string path)
    {
        bool fail = false; var clock = new Clock(); ItemCommand command;
        using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, Vault, p => { if (fail && p == StorageBoundary.AfterCommit) throw new IOException("completion lost"); }), Initial(), clock, 1))
        {
            var a = (await Active(server)).Session!.Address;
            command = Command(a, ItemCommandKind.Deposit, 2); fail = true;
            try { await server.ExecuteAsync(command); throw new Exception("expected uncertain commit"); } catch (IOException) { checks++; }
            try { await server.ReadAsync(); throw new Exception("faulted writer read allowed"); } catch (InvalidOperationException) { checks++; }
        }
        using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, Vault), Initial(), clock, 1))
        {
            var query = await server.ExecuteAsync(command);
            Check(query.Command!.IsRetry && Total(await server.ReadAsync()) == 12, "uncertain deposit completion recovered/queryable without second credit");
            Check((await server.ReadAsync()).Replication.Settlements.Single().Kind == SettlementKind.DepositCompleted, "restart settles uncertain removal");
        }
    }
    static async Task CorruptProtocol(string path)
    {
        using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, Vault), Initial(), new Clock(), 2)) await Active(server);
        using (var first = new SqliteJournalStorage(path, Vault))
        using (var second = new SqliteJournalStorage(path, Vault))
        {
            var stale = (await first.ReadStateAsync(default))!;
            var changed = new ReplicationState(stale.Replication.Revision + 1, stale.Replication.NextHeartbeat, stale.Replication.Sessions, stale.Replication.Settlements);
            var newer = JournalMachineState.Recover(stale.Checkpoint, stale.Current, stale.Head, stale.Journal, stale.Results, stale.UsedStackIds, changed);
            await second.CommitAsync(new StorageCommit(stale.Head, newer, stale.Replication.Revision), default);
            try { await first.CommitAsync(new StorageCommit(stale.Head, stale, stale.Replication.Revision), default); throw new Exception("stale ack accepted"); } catch (InvalidOperationException) { checks++; }
        }
        using (var db = SqliteConnections.Open(path)) { using var q = db.CreateCommand(); q.CommandText = "UPDATE replication SET body=zeroblob(8);"; q.ExecuteNonQuery(); }
        using (var storage = new SqliteJournalStorage(path, Vault))
        { try { await storage.ReadStateAsync(default); throw new Exception("corrupt protocol accepted"); } catch (InvalidDataException) { checks++; } }
    }
    static async Task Races()
    {
        foreach (bool ackFirst in new[] { true, false })
        {
            using var server = await ReplicationServer.OpenAsync(new Memory(), Initial(), new Clock(), 5);
            var a = (await Active(server)).Session!.Address;
            var b = (await Active(server, "bob")).Session!.Address;
            var take = await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 3));
            await server.ExecuteAsync(Command(b, ItemCommandKind.Withdraw, 2));
            await server.QueueDeliveryAsync(a, 1, 10);
            Task one = ackFirst ? server.AcknowledgeAsync(a, take.Command!.Result!.Entry.Sequence) : server.ExcludeAsync(a, true);
            Task two = ackFirst ? server.ExcludeAsync(a, true) : server.AcknowledgeAsync(a, take.Command!.Result!.Entry.Sequence);
            await Task.WhenAll(one, two);
            var state = await server.ReadAsync();
            Check(Total(state) == (ackFirst ? 5 : 8), "ack/exclusion serialized outcome");
            Check(state.Replication.Settlements.Count == 1 && state.Results.Count == 2, "observer's unacknowledged operation unaffected");
            await server.ExcludeAsync(a, true); Check(Total(await server.ReadAsync()) == Total(state), "no repeated compensation");
        }
    }
    static async Task ModelSchedules()
    {
        // Independent scalar accounting: choose all 256 length-four schedules over
        // take/deposit/ack/exclude. Check after every event, including retries.
        for (int schedule = 0; schedule < 256; schedule++)
        {
            using var server = await ReplicationServer.OpenAsync(new Memory(), Initial(20), new Clock(), 3);
            var a = (await Active(server)).Session!.Address; int expected = 10, pendingReturns = 0, pending = 0; bool active = true;
            int events = schedule;
            for (int step = 0; step < 4; step++, events /= 4)
            {
                int action = events % 4;
                if (action <= 1)
                {
                    var command = Command(a, action == 0 ? ItemCommandKind.Withdraw : ItemCommandKind.Deposit, 1);
                    var reply = await server.ExecuteAsync(command);
                    if (!active) Check(reply.Status == SessionStatus.InvalidSession, "model excluded admission");
                    else if (pending == 3) Check(reply.Status == SessionStatus.TransactionDepthExceeded, "model depth limit");
                    else
                    {
                        Check(reply.Command!.Status == CommandStatus.Accepted, "model accepted transfer"); pending++;
                        if (action == 0) { expected--; pendingReturns++; } else expected++;
                        var retry = await server.ExecuteAsync(command); Check(retry.Command!.IsRetry, "model uncertain outcome retry");
                    }
                }
                else if (action == 2 && active) { await AckHead(server, a); pendingReturns = pending = 0; }
                else if (action == 3) { await server.ExcludeAsync(a, false); expected += pendingReturns; pendingReturns = pending = 0; active = false; }
                var state = await server.ReadAsync();
                Check(Total(state) == expected, $"model stock schedule={schedule} step={step}");
                Check(state.Results.Count(r => r.Entry.Effect != null && !state.Replication.Settlements.Any(x => x.Operation == r.Entry.OperationId)) == pending, "model pending effects");
                var checkpoint = await server.CheckpointAsync();
                Check(Total(checkpoint) == expected && JournalReplay.Replay(checkpoint.Checkpoint, checkpoint.Journal).Checksum == JournalChecksum.Compute(checkpoint.Current), "model checkpoint invariant");
            }
        }
    }
    static async Task SeededSchedules()
    {
        for (int seed = 0; seed < 12; seed++)
        {
            var random = new Random(seed); var clock = new Clock();
            var initial = new VaultState(Vault, 2, new[] { new StackRecord(Stack, 0, Item(1000)) });
            using var server = await ReplicationServer.OpenAsync(new Memory(), initial, clock, 5);
            var login = await Active(server); var a = login.Session!.Address;
            int total = 1000, primary = 1000, pending = 0, pendingReturns = 0, accepted = 0, overflow = 0;
            var known = new List<(ItemCommand command, Guid operation)>(); var trace = new List<int>();
            for (int step = 0; step < 100; step++)
            {
                int action = random.Next(9); trace.Add(action);
                string context = $"seed={seed} step={step} trace={string.Join(',', trace)}";
                if (action <= 1)
                {
                    int delta = action == 0 ? -1 : 1;
                    var command = Command(a, delta < 0 ? ItemCommandKind.Withdraw : ItemCommandKind.Deposit, 1);
                    var result = await server.ExecuteAsync(command);
                    if (pending == 5) Check(result.Status == SessionStatus.TransactionDepthExceeded, "seeded model admission " + context);
                    else
                    {
                        Check(result.Command!.Status == CommandStatus.Accepted, "seeded accepted " + context);
                        primary += delta; total += delta; pending++; accepted++; if (delta < 0) pendingReturns++;
                        known.Add((command, result.Command.Result!.Entry.OperationId));
                    }
                }
                else if (action == 2) { await AckHead(server, a); pending = pendingReturns = 0; }
                else if (action == 3 || action == 4)
                {
                    if (action == 3) await server.ExcludeAsync(a, false);
                    else
                    {
                        clock.Advance(10000); await server.TickAsync();
                        var session = (await server.ReadAsync()).Replication.Sessions.Single(x => x.Address.Session == a.Session);
                        await server.QueueDeliveryAsync(a, session.Acknowledged, 1000);
                        clock.Advance(30000); await server.TickAsync(); clock.Advance(30000);
                    }
                    total += pendingReturns; overflow += pendingReturns; pending = pendingReturns = 0;
                    login = await Active(server); a = login.Session!.Address;
                }
                else if (action == 5) await server.CheckpointAsync();
                else if (action == 6 && known.Count > 0)
                {
                    var old = known[random.Next(known.Count)]; var retry = await server.ExecuteAsync(old.command);
                    Check(retry.Command!.IsRetry && retry.Command.Result!.Entry.OperationId == old.operation, "seeded old-session uncertain retry " + context);
                }
                else if (action == 7)
                {
                    await server.LogoutAsync(a, Guid.NewGuid()); await AckHead(server, a); pending = pendingReturns = 0;
                    login = await Active(server); a = login.Session!.Address;
                }
                else if (action == 8)
                    Check((await server.LoginAsync("alice", login.Session!.LoginRequest)).Session!.Address.Session == a.Session, "seeded duplicate login " + context);
                var state = await server.ReadAsync();
                Check(Total(state) == total && state.Current.Stacks.Single(s => s.Id == Stack).Item.Quantity == primary
                    && state.Current.Stacks.Where(s => s.Slot < 0).Sum(s => s.Item.Quantity) == overflow, "seeded independent stock/overflow ledger " + context);
                Check(state.Replication.Settlements.Count == accepted - pending, "seeded independent settlement ledger " + context);
                Check(state.Checkpoint.Sequence <= state.Replication.Sessions.Single(x => x.Address.Session == a.Session).Acknowledged, "seeded participant floor " + context);
            }
        }
    }
    static async Task Durable(string path)
    {
        var clock = new Clock(); SessionAddress a; Guid request; Guid operation;
        using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, Vault), Initial(), clock, 2))
        {
            var login = await Active(server); a = login.Session!.Address; request = login.Session.LoginRequest;
            var take = await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 10)); operation = take.Command!.Result!.Entry.OperationId;
            await server.ExecuteAsync(Command(a, ItemCommandKind.Deposit, 2, Guid.NewGuid()));
            Check(Total(await server.ReadAsync()) == 2, "full vault replacement before compensation");
        }
        using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, Vault), Initial(), clock, 2))
        {
            var recovered = await server.ReadAsync();
            Check(Total(recovered) == 12 && recovered.Current.Stacks.Any(s => s.Slot == -1 && s.Item.Quantity == 10), "restart returns unlimited overflow and retains deposit");
            Check(recovered.Replication.Sessions.Single().Phase == SessionPhase.Closed && recovered.Replication.Settlements.Count == 2, "restart atomically invalidates and settles");
            Check(recovered.Replication.Settlements.Single(s => s.Operation == operation).Kind == SettlementKind.WithdrawalReturned, "durable returned status");
            Check((await server.LoginAsync("alice", request)).Session!.Address.Session == a.Session, "restart duplicate login cannot reopen old UUID");
            var checkpoint = await server.CheckpointAsync(); Check(checkpoint.Checkpoint.Sequence == checkpoint.Head, "closed deposits don't pin checkpoint");
            var login = await Active(server); var next = login.Session!.Address;
            Check(login.SettledEffects.Count == 2, "login supplies durable per-item settlement identities");
            var overflow = (await server.ReadAsync()).Current.Stacks.Single(s => s.Slot < 0);
            Check((await server.ExecuteAsync(Command(next, ItemCommandKind.Withdraw, 1, overflow.Id))).Command!.Status == CommandStatus.Accepted, "overflow is usable");
            await server.ExcludeAsync(next, true);
        }
        using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, Vault), Initial(), new Clock(), 2))
            Check((await server.LoginAsync("alice", Guid.NewGuid())).Status == SessionStatus.Suspended, "cooldown survives server restart");
        // Schema 1 upgrade retains existing stock/results; no empty recreation.
        string oldPath = path + ".m3";
        using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(oldPath, Vault), Initial()))
            await writer.ExecuteAsync(Command(a, ItemCommandKind.Deposit, 2));
        using (var db = SqliteConnections.Open(oldPath)) { using var q = db.CreateCommand(); q.CommandText = "DROP TABLE replication; PRAGMA user_version=1;"; q.ExecuteNonQuery(); }
        using (var storage = new SqliteJournalStorage(oldPath, Vault))
        {
            var upgraded = (await storage.ReadStateAsync(default))!;
            Check(Total(upgraded) == 12 && upgraded.Results.Count == 1, "M3 schema upgrade preserves stock history");
        }
    }
    static async Task Crash(string root, string operation, StorageBoundary point)
    {
        string directory = Path.Combine(root, operation + "-" + point); Directory.CreateDirectory(directory); string path = Path.Combine(directory, "journal.db");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(SessionTests).Assembly.Location);
        foreach (string arg in new[] { "--m4-child", operation, point.ToString(), path }) start.ArgumentList.Add(arg);
        using (var child = Process.Start(start)!)
        {
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                string? signal = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Check(signal == "M4-BOUNDARY " + point, "child reaches production session boundary " + operation + "/" + point + ": " + signal);
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        }
        bool committed = point == StorageBoundary.AfterCommit;
        using (var storage = new SqliteJournalStorage(path, Vault))
        {
            var s = (await storage.ReadStateAsync(default))!;
            Check(Total(s) == (committed && (operation == "suspend" || operation == "restart") ? 12 : 9), "crash atomic quantity " + operation);
            Check(s.Replication.Settlements.Count == (committed && (operation == "ack" || operation == "suspend" || operation == "restart") ? 2 : 0), "crash atomic settlement " + operation);
            Check(s.Replication.Sessions.Count == (committed && operation == "login" ? 2 : 1), "crash atomic login registration");
            Check(s.Checkpoint.Sequence == (committed && operation == "checkpoint" ? 1 : 0), "crash checkpoint floor");
        }
        using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, Vault), Initial(), new Clock(), 3))
        {
            var s = await server.ReadAsync(); Check(Total(s) == (committed && operation == "ack" ? 9 : 12), "recovery settles only pending effects " + operation);
            Check(s.Replication.Sessions.All(x => x.Phase == SessionPhase.Closed), "restart closes every old participant");
            Check((await server.CheckpointAsync()).Checkpoint.Sequence == (await server.ReadAsync()).Head, "recovery releases all protection");
        }
        Console.WriteLine("M4 crash recovered: " + operation + "/" + point);
    }
    public static async Task Child(string[] args)
    {
        string operation = args[1]; var target = Enum.Parse<StorageBoundary>(args[2]); string path = args[3]; bool armed = false;
        void Boundary(StorageBoundary point)
        {
            if (!armed || point != target) return;
            Console.WriteLine("M4-BOUNDARY " + point); Console.Out.Flush(); using var never = new ManualResetEventSlim(false); never.Wait();
        }
        var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, Vault, Boundary), Initial(), new Clock(), 3);
        var a = (await Active(server)).Session!.Address;
        await server.ExecuteAsync(Command(a, ItemCommandKind.Withdraw, 3));
        await server.ExecuteAsync(Command(a, ItemCommandKind.Deposit, 2));
        await server.QueueDeliveryAsync(a, 1, 10);
        armed = true;
        if (operation == "login") await server.LoginAsync("bob", Guid.NewGuid());
        if (operation == "ack") await server.AcknowledgeAsync(a, 3);
        if (operation == "suspend") await server.ExcludeAsync(a, true);
        if (operation == "logout") await server.LogoutAsync(a, Guid.NewGuid());
        if (operation == "checkpoint") await server.CheckpointAsync();
        if (operation == "restart") { server.Dispose(); await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, Vault, Boundary), Initial(), new Clock(), 3); }
        throw new Exception("M4 child missed boundary");
    }
}
