using System;
using System.Collections.Generic;
using System.Linq;

namespace ThorsKist.Sync
{
    public sealed class CommandResult
    {
        public ItemCommand Command { get; }
        public CommandStatus Status { get; }
        public JournalEntry Entry { get; }
        internal CommandResult(ItemCommand command, CommandStatus status, JournalEntry entry)
        { Command = command; Status = status; Entry = entry; }
    }

    // Immutable candidate commit image. M3 must commit a transition before
    // installing/publishing it. No database or transport work occurs here.
    public sealed class JournalMachineState
    {
        public JournalSnapshot Checkpoint { get; }
        public VaultState Current { get; }
        public long Head { get; }
        public IReadOnlyList<JournalEntry> Journal { get; }
        public IReadOnlyList<CommandResult> Results { get; }
        public IReadOnlyList<Guid> UsedStackIds { get; }
        internal JournalMachineState(JournalSnapshot checkpoint, VaultState current, long head,
            IEnumerable<JournalEntry> journal, IEnumerable<CommandResult> results, IEnumerable<Guid> usedStackIds)
        {
            Checkpoint = checkpoint; Current = current; Head = head;
            Journal = Array.AsReadOnly(journal.ToArray()); Results = Array.AsReadOnly(results.ToArray());
            UsedStackIds = Array.AsReadOnly(usedStackIds.Distinct().ToArray());
        }
        public static JournalMachineState Create(VaultState initial)
        {
            var snapshot = new JournalSnapshot(0, initial, JournalChecksum.Compute(initial));
            return new JournalMachineState(snapshot, initial, 0, Array.Empty<JournalEntry>(), Array.Empty<CommandResult>(), initial.Stacks.Select(s => s.Id));
        }
    }

    public sealed class CommandTransition
    {
        public JournalMachineState State { get; }
        public CommandStatus Status { get; }
        public CommandResult? Result { get; }
        public bool IsRetry { get; }
        internal CommandTransition(JournalMachineState state, CommandStatus status, CommandResult? result, bool retry = false)
        { State = state; Status = status; Result = result; IsRetry = retry; }
    }

    public static class JournalMachine
    {
        // Invoke in single-writer order, with authenticated/authorized commands.
        // M4 owns session validation and admission before this transition.
        public static CommandTransition Execute(JournalMachineState state, ItemCommand command)
        {
            if (!state.Current.Identity.Contains(command.Address)) return new CommandTransition(state, CommandStatus.WrongVault, null);
            // Request IDs are scoped to the authenticated player within this vault/epoch,
            // retained beyond checkpoint pruning, and bound to the original session/payload.
            var old = state.Results.FirstOrDefault(r => r.Command.Address.Player == command.Address.Player && r.Command.RequestId == command.RequestId);
            if (old != null)
            {
                bool same = JournalChecksum.SameAddress(old.Command.Address, command.Address) && old.Command.Kind == command.Kind
                    && old.Command.StackId == command.StackId && JournalChecksum.SameItem(old.Command.Item, command.Item);
                return new CommandTransition(state, same ? old.Status : CommandStatus.RequestConflict, same ? old : null, same);
            }
            var stacks = state.Current.Stacks.ToDictionary(s => s.Id);
            stacks.TryGetValue(command.StackId, out var existing);
            var status = CommandStatus.Accepted; StackRecord? changed = null; Guid? deleted = null;
            if (existing != null && !JournalChecksum.SameItem(existing.Item, command.Item, false)) status = CommandStatus.StackConflict;
            else if (command.Kind == ItemCommandKind.Withdraw)
            {
                if (existing == null || existing.Item.Quantity < command.Item.Quantity) status = CommandStatus.InsufficientStock;
                else if (existing.Item.Quantity == command.Item.Quantity) deleted = existing.Id;
                else changed = WithQuantity(existing, existing.Item.Quantity - command.Item.Quantity);
            }
            else if (existing != null)
            {
                if (command.Item.Quantity > int.MaxValue - existing.Item.Quantity) status = CommandStatus.CapacityExceeded;
                else changed = WithQuantity(existing, existing.Item.Quantity + command.Item.Quantity);
            }
            else if (state.UsedStackIds.Contains(command.StackId)) status = CommandStatus.StackConflict;
            else
            {
                var occupied = new HashSet<int>(stacks.Values.Where(s => s.Slot >= 0).Select(s => s.Slot));
                int slot = 0; while (slot < state.Current.Capacity && occupied.Contains(slot)) slot++;
                if (slot == state.Current.Capacity || stacks.Count >= state.Current.Capacity) status = CommandStatus.CapacityExceeded;
                else changed = new StackRecord(command.StackId, slot, command.Item);
            }
            if (changed != null) stacks[changed.Id] = changed;
            if (deleted.HasValue) stacks.Remove(deleted.Value);
            var current = new VaultState(state.Current.Identity, state.Current.Capacity, stacks.Values);
            var operation = Guid.NewGuid();
            var effect = status == CommandStatus.Accepted ? new CharacterEffect(Guid.NewGuid(), operation, command.RequestId,
                command.Address, command.Kind == ItemCommandKind.Deposit ? CharacterEffectKind.RemoveReserved : CharacterEffectKind.Add, command.Item) : null;
            var entry = new JournalEntry(current.Identity, checked(state.Head + 1), operation, command.RequestId,
                status != CommandStatus.Accepted ? JournalEntryKind.Rejected : command.Kind == ItemCommandKind.Deposit ? JournalEntryKind.Deposit : JournalEntryKind.Withdraw,
                command.Address, changed, deleted, effect, JournalChecksum.Compute(current));
            var result = new CommandResult(command, status, entry);
            var next = new JournalMachineState(state.Checkpoint, current, entry.Sequence, state.Journal.Concat(new[] { entry }),
                state.Results.Concat(new[] { result }), changed == null ? state.UsedStackIds : state.UsedStackIds.Concat(new[] { changed.Id }));
            return new CommandTransition(next, status, result);
        }
        private static StackRecord WithQuantity(StackRecord stack, int quantity) => new StackRecord(stack.Id, stack.Slot,
            new ItemRecord(stack.Item.Prefab, quantity, stack.Item.FormatVersion, stack.Item.CopyPayload()));

        // Scheduling and recipients belong to M4; this is only the no-op transition.
        public static JournalMachineState AppendNoOp(JournalMachineState state)
        {
            var entry = new JournalEntry(state.Current.Identity, checked(state.Head + 1), Guid.NewGuid(), Guid.Empty,
                JournalEntryKind.NoOp, null, null, null, null, JournalChecksum.Compute(state.Current));
            return new JournalMachineState(state.Checkpoint, state.Current, entry.Sequence, state.Journal.Concat(new[] { entry }), state.Results, state.UsedStackIds);
        }

        // Caller supplies the protected cumulative-processing cutoff. M4 computes
        // it from active/login/logout participants and settled item operations.
        public static JournalMachineState Checkpoint(JournalMachineState state, long cutoff)
        {
            if (cutoff < state.Checkpoint.Sequence || cutoff > state.Head) throw new ArgumentOutOfRangeException(nameof(cutoff));
            var checkpoint = JournalReplay.Replay(state.Checkpoint, state.Journal.Where(e => e.Sequence <= cutoff));
            return new JournalMachineState(checkpoint, state.Current, state.Head, state.Journal.Where(e => e.Sequence > cutoff), state.Results, state.UsedStackIds);
        }
    }
}
