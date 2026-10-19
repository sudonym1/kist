using System;
using System.IO;
using System.Text;
using ThorsKist.Sync;

namespace ThorsKist.Sync.Sqlite
{
    // Versioned storage envelopes, independent of checksum and game item schemas.
    // Each SQL row is one envelope; reject truncated/trailing/oversized values.
    internal static class StorageEncoding
    {
        private const int Version = 1;
        private const int MaxBytes = 64 * 1024 * 1024;
        internal static byte[] Encode<T>(T value, Action<BinaryWriter, T> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            {
                writer.Write(Version); write(writer, value); writer.Flush();
                if (stream.Length > MaxBytes) throw new InvalidDataException("Storage row exceeds limit");
                return stream.ToArray();
            }
        }
        internal static T Decode<T>(byte[] bytes, Func<BinaryReader, T> read)
        {
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Storage row exceeds limit");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (reader.ReadInt32() != Version) throw new InvalidDataException("Unknown storage envelope version");
                var result = read(reader);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing storage bytes");
                return result;
            }
        }
        private static void Text(BinaryWriter w, string value) { var bytes = new UTF8Encoding(false, true).GetBytes(value); Bytes(w, bytes); }
        private static string Text(BinaryReader r) => new UTF8Encoding(false, true).GetString(Bytes(r));
        private static void Bytes(BinaryWriter w, byte[] value) { if (value.Length > MaxBytes) throw new InvalidDataException("Payload exceeds limit"); w.Write(value.Length); w.Write(value); }
        private static byte[] Bytes(BinaryReader r)
        {
            int size = r.ReadInt32();
            if (size < 0 || size > MaxBytes || size > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid payload length");
            return r.ReadBytes(size);
        }
        private static void Id(BinaryWriter w, Guid id) => Text(w, id.ToString("N"));
        private static Guid Id(BinaryReader r) => Guid.ParseExact(Text(r), "N");
        private static void Identity(BinaryWriter w, VaultIdentity v) { Text(w, v.World); Text(w, v.Vault); Id(w, v.Epoch); }
        private static VaultIdentity Identity(BinaryReader r) => new VaultIdentity(Text(r), Text(r), Id(r));
        private static void Address(BinaryWriter w, SessionAddress a) { Identity(w, new VaultIdentity(a.World, a.Vault, a.Epoch)); Text(w, a.Player); Id(w, a.Session); }
        private static SessionAddress Address(BinaryReader r) { var v = Identity(r); return new SessionAddress(v.World, v.Vault, v.Epoch, Text(r), Id(r)); }
        private static void Item(BinaryWriter w, ItemRecord i) { Text(w, i.Prefab); w.Write(i.Quantity); w.Write(i.FormatVersion); Bytes(w, i.CopyPayload()); }
        private static ItemRecord Item(BinaryReader r) => new ItemRecord(Text(r), r.ReadInt32(), r.ReadInt32(), Bytes(r));
        internal static void Stack(BinaryWriter w, StackRecord s) { Id(w, s.Id); w.Write(s.Slot); Item(w, s.Item); }
        internal static StackRecord Stack(BinaryReader r) => new StackRecord(Id(r), r.ReadInt32(), Item(r));
        internal static void Entry(BinaryWriter w, JournalEntry e)
        {
            Identity(w, e.Identity); w.Write(e.Schema); w.Write(e.Sequence); Id(w, e.OperationId); Id(w, e.RequestId); w.Write((int)e.Kind);
            w.Write(e.Actor != null); if (e.Actor != null) Address(w, e.Actor);
            w.Write(e.ChangedStack != null); if (e.ChangedStack != null) Stack(w, e.ChangedStack);
            w.Write(e.DeletedStack.HasValue); if (e.DeletedStack.HasValue) Id(w, e.DeletedStack.Value);
            w.Write(e.Effect != null);
            if (e.Effect != null)
            {
                var f = e.Effect; Id(w, f.EffectId); Id(w, f.OperationId); Id(w, f.RequestId);
                Address(w, f.Recipient); w.Write((int)f.Kind); Item(w, f.Item);
            }
            Text(w, e.Checksum);
        }
        internal static JournalEntry Entry(BinaryReader r)
        {
            var v = Identity(r); int schema = r.ReadInt32(); long seq = r.ReadInt64(); var op = Id(r); var request = Id(r); var kind = (JournalEntryKind)r.ReadInt32();
            var actor = r.ReadBoolean() ? Address(r) : null; var changed = r.ReadBoolean() ? Stack(r) : null;
            Guid? deleted = r.ReadBoolean() ? Id(r) : (Guid?)null;
            var effect = r.ReadBoolean() ? new CharacterEffect(Id(r), Id(r), Id(r), Address(r), (CharacterEffectKind)r.ReadInt32(), Item(r)) : null;
            return new JournalEntry(v, seq, op, request, kind, actor, changed, deleted, effect, Text(r), schema);
        }
        internal static void Result(BinaryWriter w, CommandResult result)
        {
            var c = result.Command; Address(w, c.Address); Id(w, c.RequestId); Id(w, c.StackId); w.Write((int)c.Kind); Item(w, c.Item);
            w.Write((int)result.Status); Entry(w, result.Entry);
        }
        internal static CommandResult Result(BinaryReader r)
        {
            var c = new ItemCommand(Address(r), Id(r), Id(r), (ItemCommandKind)r.ReadInt32(), Item(r));
            return CommandResult.Restore(c, (CommandStatus)r.ReadInt32(), Entry(r));
        }
        internal static void Replication(BinaryWriter w, ReplicationState state)
        {
            w.Write(state.Revision); w.Write(state.NextHeartbeat.UtcDateTime.Ticks); w.Write(state.Sessions.Count);
            foreach (var s in state.Sessions)
            {
                Address(w, s.Address); Id(w, s.LoginRequest);
                var b = s.Baseline; Identity(w, b.State.Identity); w.Write(b.State.Capacity); w.Write(b.Sequence); Text(w, b.Checksum); w.Write(b.Schema);
                w.Write(b.State.Stacks.Count); foreach (var stack in b.State.Stacks) Stack(w, stack);
                w.Write(s.Catchup.Count); foreach (var e in s.Catchup) Entry(w, e);
                w.Write(s.LoginMarker); w.Write((int)s.Phase); w.Write(s.Acknowledged); w.Write(s.QueuedThrough);
                Id(w, s.LogoutRequest); w.Write(s.LogoutMarker); w.Write(s.BarrierDue.UtcDateTime.Ticks);
                w.Write(s.SuspendedUntil.HasValue); if (s.SuspendedUntil.HasValue) w.Write(s.SuspendedUntil.Value.UtcDateTime.Ticks);
                w.Write(s.Deadlines.Count); foreach (var d in s.Deadlines) { w.Write(d.Sequence); w.Write(d.Due.UtcDateTime.Ticks); }
            }
            w.Write(state.Settlements.Count);
            foreach (var s in state.Settlements) { Id(w, s.Operation); w.Write((int)s.Kind); Id(w, s.Compensation); }
        }
        private static int Count(BinaryReader r)
        {
            int n = r.ReadInt32(); if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid replication count"); return n;
        }
        private static DateTimeOffset Time(BinaryReader r) => new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);
        internal static ReplicationState Replication(BinaryReader r)
        {
            long revision = r.ReadInt64(); var heartbeat = Time(r); int count = Count(r);
            var sessions = new System.Collections.Generic.List<ReplicationSession>();
            for (int i = 0; i < count; i++)
            {
                var address = Address(r); var request = Id(r); var identity = Identity(r); int capacity = r.ReadInt32();
                long sequence = r.ReadInt64(); string checksum = Text(r); int schema = r.ReadInt32();
                var stacks = new System.Collections.Generic.List<StackRecord>(); int n = Count(r); for (int j = 0; j < n; j++) stacks.Add(Stack(r));
                var baseline = new JournalSnapshot(sequence, new VaultState(identity, capacity, stacks), checksum, schema);
                var entries = new System.Collections.Generic.List<JournalEntry>(); n = Count(r); for (int j = 0; j < n; j++) entries.Add(Entry(r));
                long marker = r.ReadInt64(); var phase = (SessionPhase)r.ReadInt32(); long ack = r.ReadInt64(); long queued = r.ReadInt64();
                var logout = Id(r); long logoutMarker = r.ReadInt64(); var due = Time(r); DateTimeOffset? suspended = r.ReadBoolean() ? Time(r) : (DateTimeOffset?)null;
                var deadlines = new System.Collections.Generic.List<DeliveryDeadline>(); n = Count(r); for (int j = 0; j < n; j++) deadlines.Add(new DeliveryDeadline(r.ReadInt64(), Time(r)));
                sessions.Add(new ReplicationSession(address, request, baseline, entries, marker, phase, ack, queued, logout, logoutMarker, due, suspended, deadlines));
            }
            var settlements = new System.Collections.Generic.List<EffectSettlement>(); count = Count(r);
            for (int i = 0; i < count; i++) settlements.Add(new EffectSettlement(Id(r), (SettlementKind)r.ReadInt32(), Id(r)));
            return new ReplicationState(revision, heartbeat, sessions, settlements);
        }
    }
}
