using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;

static class CoordinatorTests
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static void Invalid(Action action, string message)
    { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
    public static async Task<int> Run()
    {
        checks = 0;
        Replies(); await Coordination(); await Correlation();
        Console.WriteLine($"PASS {checks} M7 coordination assertions"); return checks;
    }
    static void Replies()
    {
        var address = new SessionAddress("world", "vault", Guid.NewGuid(), "steam:123", Guid.NewGuid());
        var replies = new[] {
            new JournalReply(JournalMessage.Login, SessionStatus.Ok, address, SessionPhase.LoggingIn, marker: 1, transfer: Guid.NewGuid()),
            new JournalReply(JournalMessage.Command, SessionStatus.Ok, command: CommandStatus.Accepted, operation: Guid.NewGuid(), sequence: 1),
            new JournalReply(JournalMessage.Acknowledge, SessionStatus.Ok, address, SessionPhase.Active, 1),
            new JournalReply(JournalMessage.Suspend, SessionStatus.Ok, address, SessionPhase.Closed, 1, suspendedUntil: DateTimeOffset.UtcNow),
            new JournalReply(JournalMessage.Command, SessionStatus.TransactionDepthExceeded),
            new JournalReply(JournalMessage.Logout, SessionStatus.InvalidSession) };
        foreach (var reply in replies)
        {
            var bytes = reply.Encode(); var decoded = JournalReply.Decode(bytes);
            Check(decoded.Request == reply.Request && decoded.Status == reply.Status && decoded.Phase == reply.Phase
                && decoded.Command == reply.Command && decoded.Operation == reply.Operation && decoded.Sequence == reply.Sequence
                && decoded.Transfer == reply.Transfer && decoded.SuspendedUntil == reply.SuspendedUntil, "Compact reply roundtrip");
            for (int n = 0; n < bytes.Length; n++) Invalid(() => JournalReply.Decode(bytes.Take(n).ToArray()), "Truncated reply accepted");
            Invalid(() => JournalReply.Decode(bytes.Concat(new byte[] { 0 }).ToArray()), "Trailing reply accepted");
        }
        var bad = replies[0].Encode(); bad[2] = 2; Invalid(() => JournalReply.Decode(bad), "Reply boolean must be canonical");
        bad = replies[0].Encode(); bad[0] = 255; Invalid(() => JournalReply.Decode(bad), "Reply enum must be defined");
    }
    static JournalFrame Login(VaultIdentity vault, Guid id) => new JournalFrame(JournalMessage.Login, id, JournalWire.EncodeLogin(vault));
    static JournalFrame Control(JournalMessage kind, SessionAddress a, long sequence = 0) => new JournalFrame(kind, Guid.NewGuid(), JournalWire.EncodePosition(a, sequence));
    static JournalFrame Next(JournalSendQueue queue) => JournalWire.DecodeFrame(queue.Dequeue() ?? throw new Exception("Expected outgoing frame"));
    static JournalReply Reply(JournalSendQueue queue) => JournalReply.Decode(Next(queue).CopyBody());
    static async Task<SessionReply> Join(JournalServerConnection connection, JournalSendQueue queue, VaultIdentity vault, Clock clock, JournalTransferBudget budget)
    {
        var id = Guid.NewGuid(); await connection.ReceiveAsync(Login(vault, id));
        var reply = Reply(queue); Check(reply.Status == SessionStatus.Ok && reply.Transfer == id, "Login reply correlates immutable transfer");
        long reserved = budget.Used;
        await connection.ReceiveAsync(Login(vault, id)); var repeated = Reply(queue);
        Check(repeated.Transfer == reply.Transfer && repeated.Address!.Session == reply.Address!.Session
            && repeated.Marker == reply.Marker && budget.Used == reserved, "Login retry preserves snapshot transfer and budget");
        using var receiver = new JournalTransferReceiver(budget, clock);
        byte[]? image = null;
        for (int i = 0; i < 100 && image == null; i++)
        { await connection.PumpAsync(); var frame = Next(queue); Check(frame.Correlation == id, "Transfer identity"); image = receiver.Accept(frame); }
        var login = JournalWire.DecodeLoginImage(image ?? throw new Exception("Missing login image"));
        await connection.ReceiveAsync(Control(JournalMessage.Acknowledge, login.Session!.Address, login.Session.LoginMarker));
        Check(Reply(queue).Phase == SessionPhase.Active, "Acknowledgment activates session through compact reply");
        return login;
    }
    static async Task Coordination()
    {
        var clock = new Clock(); var v = new VaultIdentity("world", "vault", Guid.NewGuid()); var budget = new JournalTransferBudget();
        var item = new ItemRecord("Wood", 8, 110, new byte[24 * 1024]); var stack = Guid.NewGuid();
        using var server = await ReplicationServer.OpenAsync(new MemoryStorage(), new VaultState(v, 4, new[] { new StackRecord(stack, 0, item) }), clock, 1);
        var peer = new JournalPeerIdentity(new object(), "steam:123", 123, v); var binding = new JournalPeerBinding(peer);
        using var queue = new JournalSendQueue(); var c = new JournalServerConnection(server, peer, binding, queue, budget);
        var login = await Join(c, queue, v, clock, budget); var a = login.Session!.Address;
        Check(budget.Used == 0, "Outbound/inbound images released after complete transfer");
        var take = new ItemCommand(a, Guid.NewGuid(), stack, ItemCommandKind.Withdraw, new ItemRecord(item.Prefab, 1, item.FormatVersion, item.CopyPayload()));
        var takeFrame = new JournalFrame(JournalMessage.Command, take.RequestId, JournalWire.EncodeCommand(take));
        await c.ReceiveAsync(takeFrame); var accepted = Reply(queue);
        Check(accepted.Command == CommandStatus.Accepted && accepted.Sequence == 2 && accepted.Operation != Guid.Empty, "Committed command compact result");
        await c.ReceiveAsync(takeFrame); var retry = Reply(queue);
        Check(retry.Operation == accepted.Operation && retry.Sequence == accepted.Sequence, "Retry returns same operation");
        var blocked = new ItemCommand(a, Guid.NewGuid(), stack, ItemCommandKind.Withdraw, take.Item);
        await c.ReceiveAsync(new JournalFrame(JournalMessage.Command, blocked.RequestId, JournalWire.EncodeCommand(blocked)));
        Check(Reply(queue).Status == SessionStatus.TransactionDepthExceeded, "Unacknowledged initiating effect retains admission");
        Check((await server.ReadAsync()).Head == 2, "Depth rejection did not allocate sequence");
        await c.PumpAsync(); var delivery = Next(queue); var entries = JournalWire.DecodeEntries(delivery.CopyBody());
        Check(entries.Entries.Count == 1 && entries.Entries[0].Effect?.Item.Quantity == 1, "Committed effect delivered via journal only");
        await c.ReceiveAsync(Control(JournalMessage.Acknowledge, a, 2)); Check(Reply(queue).Status == SessionStatus.Ok, "Persist processing acknowledgment");
        // A deliberately unencodable local command fails before admission even
        // though the state machine itself supports opaque payloads of this size.
        var oversized = new ItemCommand(a, Guid.NewGuid(), stack, ItemCommandKind.Withdraw, new ItemRecord("Wood", 1, 110, new byte[JournalWire.MaximumItemBytes + 1]));
        Invalid(() => JournalDelivery.ValidateCommand(oversized), "Oversized command accepted");
        Check((await server.ReadAsync()).Head == 2, "Preflight failure leaves journal unchanged");
        // Queue full: retain the exact frame, register the deadline once, then
        // advance only when the queue accepts it.
        using var slowQueue = new JournalSendQueue(maximumFrames: 1);
        var slowPeer = new JournalPeerIdentity(new object(), "steam:456", 456, v);
        var slow = new JournalServerConnection(server, slowPeer, new JournalPeerBinding(slowPeer), slowQueue, budget);
        var observerLogin = await Join(slow, slowQueue, v, clock, budget);
        await c.PumpAsync(); var observerMarker = JournalWire.DecodeEntries(Next(queue).CopyBody()).Entries.Last().Sequence;
        await c.ReceiveAsync(Control(JournalMessage.Acknowledge, a, observerMarker)); Reply(queue);
        clock.Advance(TimeSpan.FromSeconds(10)); await server.TickAsync();
        await slow.PumpAsync(); // heartbeat fills the one-frame queue
        clock.Advance(TimeSpan.FromSeconds(10)); await server.TickAsync();
        await slow.PumpAsync(); // second heartbeat stays in the journal until the first is acknowledged
        var before = await server.ReadAsync();
        var oldest = before.Replication.Sessions.Single(s => s.Address.Session == observerLogin.Session!.Address.Session).Deadlines.Min(d => d.Due);
        await c.PumpAsync(); var fastEntries = JournalWire.DecodeEntries(Next(queue).CopyBody());
        Check(fastEntries.Entries.Count == 2, "Blocked peer does not stall independent fan-out");
        await c.ReceiveAsync(Control(JournalMessage.Acknowledge, a, fastEntries.Entries.Last().Sequence)); Reply(queue);
        clock.Advance(TimeSpan.FromSeconds(9)); await slow.PumpAsync();
        Check((await server.ReadAsync()).Replication.Sessions.Single(s => s.Address.Session == observerLogin.Session!.Address.Session).Deadlines.Min(d => d.Due) == oldest,
            "Retry does not extend oldest deadline");
        var first = Next(slowQueue);
        for (int i = 0; i < 32; i++) await slow.PumpAsync();
        Check(slowQueue.Dequeue() == null, "Slow application cannot accumulate deliveries even after transport drains");
        long firstThrough = JournalWire.DecodeEntries(first.CopyBody()).Entries.Last().Sequence;
        await slow.ReceiveAsync(Control(JournalMessage.Acknowledge, observerLogin.Session!.Address, firstThrough)); Reply(slowQueue);
        await slow.PumpAsync(); var second = Next(slowQueue);
        Check(JournalWire.DecodeEntries(first.CopyBody()).Entries.Last().Sequence < JournalWire.DecodeEntries(second.CopyBody()).Entries.First().Sequence,
            "Queue backpressure preserves delivery sequence");
        clock.Advance(TimeSpan.FromSeconds(30)); await server.TickAsync();
        Check((await server.ReadAsync()).Replication.Sessions.Single(s => s.Address.Session == observerLogin.Session!.Address.Session).Phase == SessionPhase.Closed,
            "Stalled observer excluded after fixed deadline");
        await slow.CloseAsync(); await slow.CloseAsync();
        // Multiple maximum-size effects split on bytes, not just count.
        var state = await server.ReadAsync();
        var all = state.Results.Select(r => r.Entry).ToArray();
        var prefix = JournalDelivery.EncodePrefix(a, all.Concat(all).ToArray());
        Check(prefix.Count == 1 && prefix.Body.Length <= JournalWire.MaximumFrameBody, "Maximum item effects split by bytes");
        await c.ReceiveAsync(Control(JournalMessage.Suspend, a)); var suspended = Reply(queue);
        Check(suspended.Phase == SessionPhase.Closed && suspended.SuspendedUntil.HasValue, "Self-suspension returns committed close status");
        Check(binding.Session == null, "Closed Kist UUID unbound without closing game connection");
        try { await c.ReceiveAsync(takeFrame); throw new Exception("Closed UUID accepted"); } catch (InvalidDataException) { checks++; }
        await c.ReceiveAsync(Login(v, Guid.NewGuid())); Check(Reply(queue).Status == SessionStatus.Suspended, "Reconnect cannot bypass account cooldown");
        clock.Advance(TimeSpan.FromSeconds(30)); await server.TickAsync();
        var rejoined = await Join(c, queue, v, clock, budget);
        Check(rejoined.Session!.Address.Session != a.Session, "Cooldown rejoin on same game connection gets a new UUID");
        await c.ReceiveAsync(Login(v, login.Session.LoginRequest));
        Check(Reply(queue).Phase == SessionPhase.Closed && binding.Session!.Session == rejoined.Session.Address.Session,
            "Retry of old login cannot unbind new session");
        var logout = Control(JournalMessage.Logout, rejoined.Session.Address);
        await c.ReceiveAsync(logout); var loggingOut = Reply(queue);
        Check(loggingOut.Phase == SessionPhase.LoggingOut && loggingOut.Marker > rejoined.Session.LoginMarker, "Logout creates ordered marker");
        await c.PumpAsync(); var logoutEntries = JournalWire.DecodeEntries(Next(queue).CopyBody());
        await c.ReceiveAsync(Control(JournalMessage.Acknowledge, rejoined.Session.Address, logoutEntries.Entries.Last().Sequence));
        Check(Reply(queue).Phase == SessionPhase.Closed && binding.Session == null, "Final logout ack releases session binding");
        await c.ReceiveAsync(logout);
        Check(Reply(queue).Phase == SessionPhase.Closed, "Lost logout completion can be retried without reopening UUID");
        await c.CloseAsync(); Check(budget.Used == 0, "Connection cleanup releases all transfer memory");
        try { await c.ReceiveAsync(takeFrame); throw new Exception("Closed connection accepted traffic"); } catch (ObjectDisposedException) { checks++; }
        using var abandonedQueue = new JournalSendQueue();
        var abandonedPeer = new JournalPeerIdentity(new object(), "steam:789", 789, v); var abandonedBinding = new JournalPeerBinding(abandonedPeer);
        var abandoned = new JournalServerConnection(server, abandonedPeer, abandonedBinding, abandonedQueue, budget);
        await abandoned.ReceiveAsync(Login(v, Guid.NewGuid())); Reply(abandonedQueue);
        Check(budget.Used > 0, "Incomplete transfer retains reserved image");
        clock.Advance(TimeSpan.FromSeconds(30)); await server.TickAsync(); await abandoned.PumpAsync();
        Check(budget.Used == 0 && abandonedBinding.Session == null && Reply(abandonedQueue).Phase == SessionPhase.Closed,
            "Login timeout releases stalled transfer without disconnecting game");
        await abandoned.CloseAsync();
        using var exhaustedQueue = new JournalSendQueue(); var tinyBudget = new JournalTransferBudget(1);
        var exhaustedPeer = new JournalPeerIdentity(new object(), "steam:999", 999, v); var exhaustedBinding = new JournalPeerBinding(exhaustedPeer);
        var exhausted = new JournalServerConnection(server, exhaustedPeer, exhaustedBinding, exhaustedQueue, tinyBudget);
        try { await exhausted.ReceiveAsync(Login(v, Guid.NewGuid())); throw new Exception("Outbound budget ignored"); }
        catch (InvalidDataException) { checks++; }
        Check(tinyBudget.Used == 0 && exhaustedBinding.Session != null, "Rejected image reservation allocates no retained transfer");
        var exhaustedAddress = exhaustedBinding.Session!;
        // Direct adapter closes bindings before its asynchronous authority
        // cleanup runs. The coordinator retains the address independently.
        exhaustedBinding.Close(); await exhausted.CloseAsync();
        Check((await server.ReadAsync()).Replication.Sessions.Single(s => s.Address.Session == exhaustedAddress.Session).Phase == SessionPhase.Closed,
            "Failed login cleanup releases protection even after adapter closes binding");
    }
    static async Task Correlation()
    {
        var clock = new Clock(); var sent = new List<byte[]>();
        using var client = new JournalRequestClient(bytes => { sent.Add((byte[])bytes.Clone()); return true; }, clock, 1);
        var vault = new VaultIdentity("world", "vault", Guid.NewGuid()); var frame = Login(vault, Guid.NewGuid());
        var first = client.RequestAsync(frame); var duplicate = client.RequestAsync(frame);
        Check(ReferenceEquals(first, duplicate) && sent.Count == 1, "Concurrent retries share bounded correlation");
        try { _ = client.RequestAsync(Login(vault, Guid.NewGuid())); throw new Exception("Unbounded pending requests"); } catch (IOException) { checks++; }
        try { _ = client.RequestAsync(new JournalFrame(JournalMessage.Login, frame.Correlation, Array.Empty<byte>())); throw new Exception("Payload conflict accepted"); } catch (InvalidOperationException) { checks++; }
        Invalid(() => client.AcceptReply(new JournalFrame(JournalMessage.Reply, frame.Correlation,
            new JournalReply(JournalMessage.Logout, SessionStatus.InvalidSession).Encode())), "Wrong-kind reply correlated");
        var response = new JournalFrame(JournalMessage.Reply, frame.Correlation, new JournalReply(JournalMessage.Login, SessionStatus.SessionExists).Encode());
        Check(client.AcceptReply(response) && (await first).Status == SessionStatus.SessionExists, "Reply completes exact request");
        Check(!client.AcceptReply(response), "Duplicate late reply ignored");
        var timed = client.RequestAsync(frame); clock.Advance(TimeSpan.FromSeconds(30));
        try { await timed; throw new Exception("Missing reply did not timeout"); } catch (TimeoutException) { checks++; }
        var uncertainRetry = client.RequestAsync(frame); Check(sent[0].SequenceEqual(sent.Last()), "Uncertain retry retains exact request bytes");
        client.Dispose(); try { await uncertainRetry; throw new Exception("Closed pending request survived"); } catch (IOException) { checks++; }
        using var backoff = new JournalRequestClient(bytes => { lock (sent) sent.Add(bytes); return true; }, clock);
        var a = new SessionAddress("world", "vault", vault.Epoch, "steam:123", Guid.NewGuid());
        var command = new ItemCommand(a, Guid.NewGuid(), Guid.NewGuid(), ItemCommandKind.Deposit, new ItemRecord("Wood", 1, 110, Array.Empty<byte>()));
        int starting = sent.Count; var execute = backoff.ExecuteAsync(command);
        backoff.AcceptReply(new JournalFrame(JournalMessage.Reply, command.RequestId, new JournalReply(JournalMessage.Command, SessionStatus.TransactionDepthExceeded).Encode()));
        await clock.WaitForDelay(TimeSpan.FromMilliseconds(100));
        clock.Advance(TimeSpan.FromMilliseconds(99)); Check(sent.Count == starting + 1, "No early admission retry");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => { lock (sent) return sent.Count == starting + 2; });
        Check(sent[starting].SequenceEqual(sent[starting + 1]), "Admission retry preserves request identity and payload");
        backoff.AcceptReply(new JournalFrame(JournalMessage.Reply, command.RequestId, new JournalReply(JournalMessage.Command, SessionStatus.InvalidSession).Encode()));
        Check((await execute).Status == SessionStatus.InvalidSession, "Definitive result stops depth retry");
    }
    static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(1); } }
    sealed class Clock : IReplicationClock
    {
        readonly object sync = new();
        readonly List<(TimeSpan due, TimeSpan duration, TaskCompletionSource<bool> done)> waits = new();
        public TimeSpan Elapsed { get; private set; }
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Elapsed;
        public Task Delay(TimeSpan d, CancellationToken token)
        {
            var t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => t.TrySetCanceled(token));
            lock (sync) waits.Add((Elapsed + d, d, t)); return t.Task;
        }
        public Task WaitForDelay(TimeSpan d) => Until(() => { lock (sync) return waits.Any(w => w.duration == d && !w.done.Task.IsCompleted); });
        public void Advance(TimeSpan d)
        {
            lock (sync)
            {
                Elapsed += d;
                foreach (var w in waits.Where(w => w.due <= Elapsed).ToArray()) { waits.Remove(w); w.done.TrySetResult(true); }
                waits.RemoveAll(w => w.done.Task.IsCompleted);
            }
        }
    }
    sealed class MemoryStorage : IJournalStorage
    {
        JournalMachineState? state;
        public Task<JournalMachineState?> ReadStateAsync(CancellationToken token) => Task.FromResult(state);
        public Task CommitAsync(StorageCommit commit, CancellationToken token) { state = commit.State; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
