using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;

static class WireTests
{
    static int checks;
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Wire: " + message); checks++; }
    static void Invalid(Action action, string message)
    { try { action(); throw new Exception("Wire accepted " + message); } catch (InvalidDataException) { checks++; } }
    public static async Task<int> Run()
    {
        checks = 0;
        var vault = new VaultIdentity("world", "vault", Guid.NewGuid());
        var address = new SessionAddress(vault.World, vault.Vault, vault.Epoch, "steam:123", Guid.NewGuid());
        var item = new ItemRecord("unknown-mod-prefab", 3, 109, new byte[] { 0, 128, 255 });
        var command = new ItemCommand(address, Guid.NewGuid(), Guid.NewGuid(), ItemCommandKind.Deposit, item);
        var frame = new JournalFrame(JournalMessage.Command, command.RequestId, JournalWire.EncodeCommand(command));
        var encoded = JournalWire.EncodeFrame(frame);
        var decoded = JournalWire.DecodeCommand(JournalWire.DecodeFrame(encoded).CopyBody());
        Check(decoded.RequestId == command.RequestId && decoded.StackId == command.StackId && JournalChecksum.SameItem(decoded.Item, item), "command exact roundtrip");
        Check(JournalWire.DecodeFrame(encoded).Correlation == command.RequestId, "correlation roundtrip");
        var body = frame.CopyBody(); body[0] ^= 255;
        Check(frame.CopyBody()[0] != body[0], "immutable frame");
        for (int length = 0; length < encoded.Length; length++)
            Invalid(() => JournalWire.DecodeFrame(encoded.Take(length).ToArray()), "truncated frame " + length);
        Invalid(() => JournalWire.DecodeFrame(encoded.Concat(new byte[] { 0 }).ToArray()), "trailing frame");
        var wrongVersion = (byte[])encoded.Clone(); Array.Copy(BitConverter.GetBytes(JournalWire.Version + 1), 0, wrongVersion, 4, 4);
        Invalid(() => JournalWire.DecodeFrame(wrongVersion), "version");
        var wrongKind = (byte[])encoded.Clone(); wrongKind[8] = 255;
        Invalid(() => JournalWire.DecodeFrame(wrongKind), "opcode");
        var wrongSize = (byte[])encoded.Clone(); Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, wrongSize, 25, 4);
        Invalid(() => JournalWire.DecodeFrame(wrongSize), "declared size");
        for (int length = 0; length < frame.CopyBody().Length; length++)
            Invalid(() => JournalWire.DecodeCommand(frame.CopyBody().Take(length).ToArray()), "truncated command " + length);
        var large = new ItemRecord("ModItem", 1, 109, new byte[JournalWire.MaximumItemBytes + 1]);
        Invalid(() => JournalWire.EncodeCommand(new ItemCommand(address, Guid.NewGuid(), Guid.NewGuid(), ItemCommandKind.Deposit, large)), "large item");

        var connection = new object(); var current = new JournalPeerIdentity(connection, address.Player, 42, vault);
        var binding = new JournalPeerBinding(current);
        Invalid(() => JournalRequest.Decode(frame, current, binding), "command before login");
        Check(binding.Bind(current, address), "bind authenticated session");
        Check(JournalRequest.Decode(frame, current, binding).Command!.RequestId == command.RequestId, "bound command");
        foreach (var changed in new[] {
            new JournalPeerIdentity(new object(), address.Player, 42, vault),
            new JournalPeerIdentity(connection, "steam:other", 42, vault),
            new JournalPeerIdentity(connection, address.Player, 43, vault),
            new JournalPeerIdentity(connection, address.Player, 42, new VaultIdentity("other-world", vault.Vault, vault.Epoch)),
            new JournalPeerIdentity(connection, address.Player, 42, new VaultIdentity(vault.World, "other-vault", vault.Epoch)),
            new JournalPeerIdentity(connection, address.Player, 42, new VaultIdentity(vault.World, vault.Vault, Guid.NewGuid())) })
        {
            Check(!binding.IsCurrent(changed), "connection/account/character/world/vault/epoch change rejected");
            Invalid(() => JournalRequest.Decode(frame, changed, binding), "stale peer command");
        }
        Check(!binding.Bind(current, new SessionAddress(vault.World, vault.Vault, vault.Epoch, address.Player, Guid.NewGuid())), "no second session binding");
        var forged = new SessionAddress(vault.World, vault.Vault, vault.Epoch, "steam:other", address.Session);
        var forgedCommand = new ItemCommand(forged, command.RequestId, command.StackId, command.Kind, item);
        Invalid(() => JournalRequest.Decode(new JournalFrame(JournalMessage.Command, command.RequestId, JournalWire.EncodeCommand(forgedCommand)), current, binding), "forged player");
        Invalid(() => JournalRequest.Decode(new JournalFrame(JournalMessage.Command, Guid.NewGuid(), frame.CopyBody()), current, binding), "conflicting request correlation");
        foreach (var direction in new[] { JournalMessage.Reply, JournalMessage.Entries, JournalMessage.Transfer })
            Invalid(() => JournalRequest.Decode(new JournalFrame(direction, Guid.NewGuid(), Array.Empty<byte>()), current, binding), "client reply direction");
        var ack = new JournalFrame(JournalMessage.Acknowledge, Guid.NewGuid(), JournalWire.EncodePosition(address, 7));
        Check(JournalRequest.Decode(ack, current, binding).Sequence == 7, "ack bound to session");
        binding.Close(); Invalid(() => JournalRequest.Decode(ack, current, binding), "closed session ack");

        await LoginAndReplay(vault, address.Player, item);
        Transfers();
        await Workers();
        Console.WriteLine($"M6 wire/transfer/peer tests: {checks} assertions");
        return checks;
    }

    static async Task LoginAndReplay(VaultIdentity vault, string player, ItemRecord item)
    {
        var clock = new Clock();
        var initial = new VaultState(vault, 2, new[] { new StackRecord(Guid.NewGuid(), -1, item) });
        using var server = await ReplicationServer.OpenAsync(new MemoryStorage(), initial, clock, 8);
        var login = await server.LoginAsync(player, Guid.NewGuid());
        var image = JournalWire.EncodeLoginImage(login);
        var wireLogin = JournalWire.DecodeLoginImage(image);
        Check(wireLogin.Session!.Address.Session == login.Session!.Address.Session, "login session preserved");
        Check(wireLogin.Session.Baseline.State.Stacks.Single().Slot == -1 && JournalChecksum.SameItem(wireLogin.Session.Baseline.State.Stacks.Single().Item, item), "unknown overflow snapshot preserved");
        for (int n = 0; n < image.Length; n++) Invalid(() => JournalWire.DecodeLoginImage(image.Take(n).ToArray()), "login image truncation");
        var replica = new JournalReplica(new Inventory());
        var client = new ReplicationClient(replica, action => Task.FromResult(action()), server.AcknowledgeAsync, a => server.ExcludeAsync(a, true));
        Check(await client.BeginAsync(wireLogin), "wire login installs");
        var batch = JournalWire.DecodeEntries(JournalWire.EncodeEntries(wireLogin.Session.Address, wireLogin.Entries));
        Check(await client.ProcessBatchAsync(batch.Address, batch.Entries) == 1 && client.AccessEnabled, "wire login verified before activation");
        var a = wireLogin.Session.Address;
        var largest = new ItemRecord("ModItem", 1, 109, new byte[JournalWire.MaximumItemBytes]);
        var command = new ItemCommand(a, Guid.NewGuid(), Guid.NewGuid(), ItemCommandKind.Deposit, largest);
        var result = await server.ExecuteAsync(JournalWire.DecodeCommand(JournalWire.EncodeCommand(command)));
        var entry = result.Command!.Result!.Entry;
        await server.QueueDeliveryAsync(a, client.Sequence, 1);
        var delivery = JournalWire.DecodeEntries(JournalWire.EncodeEntries(a, new[] { entry }, true));
        Check(delivery.Entries[0].Effect != null && delivery.Entries[0].ChangedStack != null, "largest admitted item fits bounded delta and effect transfer");
        Check(await client.ProcessBatchAsync(a, delivery.Entries) == 1 && replica.Sequence == entry.Sequence, "wire item effect acknowledged");
        Check((await server.ReadAsync()).Current.Stacks.Count == 2, "overflow plus ordinary stack retained");
        var retry = await server.ExecuteAsync(command);
        Check(retry.Command!.Result!.Entry.OperationId == entry.OperationId, "wire request retains server dedup identity");
    }
    static void Transfers()
    {
        var image = new byte[JournalTransfer.ChunkBytes * 2 + 19];
        new Random(173).NextBytes(image); var original = (byte[])image.Clone();
        var transfer = new JournalTransfer(Guid.NewGuid(), image); image[0] ^= 255;
        var clock = new Clock(); var budget = new JournalTransferBudget(original.Length);
        using var receiver = new JournalTransferReceiver(budget, clock);
        Check(transfer.FrameCount == 3 && receiver.Accept(transfer.Frame(0)) == null, "bounded transfer start");
        Check(budget.Used == original.Length, "reserve global budget before allocation");
        using (var other = new JournalTransferReceiver(budget, clock))
            Invalid(() => other.Accept(transfer.Frame(0)), "cross-peer budget exhaustion");
        Check(receiver.Accept(transfer.Frame(1)) == null, "bounded middle chunk");
        Check(receiver.Accept(transfer.Frame(2))!.SequenceEqual(original) && budget.Used == 0, "immutable complete transfer and release");
        Invalid(() => receiver.Accept(transfer.Frame(1)), "missing first chunk");
        receiver.Accept(transfer.Frame(0));
        Invalid(() => receiver.Accept(transfer.Frame(0)), "duplicate chunk");
        Check(budget.Used == 0, "duplicate discards buffer");
        receiver.Accept(transfer.Frame(0));
        var foreign = new JournalTransfer(Guid.NewGuid(), original);
        Invalid(() => receiver.Accept(foreign.Frame(1)), "interleaved transfer identity");
        receiver.Accept(transfer.Frame(0)); clock.Elapsed = TimeSpan.FromSeconds(29);
        receiver.Accept(transfer.Frame(1)); clock.Elapsed = TimeSpan.FromSeconds(30);
        Invalid(() => receiver.Accept(transfer.Frame(2)), "later chunks never extend transfer deadline");
        receiver.Accept(transfer.Frame(0)); clock.Elapsed = TimeSpan.FromSeconds(60);
        Check(receiver.Expire() && budget.Used == 0, "idle transfer expiration releases memory");
        var corrupted = transfer.Frame(0).CopyBody(); corrupted[corrupted.Length - 1] ^= 1;
        receiver.Accept(new JournalFrame(JournalMessage.Transfer, transfer.Id, corrupted));
        receiver.Accept(transfer.Frame(1)); Invalid(() => receiver.Accept(transfer.Frame(2)), "whole image hash corruption");
        Check(budget.Used == 0, "corruption releases buffer");
        foreach (var kind in new[] { JournalMessage.CommandChunk, JournalMessage.EntriesChunk })
        {
            using var typed = new JournalTransferReceiver(budget, clock, JournalWire.MaximumLogicalBody);
            var chunks = new JournalTransfer(Guid.NewGuid(), original, kind);
            Check(typed.Accept(chunks.Frame(0)) == null, "Typed transfer starts with bounded allocation");
            var second = chunks.Frame(1);
            var changedKind = kind == JournalMessage.CommandChunk ? JournalMessage.EntriesChunk : JournalMessage.CommandChunk;
            Invalid(() => typed.Accept(new JournalFrame(changedKind, second.Correlation, second.CopyBody())), "Chunk direction cannot change mid-transfer");
            Check(budget.Used == 0, "Wrong-direction chunk releases shared allocation");
        }
        using (var small = new JournalTransferReceiver(budget, clock, 1024))
            Invalid(() => small.Accept(transfer.Frame(0)), "Logical transfer cap checked before allocation");
        receiver.Accept(transfer.Frame(0)); receiver.Dispose(); receiver.Dispose();
        Check(budget.Used == 0, "teardown releases once");
    }
    static async Task Workers()
    {
        var bytes = JournalWire.EncodeFrame(new JournalFrame(JournalMessage.Login, Guid.NewGuid(), Array.Empty<byte>()));
        using var sends = new JournalSendQueue(maximumFrames: 2);
        Check(sends.TryEnqueue(bytes) && sends.TryEnqueue(bytes) && !sends.TryEnqueue(bytes), "bounded send count");
        bytes[0] ^= 1;
        Check(sends.Dequeue()![0] != bytes[0], "send owns immutable frame"); bytes[0] ^= 1;
        Check(sends.TryEnqueue(bytes), "send drain releases slot");
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var slow = new JournalConnectionWorker(async (_, token) => { Interlocked.Increment(ref calls); entered.TrySetResult(true); await gate.Task; token.ThrowIfCancellationRequested(); }, maximumFrames: 2);
        Check(slow.TryReceive(bytes), "worker admits"); await entered.Task;
        Check(slow.TryReceive(bytes) && !slow.TryReceive(bytes), "running plus queued count against limit");
        int fastCalls = 0;
        using (var fast = new JournalConnectionWorker((_, token) => { fastCalls++; return Task.CompletedTask; }))
        {
            Check(fast.TryReceive(bytes), "independent peer admission"); await fast.Completion;
            Check(fastCalls == 1 && calls == 1, "slow peer does not stall another worker");
        }
        slow.Dispose(); slow.Dispose(); gate.TrySetResult(true); await slow.Completion;
        Check(calls == 1 && !slow.TryReceive(bytes), "close skips queued work and rejects ingress");
        using var malformed = new JournalConnectionWorker((_, token) => Task.CompletedTask);
        var bad = (byte[])bytes.Clone(); bad[0] = 0;
        Check(malformed.TryReceive(bad), "decode admitted to worker"); await malformed.Completion;
        Check(malformed.Failure is InvalidDataException && !malformed.TryReceive(bytes), "malformed peer closes and observes failure");
        using var ordered = new JournalConnectionWorker((f, token) => { lock (seen) seen.Add(f.Correlation); return Task.CompletedTask; });
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray(); seen.Clear();
        foreach (var id in ids) Check(ordered.TryReceive(JournalWire.EncodeFrame(new JournalFrame(JournalMessage.Login, id, Array.Empty<byte>()))), "ordered worker admission");
        await ordered.Completion; Check(seen.SequenceEqual(ids), "worker preserves connection order");
    }
    static readonly List<Guid> seen = new List<Guid>();
    sealed class Clock : IReplicationClock
    {
        public TimeSpan Elapsed { get; set; }
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Elapsed;
        public Task Delay(TimeSpan d, CancellationToken token) => Task.Delay(d, token);
    }
    sealed class MemoryStorage : IJournalStorage
    {
        JournalMachineState? state;
        public Task<JournalMachineState?> ReadStateAsync(CancellationToken token) => Task.FromResult(state);
        public Task CommitAsync(StorageCommit commit, CancellationToken token) { state = commit.State; return Task.CompletedTask; }
        public void Dispose() { }
    }
    sealed class Inventory : ICharacterInventory
    {
        public bool TryReserve(Guid request, Guid stack, int quantity) => true;
        public InventoryResult Apply(CharacterEffect effect) => InventoryResult.Applied;
        public bool ReleaseRejectedReservation(Guid request) => true;
    }
}
