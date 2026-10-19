using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ThorsKist.Sync;

static class StateMachineTests
{
    static int checks;
    static readonly Guid Epoch = Guid.Parse("12345678-1234-1234-1234-123456789abc");
    static readonly VaultIdentity Vault = new("world", "hird", Epoch);
    static SessionAddress Address(string player = "alice") => new("world", "hird", Epoch, player, Guid.NewGuid());
    static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    static ItemRecord Item(int quantity, byte metadata = 1) => new("unknown-prefab", quantity, 109, new[] { metadata });
    static ItemCommand Command(SessionAddress address, int stack, int quantity, ItemCommandKind kind, Guid? request = null, byte metadata = 1)
        => new(address, request ?? Guid.NewGuid(), Id(stack), kind, Item(quantity, metadata));
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception("M2: " + message); }
    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { checks++; return; }
        throw new Exception("M2: expected " + typeof(T).Name);
    }
    public static int Run()
    {
        Canonical(); Transitions(); Replicas(); Exhaustive(); Seeded();
        Console.WriteLine($"PASS {checks} M2 state machine assertions (1,024 exhaustive schedules; 20 seeds x 250 events)");
        return checks;
    }
    static void Equivalent(VaultState actual, Dictionary<Guid, (int quantity, byte metadata, int slot)> expected, string context)
    {
        Check(actual.Stacks.Count == expected.Count && actual.Stacks.All(s => expected.TryGetValue(s.Id, out var value)
            && s.Item.Quantity == value.quantity && s.Item.CopyPayload().SequenceEqual(new[] { value.metadata })
            && s.Item.Prefab == "unknown-prefab" && s.Item.FormatVersion == 109 && s.Slot == value.slot), context);
    }
    static void Invariant(JournalMachineState state)
    {
        var replay = JournalReplay.Replay(state.Checkpoint, state.Journal);
        Check(replay.Sequence == state.Head && replay.Checksum == JournalChecksum.Compute(state.Current), "checkpoint + suffix = current");
        Check(state.Journal.Count == state.Head - state.Checkpoint.Sequence, "contiguous retained suffix");
    }
    static void Canonical()
    {
        var a = new StackRecord(Id(1), 0, Item(3)); var b = new StackRecord(Id(2), -1, Item(4, 2));
        var state = new VaultState(Vault, 1, new[] { a, b });
        var hash = JournalChecksum.Compute(state);
        Check(hash.Length == 64, "SHA256 hex");
        Check(hash == JournalChecksum.Compute(new VaultState(Vault, 1, new[] { b, a })), "record order independent including overflow");
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo("tr-TR"); Check(hash == JournalChecksum.Compute(state), "culture independent"); }
        finally { CultureInfo.CurrentCulture = culture; }
        foreach (var variant in new[] {
            new VaultState(Vault, 1, new[] { a }),
            new VaultState(Vault, 1, new[] { a, new StackRecord(b.Id, -1, Item(5, 2)) }),
            new VaultState(Vault, 1, new[] { a, new StackRecord(b.Id, -1, Item(4, 3)) }),
            new VaultState(Vault, 1, new[] { a, new StackRecord(b.Id, -1, new ItemRecord("known", 4, 109, new byte[] { 2 })) }),
            new VaultState(Vault, 1, new[] { a, new StackRecord(b.Id, -1, new ItemRecord("unknown-prefab", 4, 110, new byte[] { 2 })) }),
            new VaultState(Vault, 1, new[] { new StackRecord(Id(3), 0, Item(3)), b }),
            new VaultState(Vault, 1, new[] { new StackRecord(a.Id, -1, Item(3)), b }),
            new VaultState(Vault, 2, new[] { a, b }),
            new VaultState(new VaultIdentity("other", "hird", Epoch), 1, new[] { a, b }),
            new VaultState(new VaultIdentity("world", "other", Epoch), 1, new[] { a, b }),
            new VaultState(new VaultIdentity("world", "hird", Id(42)), 1, new[] { a, b }) })
            Check(hash != JournalChecksum.Compute(variant), "every stock/address field changes hash");
        // Fixed encoding vector assembled independently from the schema description.
        var empty = new VaultState(new VaultIdentity("w", "v", Guid.Parse("00000000-0000-0000-0000-000000000001")), 2, Array.Empty<StackRecord>());
        Check(Convert.ToHexString(JournalChecksum.Encode(empty)).ToLowerInvariant() ==
            "01000000010000007701000000762000000030303030303030303030303030303030303030303030303030303030303030310200000000000000", "fixed canonical bytes");
        Throws<ArgumentException>(() => new VaultState(Vault, 1, new[] { a, a }));
        Throws<ArgumentException>(() => new VaultState(Vault, 1, new[] { a, new StackRecord(b.Id, 0, Item(1)) }));
        Throws<ArgumentException>(() => new VaultState(Vault, 1, new[] { new StackRecord(a.Id, 1, Item(1)) }));
        Throws<NotSupportedException>(() => ((IList<StackRecord>)state.Stacks).Clear());
        var snapshot = new JournalSnapshot(0, state, "corrupt");
        Throws<InvalidDataException>(() => JournalReplay.Replay(snapshot, Array.Empty<JournalEntry>()));
    }
    static void Transitions()
    {
        var alice = Address(); var bob = Address("bob");
        var state = JournalMachineState.Create(new VaultState(Vault, 1, Array.Empty<StackRecord>()));
        var original = state;
        var deposit = Command(alice, 1, 5, ItemCommandKind.Deposit);
        var accepted = JournalMachine.Execute(state, deposit); state = accepted.State;
        var commit = new StorageCommit(original.Head, state);
        Check(commit.ExpectedHead == 0 && ReferenceEquals(commit.State, state), "typed atomic candidate includes checkpoint/current/log/results");
        Throws<ArgumentException>(() => new StorageCommit(state.Head + 1, state));
        Check(original.Head == 0 && original.Current.Stacks.Count == 0, "staged transition leaves previous state intact");
        Check(state.Head == 1 && state.Current.Stacks.Single().Item.Quantity == 5, "deposit credits exact quantity");
        var effect = accepted.Result!.Entry.Effect!;
        Check(effect.Kind == CharacterEffectKind.RemoveReserved && effect.RequestId == deposit.RequestId && effect.Item.Quantity == 5
            && effect.OperationId == accepted.Result.Entry.OperationId && effect.EffectId != Guid.Empty, "deposit removal identity");
        var retry = JournalMachine.Execute(state, deposit);
        Check(ReferenceEquals(retry.State, state) && ReferenceEquals(retry.Result, accepted.Result) && retry.IsRetry, "retry returns same operation/effect");
        Check(JournalMachine.Execute(state, Command(alice, 1, 4, ItemCommandKind.Deposit, deposit.RequestId)).Status == CommandStatus.RequestConflict, "changed quantity retry rejected");
        Check(JournalMachine.Execute(state, Command(alice, 2, 5, ItemCommandKind.Deposit, deposit.RequestId)).Status == CommandStatus.RequestConflict, "changed stack retry rejected");
        Check(JournalMachine.Execute(state, Command(alice, 1, 5, ItemCommandKind.Withdraw, deposit.RequestId)).Status == CommandStatus.RequestConflict, "changed kind retry rejected");
        Check(JournalMachine.Execute(state, Command(alice, 1, 5, ItemCommandKind.Deposit, deposit.RequestId, 2)).Status == CommandStatus.RequestConflict, "changed metadata retry rejected");
        var replacement = Address();
        Check(JournalMachine.Execute(state, Command(replacement, 1, 5, ItemCommandKind.Deposit, deposit.RequestId)).Status == CommandStatus.RequestConflict, "retry bound to original session");
        var full = Command(alice, 2, 1, ItemCommandKind.Deposit);
        var rejected = JournalMachine.Execute(state, full); state = rejected.State;
        Check(rejected.Status == CommandStatus.CapacityExceeded && state.Head == 2 && rejected.Result!.Entry.Effect == null, "definitive rejection is a checksummed no-change entry");
        var take = Command(bob, 1, 5, ItemCommandKind.Withdraw);
        var taken = JournalMachine.Execute(state, take); state = taken.State;
        Check(state.Current.Stacks.Count == 0 && taken.Result!.Entry.DeletedStack == Id(1), "whole stack delete");
        var last = JournalMachine.Execute(state, Command(alice, 1, 1, ItemCommandKind.Withdraw)); state = last.State;
        Check(last.Status == CommandStatus.InsufficientStock, "ordered last-stack spending once");
        var reused = JournalMachine.Execute(state, Command(alice, 1, 1, ItemCommandKind.Deposit)); state = reused.State;
        Check(reused.Status == CommandStatus.StackConflict, "deleted stack IDs cannot be reused");
        var before = state; state = JournalMachine.Checkpoint(state, state.Head);
        Check(state.Journal.Count == 0 && ReferenceEquals(before.Current, state.Current), "checkpoint truncates without changing current");
        Check(JournalMachine.Execute(state, deposit).Result == accepted.Result, "dedup survives pruning");
        Check(JournalMachine.Execute(state, full).Result == rejected.Result, "rejection survives pruning even with free capacity");
        Throws<ArgumentOutOfRangeException>(() => JournalMachine.Checkpoint(state, state.Head + 1));
        Throws<ArgumentOutOfRangeException>(() => JournalMachine.Checkpoint(state, 0));
        var emptyCheckpoint = state.Checkpoint;
        state = JournalMachine.AppendNoOp(state);
        Check(state.Head == before.Head + 1 && state.Journal.Single().Checksum == emptyCheckpoint.Checksum, "no-op advances with unchanged checksum");
        Check(emptyCheckpoint.Sequence == before.Head, "captured snapshot immutable after later entries");
        var wrong = new SessionAddress("world", "hird", Guid.NewGuid(), "alice", Guid.NewGuid());
        var mismatch = JournalMachine.Execute(state, Command(wrong, 2, 1, ItemCommandKind.Deposit));
        Check(mismatch.Status == CommandStatus.WrongVault && mismatch.State == state && mismatch.Result == null, "wrong epoch cannot mutate or consume request");
        var overflow = JournalMachineState.Create(new VaultState(Vault, 0, new[] { new StackRecord(Id(8), -1, Item(4)) }));
        overflow = JournalMachine.Execute(overflow, Command(alice, 8, 2, ItemCommandKind.Withdraw)).State;
        Check(overflow.Current.Stacks.Single().Slot == -1 && overflow.Current.Stacks.Single().Item.Quantity == 2, "overflow usable without disappearing");
        Invariant(overflow); Invariant(state);
    }
    sealed class Inventory : ICharacterInventory
    {
        public int Quantity = 100, Calls, Releases; public InventoryResult Result = InventoryResult.Applied;
        public readonly List<CharacterEffect> Effects = new();
        public bool TryReserve(Guid request, Guid stack, int quantity) => true;
        public bool ReleaseRejectedReservation(Guid request) { Releases++; return true; }
        public InventoryResult Apply(CharacterEffect effect)
        {
            Calls++;
            if (Result == InventoryResult.Applied) { Quantity += effect.Kind == CharacterEffectKind.Add ? effect.Item.Quantity : -effect.Item.Quantity; Effects.Add(effect); }
            return Result;
        }
    }
    static JournalEntry Corrupt(JournalEntry entry, string? checksum = null, CharacterEffect? effect = null) => new(entry.Identity, entry.Sequence,
        entry.OperationId, entry.RequestId, entry.Kind, entry.Actor, entry.ChangedStack, entry.DeletedStack, effect ?? entry.Effect, checksum ?? entry.Checksum);
    static void Replicas()
    {
        var alice = Address(); var bob = Address("bob");
        var server = JournalMachineState.Create(new VaultState(Vault, 2, Array.Empty<StackRecord>())); var initial = server.Checkpoint;
        var inv = new Inventory(); var replica = new JournalReplica(inv); var observerInv = new Inventory(); var observer = new JournalReplica(observerInv);
        Check(replica.Install(alice, initial, Array.Empty<Guid>()) && observer.Install(bob, initial, Array.Empty<Guid>()), "verified snapshot install");
        var command = Command(alice, 1, 3, ItemCommandKind.Deposit); server = JournalMachine.Execute(server, command).State;
        var deposit = server.Journal.Single();
        var stagedInventory = new Inventory(); var stagedReplica = new JournalReplica(stagedInventory);
        stagedReplica.Install(alice, initial, Array.Empty<Guid>());
        var prepared = JournalReplay.Prepare(stagedReplica.Current!, stagedReplica.Sequence, deposit);
        Check(stagedInventory.Calls == 0 && stagedReplica.Sequence == 0, "worker verification performs no character calls or replica installation");
        Check(stagedReplica.ProcessVerified(alice, prepared) == ReplicaResult.Applied && stagedInventory.Quantity == 97, "game thread applies prepared effect without rehashing");
        Check(stagedReplica.ProcessVerified(alice, prepared) == ReplicaResult.Duplicate && stagedInventory.Calls == 1, "prepared duplicate adds no effect");
        var staleInventory = new Inventory(); var staleReplica = new JournalReplica(staleInventory);
        staleReplica.Install(alice, initial, Array.Empty<Guid>());
        var stale = JournalReplay.Prepare(staleReplica.Current!, staleReplica.Sequence, deposit);
        var newSession = Address();
        var copiedSnapshot = new JournalSnapshot(0, new VaultState(Vault, 2, Array.Empty<StackRecord>()), initial.Checksum);
        staleReplica.Install(newSession, copiedSnapshot, Array.Empty<Guid>());
        Check(staleReplica.ProcessVerified(newSession, stale) == ReplicaResult.Blocked && staleInventory.Calls == 0, "candidate prepared before rejoin cannot install against a different baseline");
        Check(staleReplica.VerificationFailed(alice) == ReplicaResult.Blocked && !staleReplica.NeedsSuspension, "old worker failure cannot suspend replacement session");
        Check(staleReplica.VerificationFailed(newSession) == ReplicaResult.NeedsSuspension, "worker failure requests suspension on game thread");
        Check(inv.Quantity == 100, "command response cannot remove deposit");
        inv.Result = InventoryResult.InventoryChanged;
        Check(replica.Process(alice, deposit) == ReplicaResult.Blocked && replica.Sequence == 0 && inv.Quantity == 100, "failed removal cannot advance acknowledgment");
        Check(replica.Current!.Stacks.Count == 0, "failed effect leaves replica at fully processed sequence");
        inv.Result = InventoryResult.Applied;
        Check(replica.Process(alice, deposit) == ReplicaResult.Applied && replica.Sequence == 1 && inv.Quantity == 97, "verified deposit removes exact quantity");
        Check(replica.Process(alice, deposit) == ReplicaResult.Duplicate && inv.Calls == 2 && inv.Quantity == 97, "duplicate reack without repeated removal");
        Check(observer.Process(bob, deposit) == ReplicaResult.Applied && observerInv.Calls == 0, "observer updates stock without removing items");
        var successInv = new Inventory(); var success = new JournalReplica(successInv);
        success.Install(alice, initial, Array.Empty<Guid>()); success.Process(alice, deposit);
        server = JournalMachine.Execute(server, Command(alice, 1, 2, ItemCommandKind.Withdraw)).State;
        var take = server.Journal.Last();
        Check(success.Process(alice, take) == ReplicaResult.Applied && successInv.Quantity == 99, "normal single-stack transfers preserve exact quantities");
        Check(success.Process(alice, take) == ReplicaResult.Duplicate && successInv.Quantity == 99, "known withdrawal adds once");
        var malformed = new CharacterEffect(Guid.NewGuid(), take.OperationId, take.RequestId, alice, CharacterEffectKind.Add, Item(1));
        var malformedReplica = new JournalReplica(new Inventory()); malformedReplica.Install(alice, initial, Array.Empty<Guid>()); malformedReplica.Process(alice, deposit);
        Check(malformedReplica.Process(alice, Corrupt(take, effect: malformed)) == ReplicaResult.NeedsSuspension, "checksum alone cannot authorize an effect with the wrong exact quantity");
        inv.Result = InventoryResult.NoCapacity;
        Check(replica.Process(alice, take) == ReplicaResult.Blocked && replica.Sequence == 1, "full character blocks consecutive progress");
        server = JournalMachine.AppendNoOp(server);
        Check(replica.Process(alice, server.Journal.Last()) == ReplicaResult.NeedsSuspension && replica.Sequence == 1, "cannot skip blocked effect with later entry");
        // A new login replays all stock deltas, suppressing old-session effects.
        var replacement = Address(); inv.Result = InventoryResult.Applied;
        Check(replica.Install(replacement, initial, new[] { take.Effect!.EffectId, deposit.Effect!.EffectId }), "ordinary login after settlement");
        Check(replica.Process(alice, deposit) == ReplicaResult.Blocked && !replica.NeedsSuspension, "old transport session ignored");
        foreach (var entry in server.Journal) Check(replica.Process(replacement, entry) == ReplicaResult.Applied, "rejoin replay");
        Check(inv.Quantity == 97 && replica.Sequence == server.Head && JournalChecksum.Compute(replica.Current!) == JournalChecksum.Compute(server.Current), "settled replay applies stock without old effects");
        // In-process known identity guard: same identity can never authorize different effect content.
        var knownInventory = new Inventory(); var known = new JournalReplica(knownInventory);
        known.Install(alice, initial, Array.Empty<Guid>()); known.Process(alice, deposit);
        var collision = new CharacterEffect(deposit.Effect.EffectId, take.OperationId, take.RequestId, alice, CharacterEffectKind.Add, take.Effect.Item);
        Check(known.Process(alice, Corrupt(take, effect: collision)) == ReplicaResult.NeedsSuspension && knownInventory.Quantity == 97, "known effect collision fails closed");
        foreach (var entry in new[] { deposit, server.Journal.Last() })
        {
            var badInv = new Inventory(); var bad = new JournalReplica(badInv); bad.Install(alice, initial, Array.Empty<Guid>());
            if (entry.Sequence != 1) foreach (var earlier in server.Journal.Where(e => e.Sequence < entry.Sequence)) bad.Process(alice, earlier);
            long seq = bad.Sequence; int calls = badInv.Calls; var current = bad.Current;
            Check(bad.Process(alice, Corrupt(entry, "bad")) == ReplicaResult.NeedsSuspension
                && bad.Sequence == seq && badInv.Calls == calls && ReferenceEquals(current, bad.Current), "mismatch discards candidate and awards nothing, including no-op");
            Check(bad.Process(alice, entry) == ReplicaResult.NeedsSuspension, "failed session cannot repair in place");
            Throws<InvalidOperationException>(() => bad.Install(alice, initial, Array.Empty<Guid>()));
        }
        var failedSnapshot = new JournalReplica(new Inventory());
        Check(!failedSnapshot.Install(alice, new JournalSnapshot(0, initial.State, "bad"), Array.Empty<Guid>()) && failedSnapshot.NeedsSuspension, "snapshot mismatch requires suspension");
        Throws<InvalidOperationException>(() => failedSnapshot.Install(alice, initial, Array.Empty<Guid>()));
        var checkpointed = JournalMachine.Checkpoint(server, server.Head);
        Check(replica.Install(Address(), checkpointed.Checkpoint, new[] { deposit.Effect.EffectId }), "checkpointed settled deposit needs no reconciliation gate");
        Check(inv.Quantity == 97, "install never changes character inventory");
        var rejectedState = JournalMachine.Execute(JournalMachineState.Create(initial.State), Command(alice, 1, 1, ItemCommandKind.Withdraw)).State;
        var rejectionInventory = new Inventory(); var rejectionReplica = new JournalReplica(rejectionInventory);
        rejectionReplica.Install(alice, initial, Array.Empty<Guid>());
        Check(rejectionReplica.Process(alice, rejectedState.Journal.Single()) == ReplicaResult.Applied && rejectionInventory.Releases == 1
            && rejectionInventory.Quantity == 100, "explicit rejection releases reservation without removal/refund");
        Check(rejectionReplica.Process(alice, rejectedState.Journal.Single()) == ReplicaResult.Duplicate && rejectionInventory.Releases == 1, "duplicate rejection does not touch reservation again");
    }

    // This reference model uses only a quantity/metadata/slot ledger, a used-ID
    // set, and request fingerprints. It never calls production replay, hashing
    // or mutation helpers to select its expected outcomes.
    sealed class Model
    {
        public Dictionary<Guid, (int quantity, byte metadata, int slot)> Stock = new();
        public readonly HashSet<Guid> Used = new();
        public readonly Dictionary<Guid, (ItemCommand command, CommandStatus status)> Results = new();
        public readonly List<Dictionary<Guid, (int quantity, byte metadata, int slot)>> History = new() { new() };
        public long Head => History.Count - 1;
        public long Cutoff;
        public CommandStatus Execute(ItemCommand c)
        {
            if (Results.TryGetValue(c.RequestId, out var prior))
                return prior.command.StackId == c.StackId && prior.command.Kind == c.Kind && prior.command.Item.Quantity == c.Item.Quantity
                    && prior.command.Item.CopyPayload()[0] == c.Item.CopyPayload()[0] ? prior.status : CommandStatus.RequestConflict;
            var status = CommandStatus.Accepted; bool present = Stock.TryGetValue(c.StackId, out var old); byte metadata = c.Item.CopyPayload()[0];
            if (present && old.metadata != metadata) status = CommandStatus.StackConflict;
            else if (c.Kind == ItemCommandKind.Withdraw)
            {
                if (!present || old.quantity < c.Item.Quantity) status = CommandStatus.InsufficientStock;
                else if (old.quantity == c.Item.Quantity) Stock.Remove(c.StackId);
                else Stock[c.StackId] = (old.quantity - c.Item.Quantity, metadata, old.slot);
            }
            else if (present) Stock[c.StackId] = (old.quantity + c.Item.Quantity, metadata, old.slot);
            else if (Used.Contains(c.StackId)) status = CommandStatus.StackConflict;
            else if (Stock.Count == 3) status = CommandStatus.CapacityExceeded;
            else
            {
                int slot = Enumerable.Range(0, 3).First(i => Stock.Values.All(v => v.slot != i));
                Stock[c.StackId] = (c.Item.Quantity, metadata, slot); Used.Add(c.StackId);
            }
            Results.Add(c.RequestId, (c, status)); Record(); return status;
        }
        public void Record() => History.Add(new(Stock));
    }
    static void Compare(JournalMachineState state, Model model, string context)
    {
        Check(state.Head == model.Head && state.Checkpoint.Sequence == model.Cutoff && state.Results.Count == model.Results.Count, context + " positions/results");
        Equivalent(state.Current, model.Stock, context + " current");
        Equivalent(state.Checkpoint.State, model.History[(int)model.Cutoff], context + " checkpoint");
        Check(new HashSet<Guid>(state.UsedStackIds).SetEquals(model.Used), context + " used IDs");
        Invariant(state);
    }
    static void Exhaustive()
    {
        // Enumerate all length-five schedules over deposit/take/retry/checkpoint.
        for (int schedule = 0; schedule < 1024; schedule++)
        {
            var alice = Address(); var model = new Model(); var state = JournalMachineState.Create(new VaultState(Vault, 3, Array.Empty<StackRecord>()));
            var first = Command(alice, 1, 1, ItemCommandKind.Deposit); int events = schedule;
            for (int step = 0; step < 5; step++, events /= 4)
            {
                int kind = events % 4;
                if (kind == 3) { model.Cutoff = model.Head; state = JournalMachine.Checkpoint(state, model.Cutoff); }
                else
                {
                    var command = kind == 2 ? first : Command(alice, 1, 1, kind == 0 ? ItemCommandKind.Deposit : ItemCommandKind.Withdraw);
                    var expected = model.Execute(command); var transition = JournalMachine.Execute(state, command); state = transition.State;
                    Check(transition.Status == expected, $"schedule={schedule} step={step} result");
                }
                Compare(state, model, $"schedule={schedule} step={step}");
            }
        }
    }
    static void Seeded()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed); var alice = Address(); var model = new Model();
            var state = JournalMachineState.Create(new VaultState(Vault, 3, Array.Empty<StackRecord>()));
            var requests = new List<ItemCommand>(); var trace = new List<string>();
            var inventory = new Inventory(); var recipient = new JournalReplica(inventory);
            recipient.Install(alice, state.Checkpoint, Array.Empty<Guid>());
            int expectedCharacter = 100, expectedEffects = 0;
            var expectedEffectIds = new HashSet<Guid>();
            try
            {
                for (int step = 0; step < 250; step++)
                {
                    int action = random.Next(10);
                    if (action == 0)
                    {
                        model.Cutoff += random.Next((int)(model.Head - model.Cutoff + 1));
                        trace.Add("checkpoint " + model.Cutoff); state = JournalMachine.Checkpoint(state, model.Cutoff);
                    }
                    else if (action == 1) { trace.Add("noop"); model.Record(); state = JournalMachine.AppendNoOp(state); }
                    else
                    {
                        ItemCommand command;
                        if (action <= 3 && requests.Count > 0)
                        {
                            command = requests[random.Next(requests.Count)];
                            if (action == 3) command = new ItemCommand(command.Address, command.RequestId, command.StackId, command.Kind, Item(command.Item.Quantity + 1));
                        }
                        else { command = Command(alice, random.Next(1, 9), random.Next(1, 6), (ItemCommandKind)random.Next(2), metadata: (byte)random.Next(1, 3)); requests.Add(command); }
                        trace.Add($"{command.Kind} {command.StackId} qty={command.Item.Quantity} metadata={command.Item.CopyPayload()[0]} request={command.RequestId}");
                        var expected = model.Execute(command); var prior = state;
                        var result = JournalMachine.Execute(state, command); state = result.State;
                        Check(result.Status == expected, "model-selected result");
                        if (result.IsRetry) Check(ReferenceEquals(prior, state), "retry changes nothing");
                        if (state.Head > prior.Head && expected == CommandStatus.Accepted)
                        {
                            expectedCharacter += command.Kind == ItemCommandKind.Deposit ? -command.Item.Quantity : command.Item.Quantity;
                            expectedEffects++;
                            Check(expectedEffectIds.Add(result.Result!.Entry.Effect!.EffectId), "separate stack transactions have distinct effect IDs");
                        }
                    }
                    foreach (var entry in state.Journal.Where(e => e.Sequence > recipient.Sequence))
                    {
                        Check(recipient.Process(alice, entry) == ReplicaResult.Applied, "model recipient processes consecutive effect");
                        Check(recipient.Process(alice, entry) == ReplicaResult.Duplicate, "model repeated delivery suppressed");
                    }
                    Check(inventory.Quantity == expectedCharacter && inventory.Effects.Count == expectedEffects, "independent character accounting, once per accepted stack transaction");
                    Compare(state, model, $"seed={seed} step={step}");
                    var observer = new JournalReplica(new Inventory()); var address = Address("observer");
                    Check(observer.Install(address, state.Checkpoint, Array.Empty<Guid>()), "model observer snapshot");
                    foreach (var entry in state.Journal) Check(observer.Process(address, entry) == ReplicaResult.Applied, "model observer replay");
                    Equivalent(observer.Current!, model.Stock, "model observer stock");
                }
            }
            catch (Exception ex) { throw new Exception($"Seed {seed}\n" + string.Join("\n", trace), ex); }
        }
    }
}
