using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ThorsKist.Sync
{
    public sealed class VaultIdentity
    {
        public string World { get; }
        public string Vault { get; }
        public Guid Epoch { get; }
        public VaultIdentity(string world, string vault, Guid epoch)
        {
            if (string.IsNullOrWhiteSpace(world) || string.IsNullOrWhiteSpace(vault) || epoch == Guid.Empty)
                throw new ArgumentException("World, vault and epoch required");
            World = world; Vault = vault; Epoch = epoch;
        }
        public bool Matches(VaultIdentity other) => World == other.World && Vault == other.Vault && Epoch == other.Epoch;
        public bool Contains(SessionAddress address) => World == address.World && Vault == address.Vault && Epoch == address.Epoch;
    }

    public sealed class StackRecord
    {
        public Guid Id { get; }
        // -1 is overflow. Ordinary deposits never allocate overflow slots.
        public int Slot { get; }
        public ItemRecord Item { get; }
        public StackRecord(Guid id, int slot, ItemRecord item)
        {
            if (id == Guid.Empty || slot < -1) throw new ArgumentException("Invalid stack identity/slot");
            Id = id; Slot = slot; Item = item ?? throw new ArgumentNullException(nameof(item));
        }
    }

    public sealed class VaultState
    {
        public VaultIdentity Identity { get; }
        public int Capacity { get; }
        public IReadOnlyList<StackRecord> Stacks { get; }
        public VaultState(VaultIdentity identity, int capacity, IEnumerable<StackRecord> stacks)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            var records = stacks.ToArray();
            if (records.Any(s => s == null || s.Slot >= capacity)
                || records.Select(s => s.Id).Distinct().Count() != records.Length
                || records.Where(s => s.Slot >= 0).GroupBy(s => s.Slot).Any(g => g.Count() > 1))
                throw new ArgumentException("Invalid or duplicate stack/slot");
            Stacks = Array.AsReadOnly(records);
        }
    }

    // Schema 1 deliberately hashes the complete opaque item payload from M1.
    // The game adapter's field-level canonical item schema remains an M5 gate.
    public static class JournalChecksum
    {
        public const int Schema = 1;
        public static byte[] Encode(VaultState state)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write(Schema);
                WriteString(writer, state.Identity.World); WriteString(writer, state.Identity.Vault);
                WriteString(writer, state.Identity.Epoch.ToString("N"));
                writer.Write(state.Capacity); writer.Write(state.Stacks.Count);
                foreach (var stack in state.Stacks.OrderBy(s => s.Id.ToString("N"), StringComparer.Ordinal))
                {
                    WriteString(writer, stack.Id.ToString("N")); writer.Write(stack.Slot);
                    WriteString(writer, stack.Item.Prefab); writer.Write(stack.Item.Quantity);
                    writer.Write(stack.Item.FormatVersion);
                    var payload = stack.Item.CopyPayload(); writer.Write(payload.Length); writer.Write(payload);
                }
                writer.Flush(); return stream.ToArray();
            }
        }
        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes);
        }
        public static string Compute(VaultState state)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encode(state))).Replace("-", "").ToLowerInvariant();
        }
        public static bool SameItem(ItemRecord a, ItemRecord b, bool includeQuantity = true) => a.Prefab == b.Prefab
            && (!includeQuantity || a.Quantity == b.Quantity) && a.FormatVersion == b.FormatVersion
            && a.CopyPayload().SequenceEqual(b.CopyPayload());
        internal static bool SameAddress(SessionAddress a, SessionAddress b) => a.World == b.World && a.Vault == b.Vault
            && a.Epoch == b.Epoch && a.Player == b.Player && a.Session == b.Session;
        internal static bool SameEffect(CharacterEffect a, CharacterEffect b) => a.EffectId == b.EffectId
            && a.OperationId == b.OperationId && a.RequestId == b.RequestId && SameAddress(a.Recipient, b.Recipient)
            && a.Kind == b.Kind && SameItem(a.Item, b.Item);
    }

    public sealed class JournalSnapshot
    {
        public int Schema { get; }
        public long Sequence { get; }
        public VaultState State { get; }
        public string Checksum { get; }
        public JournalSnapshot(long sequence, VaultState state, string checksum, int schema = JournalChecksum.Schema)
        {
            if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            Sequence = sequence; State = state ?? throw new ArgumentNullException(nameof(state));
            Checksum = checksum ?? throw new ArgumentNullException(nameof(checksum)); Schema = schema;
        }
    }
    public enum JournalEntryKind { Deposit, Withdraw, Rejected, NoOp, Login, Logout, Compensation }
    public enum CommandStatus { Accepted, InsufficientStock, CapacityExceeded, StackConflict, RequestConflict, WrongVault }

    public sealed class JournalEntry
    {
        public VaultIdentity Identity { get; }
        public int Schema { get; }
        public long Sequence { get; }
        public Guid OperationId { get; }
        public Guid RequestId { get; }
        public JournalEntryKind Kind { get; }
        public SessionAddress? Actor { get; }
        public Guid? DeletedStack { get; }
        public StackRecord? ChangedStack { get; }
        public CharacterEffect? Effect { get; }
        public string Checksum { get; }
        public JournalEntry(VaultIdentity identity, long sequence, Guid operationId, Guid requestId,
            JournalEntryKind kind, SessionAddress? actor, StackRecord? changedStack, Guid? deletedStack,
            CharacterEffect? effect, string checksum, int schema = JournalChecksum.Schema)
        {
            if (sequence <= 0 || operationId == Guid.Empty || !Enum.IsDefined(typeof(JournalEntryKind), kind))
                throw new ArgumentException("Invalid journal identity/kind");
            if (changedStack != null && deletedStack != null) throw new ArgumentException("Only one stack change per entry");
            Identity = identity ?? throw new ArgumentNullException(nameof(identity)); Sequence = sequence;
            OperationId = operationId; RequestId = requestId; Kind = kind; Actor = actor;
            ChangedStack = changedStack; DeletedStack = deletedStack; Effect = effect;
            Checksum = checksum ?? throw new ArgumentNullException(nameof(checksum)); Schema = schema;
        }
    }

    // Created only by successful replay verification. Adapters may produce it on
    // a worker and marshal it to the game thread without hashing there again.
    public sealed class VerifiedJournalUpdate
    {
        internal VaultState Before { get; }
        internal long BeforeSequence { get; }
        public VaultState State { get; }
        public JournalEntry Entry { get; }
        internal VerifiedJournalUpdate(VaultState before, long beforeSequence, VaultState state, JournalEntry entry)
        { Before = before; BeforeSequence = beforeSequence; State = state; Entry = entry; }
    }

    public static class JournalReplay
    {
        public static bool Verify(JournalSnapshot snapshot) => snapshot.Schema == JournalChecksum.Schema
            && snapshot.Checksum == JournalChecksum.Compute(snapshot.State);
        public static VerifiedJournalUpdate Prepare(VaultState state, long sequence, JournalEntry entry)
            => new VerifiedJournalUpdate(state, sequence, Apply(state, sequence, entry), entry);
        public static VaultState Apply(VaultState state, long previousSequence, JournalEntry entry)
        {
            if (!state.Identity.Matches(entry.Identity) || entry.Schema != JournalChecksum.Schema || previousSequence == long.MaxValue || entry.Sequence != previousSequence + 1)
                throw new InvalidDataException("Journal address, schema or sequence mismatch");
            ValidateDelta(state, entry);
            var records = state.Stacks.ToDictionary(s => s.Id);
            if (entry.DeletedStack.HasValue && !records.Remove(entry.DeletedStack.Value))
                throw new InvalidDataException("Deleting missing stack");
            if (entry.ChangedStack != null) records[entry.ChangedStack.Id] = entry.ChangedStack;
            VaultState next;
            try { next = new VaultState(state.Identity, state.Capacity, records.Values); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid stack delta", ex); }
            if (JournalChecksum.Compute(next) != entry.Checksum) throw new InvalidDataException("Journal checksum mismatch");
            return next;
        }
        private static void ValidateDelta(VaultState state, JournalEntry entry)
        {
            if (entry.Kind == JournalEntryKind.Compensation)
            {
                if (entry.Effect != null || entry.DeletedStack.HasValue || entry.ChangedStack == null
                    || entry.ChangedStack.Slot != -1 || state.Stacks.Any(s => s.Id == entry.ChangedStack.Id)
                    || entry.RequestId == Guid.Empty || entry.Actor == null || !state.Identity.Contains(entry.Actor))
                    throw new InvalidDataException("Invalid compensation delta");
                return;
            }
            if (entry.Kind == JournalEntryKind.NoOp || entry.Kind == JournalEntryKind.Rejected
                || entry.Kind == JournalEntryKind.Login || entry.Kind == JournalEntryKind.Logout)
            {
                if (entry.ChangedStack != null || entry.DeletedStack.HasValue || entry.Effect != null)
                    throw new InvalidDataException("Non-item entry contains an item change");
                if (entry.Kind != JournalEntryKind.NoOp && (entry.Actor == null || !state.Identity.Contains(entry.Actor) || entry.RequestId == Guid.Empty))
                    throw new InvalidDataException("Rejection missing actor/request");
                return;
            }
            var effect = entry.Effect;
            if (effect == null || entry.Actor == null || !state.Identity.Contains(entry.Actor)
                || !JournalChecksum.SameAddress(effect.Recipient, entry.Actor) || effect.OperationId != entry.OperationId
                || effect.RequestId != entry.RequestId || entry.RequestId == Guid.Empty
                || (entry.ChangedStack == null && !entry.DeletedStack.HasValue))
                throw new InvalidDataException("Item entry missing bound effect/delta");
            var id = entry.ChangedStack?.Id ?? entry.DeletedStack!.Value;
            var old = state.Stacks.FirstOrDefault(s => s.Id == id);
            var changed = entry.ChangedStack;
            if (old != null && (!JournalChecksum.SameItem(old.Item, effect.Item, false)
                || (changed != null && changed.Slot != old.Slot)))
                throw new InvalidDataException("Item metadata or slot changed");
            if (changed != null && !JournalChecksum.SameItem(changed.Item, effect.Item, false))
                throw new InvalidDataException("Effect metadata differs from delta");
            long before = old?.Item.Quantity ?? 0;
            long after = changed?.Item.Quantity ?? 0;
            if (entry.Kind == JournalEntryKind.Deposit)
            {
                if (effect.Kind != CharacterEffectKind.RemoveReserved || changed == null || (old == null && changed.Slot < 0)
                    || after - before != effect.Item.Quantity)
                    throw new InvalidDataException("Deposit delta differs from removal");
            }
            else if (effect.Kind != CharacterEffectKind.Add || old == null || before - after != effect.Item.Quantity)
                throw new InvalidDataException("Withdrawal delta differs from delivery");
        }
        public static JournalSnapshot Replay(JournalSnapshot checkpoint, IEnumerable<JournalEntry> entries)
        {
            if (!Verify(checkpoint)) throw new InvalidDataException("Snapshot checksum/schema mismatch");
            var state = checkpoint.State; long sequence = checkpoint.Sequence;
            foreach (var entry in entries) { state = Apply(state, sequence, entry); sequence = entry.Sequence; }
            return new JournalSnapshot(sequence, state, JournalChecksum.Compute(state));
        }
    }
}
