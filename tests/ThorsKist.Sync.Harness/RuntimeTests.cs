using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;

static class RuntimeTests
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    public static async Task<int> Run()
    {
        checks = 0; await Lifetime(); await StorageFailure();
        Console.WriteLine($"PASS {checks} M7 runtime lifetime assertions"); return checks;
    }
    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(1); }
    }
    static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(1); }
    }
    static async Task Lifetime()
    {
        var clock = new Clock(); var storage = new Storage(); var v = new VaultIdentity("m7", "runtime", Guid.NewGuid());
        var item = new ItemRecord("Wood", 5, 110, Array.Empty<byte>()); var stack = Guid.NewGuid();
        var budget = new JournalTransferBudget();
        var runtime = await JournalVaultRuntime.OpenAsync(storage, new VaultState(v, 1, new[] { new StackRecord(stack, 0, item) }), clock, 1, budget, 1);
        var peer = new JournalPeerIdentity(new object(), "account", 123, v); var binding = new JournalPeerBinding(peer);
        using var outgoing = new JournalSendQueue();
        try
        {
            var request = Guid.NewGuid();
            await runtime.ReceiveAsync(peer, binding, outgoing, new JournalFrame(JournalMessage.Login, request, JournalWire.EncodeLogin(v)));
            var reply = JournalReply.Decode(JournalWire.DecodeFrame(outgoing.Dequeue()!).CopyBody());
            Check(reply.Status == SessionStatus.Ok && budget.Used > 0, "Runtime owns accepted login transfer");
            var otherPeer = new JournalPeerIdentity(new object(), "other", 456, v); var otherBinding = new JournalPeerBinding(otherPeer);
            using var otherQueue = new JournalSendQueue();
            try { await runtime.ReceiveAsync(otherPeer, otherBinding, otherQueue, new JournalFrame(JournalMessage.Login, Guid.NewGuid(), JournalWire.EncodeLogin(v))); throw new Exception("Connection bound ignored"); }
            catch (IOException) { checks++; }
            Check((await runtime.ReadAsync()).Head == 1, "Connection refusal allocates no login sequence");
            await clock.WaitForWaiters(2); clock.Advance(TimeSpan.FromMilliseconds(100));
            byte[]? bytes = null; await Until(() => (bytes ??= outgoing.Dequeue()) != null);
            using var receiver = new JournalTransferReceiver(new JournalTransferBudget(), clock);
            var login = JournalWire.DecodeLoginImage(receiver.Accept(JournalWire.DecodeFrame(bytes!))!);
            var a = login.Session!.Address;
            Check(budget.Used == 0, "Independent pump completes transfer without a game-thread call");
            await runtime.ReceiveAsync(peer, binding, outgoing, new JournalFrame(JournalMessage.Acknowledge, Guid.NewGuid(), JournalWire.EncodePosition(a, login.Session.LoginMarker)));
            Check(JournalReply.Decode(JournalWire.DecodeFrame(outgoing.Dequeue()!).CopyBody()).Phase == SessionPhase.Active, "Runtime login activates normally");
            await clock.WaitForWaiters(2); clock.Advance(TimeSpan.FromSeconds(10));
            await UntilAsync(async () => (await runtime.ReadAsync()).Journal.Any(e => e.Kind == JournalEntryKind.NoOp));
            var state = await runtime.ReadAsync();
            Check(state.Head == 2 && state.Current.Stacks.Single().Item.Quantity == 5, "Independent timer appends idle heartbeat");
            // Send a real item mutation and deliberately leave its effect unacked.
            var take = new ItemCommand(a, Guid.NewGuid(), stack, ItemCommandKind.Withdraw, new ItemRecord("Wood", 2, 110, Array.Empty<byte>()));
            await runtime.ReceiveAsync(peer, binding, outgoing, new JournalFrame(JournalMessage.Command, take.RequestId, JournalWire.EncodeCommand(take)));
            await runtime.DisconnectAsync(binding);
            state = await runtime.ReadAsync();
            Check(state.Current.Stacks.Sum(s => s.Item.Quantity) == 5 && state.Replication.Sessions.Single().Phase == SessionPhase.Closed,
                "Disconnect settles unacknowledged withdrawal before closing runtime connection");
            Check(runtime.ConnectionCount == 0 && binding.IsClosed && budget.Used == 0, "Disconnect releases connection, binding and transfer budget");
            // Capacity becomes available after the disconnected lease has settled.
            var freshPeer = new JournalPeerIdentity(new object(), "account", 123, v); var freshBinding = new JournalPeerBinding(freshPeer);
            using var freshQueue = new JournalSendQueue();
            await runtime.ReceiveAsync(freshPeer, freshBinding, freshQueue, new JournalFrame(JournalMessage.Login, Guid.NewGuid(), JournalWire.EncodeLogin(v)));
            Check(runtime.ConnectionCount == 1, "Reconnect obtains released connection slot");
            await clock.WaitForWaiters(2); clock.Advance(TimeSpan.FromSeconds(30));
            await UntilAsync(async () => (await runtime.ReadAsync()).Replication.Sessions.All(s => s.Phase == SessionPhase.Closed));
            Check((await runtime.ReadAsync()).Replication.Sessions.Last().SuspendedUntil.HasValue, "Login deadline suspends without Unity updates");
            // Closed direct binding also cleans up a peer without a special RPC.
            freshBinding.Close(); await clock.WaitForWaiters(2); clock.Advance(TimeSpan.FromMilliseconds(100));
            await Until(() => runtime.ConnectionCount == 0);
            Check(budget.Used == 0, "Externally closed binding releases stalled login image");
            var malformedPeer = new JournalPeerIdentity(new object(), "malformed", 789, v); var malformedBinding = new JournalPeerBinding(malformedPeer);
            using var malformedQueue = new JournalSendQueue();
            try { await runtime.ReceiveAsync(malformedPeer, malformedBinding, malformedQueue, new JournalFrame(JournalMessage.Login, Guid.NewGuid(), Array.Empty<byte>())); throw new Exception("Malformed frame accepted"); }
            catch (InvalidDataException) { checks++; }
            Check(runtime.ConnectionCount == 0 && malformedBinding.IsClosed, "Malformed ingress awaits peer cleanup without leaving a lease");
        }
        finally { await runtime.StopAsync(); }
        Check(storage.Disposed && runtime.ConnectionCount == 0 && budget.Used == 0, "Async world teardown disposes storage after settlements");
        Check(ReferenceEquals(runtime.StopAsync(), runtime.StopAsync()), "Concurrent shutdown callers share one completion");
    }
    static async Task StorageFailure()
    {
        var clock = new Clock(); var storage = new Storage(); var v = new VaultIdentity("m7", "fault", Guid.NewGuid());
        var budget = new JournalTransferBudget();
        var runtime = await JournalVaultRuntime.OpenAsync(storage, new VaultState(v, 1, Array.Empty<StackRecord>()), clock, 1, budget);
        var peer = new JournalPeerIdentity(new object(), "account", 123, v); var binding = new JournalPeerBinding(peer);
        using var queue = new JournalSendQueue();
        await runtime.ReceiveAsync(peer, binding, queue, new JournalFrame(JournalMessage.Login, Guid.NewGuid(), JournalWire.EncodeLogin(v)));
        storage.Fail = true;
        await clock.WaitForWaiters(2); clock.Advance(TimeSpan.FromMilliseconds(100));
        await Until(() => runtime.Failure != null);
        try { await runtime.ReceiveAsync(peer, binding, queue, new JournalFrame(JournalMessage.Login, Guid.NewGuid(), JournalWire.EncodeLogin(v))); throw new Exception("Faulted authority admitted login"); }
        catch (InvalidOperationException) { checks++; }
        try { await runtime.StopAsync(); throw new Exception("Storage failure hidden at shutdown"); }
        catch (InvalidOperationException) { checks++; }
        Check(storage.Disposed && budget.Used == 0 && runtime.ConnectionCount == 0, "Faulted authority still releases workers, transfer memory and provider");
    }
    sealed class Storage : IJournalStorage
    {
        public bool Disposed, Fail;
        JournalMachineState? state;
        public Task<JournalMachineState?> ReadStateAsync(CancellationToken token) => Task.FromResult(state);
        public Task CommitAsync(StorageCommit commit, CancellationToken token)
        { if (Disposed) throw new ObjectDisposedException("storage"); if (Fail) throw new IOException("Injected disk failure"); state = commit.State; return Task.CompletedTask; }
        public void Dispose() { Disposed = true; }
    }
    sealed class Clock : IReplicationClock
    {
        readonly object sync = new();
        readonly List<(TimeSpan due, TaskCompletionSource<bool> done)> waits = new();
        TimeSpan elapsed;
        public TimeSpan Elapsed { get { lock (sync) return elapsed; } }
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Elapsed;
        public Task Delay(TimeSpan duration, CancellationToken token)
        {
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => source.TrySetCanceled(token));
            lock (sync) waits.Add((elapsed + duration, source)); return source.Task;
        }
        public Task WaitForWaiters(int count) => Until(() => { lock (sync) return waits.Count(w => !w.done.Task.IsCompleted) >= count; });
        public void Advance(TimeSpan duration)
        {
            lock (sync)
            {
                elapsed += duration;
                foreach (var wait in waits.Where(w => w.due <= elapsed).ToArray()) { waits.Remove(wait); wait.done.TrySetResult(true); }
                waits.RemoveAll(w => w.done.Task.IsCompleted);
            }
        }
    }
}
