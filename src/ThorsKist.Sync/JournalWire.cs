using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ThorsKist.Sync
{
    public enum JournalMessage : byte { Login = 1, Command, Acknowledge, Logout, Suspend, Reply, Entries, Transfer }

    // Independent of the legacy routed protocol and the SQLite envelope. Decode
    // only after the adapter has authenticated the actual incoming connection.
    public sealed class JournalFrame
    {
        private readonly byte[] body;
        public JournalMessage Message { get; }
        public Guid Correlation { get; }
        public JournalFrame(JournalMessage message, Guid correlation, byte[] body)
        {
            if (!Enum.IsDefined(typeof(JournalMessage), message) || correlation == Guid.Empty)
                throw new ArgumentException("Invalid frame identity");
            if (body == null || body.Length > JournalWire.MaximumFrameBody) throw new ArgumentException("Invalid frame size");
            Message = message; Correlation = correlation; this.body = (byte[])body.Clone();
        }
        public byte[] CopyBody() => (byte[])body.Clone();
    }

    public static class JournalWire
    {
        public const int Version = 1;
        public const int MaximumFrameBody = 60 * 1024;
        public const int FrameHeaderBytes = 29;
        public const int MaximumFrameBytes = FrameHeaderBytes + MaximumFrameBody;
        public const int MaximumTransferBytes = 64 * 1024 * 1024;
        // An entry may contain the item twice (delta and character effect).
        public const int MaximumItemBytes = 24 * 1024;
        public const int MaximumEntries = 32;
        private const uint Magic = 0x4a4b4854; // THKJ
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] EncodeFrame(JournalFrame frame) => Encode(w =>
        {
            w.Write(Magic); w.Write(Version); w.Write((byte)frame.Message); Id(w, frame.Correlation); Bytes(w, frame.CopyBody(), MaximumFrameBody);
        }, MaximumFrameBytes);
        public static JournalFrame DecodeFrame(byte[] bytes) => Decode(bytes, r =>
        {
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) throw new InvalidDataException("Journal protocol mismatch");
            var kind = (JournalMessage)r.ReadByte(); var correlation = Id(r);
            return new JournalFrame(kind, correlation, Bytes(r, MaximumFrameBody));
        }, MaximumFrameBytes);

        public static byte[] EncodeCommand(ItemCommand command) => Encode(w =>
        {
            Address(w, command.Address); Id(w, command.RequestId); Id(w, command.StackId); w.Write((byte)command.Kind); Item(w, command.Item);
        }, MaximumFrameBody);
        public static ItemCommand DecodeCommand(byte[] bytes) => Decode(bytes, r =>
            new ItemCommand(Address(r), Id(r), Id(r), (ItemCommandKind)r.ReadByte(), Item(r)), MaximumFrameBody);

        public static byte[] EncodePosition(SessionAddress address, long sequence) => Encode(w =>
        { Address(w, address); if (sequence < 0) throw new InvalidDataException("Negative position"); w.Write(sequence); }, MaximumFrameBody);
        public static (SessionAddress Address, long Sequence) DecodePosition(byte[] bytes) => Decode(bytes, r =>
        {
            var address = Address(r); long sequence = r.ReadInt64();
            if (sequence < 0) throw new InvalidDataException("Negative position"); return (address, sequence);
        }, MaximumFrameBody);
        public static byte[] EncodeLogin(VaultIdentity vault) => Encode(w => Identity(w, vault), MaximumFrameBody);
        public static VaultIdentity DecodeLogin(byte[] bytes) => Decode(bytes, Identity, MaximumFrameBody);

        // Login images are transferred separately in bounded frames. Never send
        // the persistent session row (which includes old deadlines/catchup) on
        // ordinary acknowledgments or command results.
        public static byte[] EncodeLoginImage(SessionReply reply) => Encode(w =>
        {
            var s = reply.Session ?? throw new InvalidDataException("Login session required");
            if (reply.Status != SessionStatus.Ok || s.Phase == SessionPhase.Closed) throw new InvalidDataException("Login is no longer valid");
            Address(w, s.Address); Id(w, s.LoginRequest); w.Write(s.LoginMarker); Snapshot(w, s.Baseline);
            Count(w, s.Catchup.Count, 1000000); foreach (var e in s.Catchup) Entry(w, e);
            Count(w, reply.SettledEffects.Count, 1000000); foreach (var id in reply.SettledEffects) Id(w, id);
        }, MaximumTransferBytes);
        public static SessionReply DecodeLoginImage(byte[] bytes) => Decode(bytes, r =>
        {
            var a = Address(r); var request = Id(r); long marker = r.ReadInt64(); var snapshot = Snapshot(r);
            var entries = new List<JournalEntry>(); int n = Count(r, 1000000, 64); for (int i = 0; i < n; i++) entries.Add(Entry(r));
            var settled = new List<Guid>(); n = Count(r, 1000000, 16); for (int i = 0; i < n; i++) settled.Add(Id(r));
            if (!snapshot.State.Identity.Contains(a) || !JournalReplay.Verify(snapshot)
                || JournalReplay.Replay(snapshot, entries).Sequence != marker || entries.Count == 0
                || entries[entries.Count - 1].Kind != JournalEntryKind.Login
                || entries[entries.Count - 1].Actor == null
                || !JournalChecksum.SameAddress(entries[entries.Count - 1].Actor!, a)
                || entries[entries.Count - 1].RequestId != request) throw new InvalidDataException("Invalid login image");
            var s = new ReplicationSession(a, request, snapshot, entries, marker, SessionPhase.LoggingIn,
                snapshot.Sequence, marker, Guid.Empty, 0, DateTimeOffset.MinValue, null, Array.Empty<DeliveryDeadline>());
            return new SessionReply(SessionStatus.Ok, s, entries: entries, settled: settled);
        }, MaximumTransferBytes);

        public static byte[] EncodeEntries(SessionAddress address, IReadOnlyList<JournalEntry> entries) => Encode(w =>
        { Address(w, address); Count(w, entries.Count, MaximumEntries); foreach (var e in entries) Entry(w, e); }, MaximumFrameBody);
        public static (SessionAddress Address, IReadOnlyList<JournalEntry> Entries) DecodeEntries(byte[] bytes) => Decode(bytes, r =>
        {
            var address = Address(r); int n = Count(r, MaximumEntries, 64); var entries = new List<JournalEntry>();
            for (int i = 0; i < n; i++)
            {
                var entry = Entry(r); if (!entry.Identity.Contains(address)) throw new InvalidDataException("Wrong delivery vault"); entries.Add(entry);
            }
            return (address, (IReadOnlyList<JournalEntry>)entries.AsReadOnly());
        }, MaximumFrameBody);

        internal static byte[] Encode(Action<BinaryWriter> write, int maximum)
        {
            using (var stream = new LimitedStream(maximum))
            using (var writer = new BinaryWriter(stream, Utf8))
            { write(writer); writer.Flush(); return stream.ToArray(); }
        }
        internal static T Decode<T>(byte[] bytes, Func<BinaryReader, T> read, int maximum)
        {
            if (bytes == null || bytes.Length > maximum) throw new InvalidDataException("Journal message exceeds limit");
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, Utf8))
                {
                    T result = read(reader); if (stream.Position != stream.Length) throw new InvalidDataException("Trailing journal bytes"); return result;
                }
            }
            catch (Exception e) when (e is EndOfStreamException || e is ArgumentException || e is OverflowException)
            { throw new InvalidDataException("Malformed journal message", e); }
        }
        internal static void Id(BinaryWriter w, Guid value) => w.Write(value.ToByteArray());
        internal static Guid Id(BinaryReader r) { var bytes = r.ReadBytes(16); if (bytes.Length != 16) throw new EndOfStreamException(); return new Guid(bytes); }
        internal static void Bytes(BinaryWriter w, byte[] bytes, int maximum)
        { if (bytes.Length > maximum) throw new InvalidDataException("Journal field exceeds limit"); w.Write(bytes.Length); w.Write(bytes); }
        internal static byte[] Bytes(BinaryReader r, int maximum)
        {
            int n = r.ReadInt32(); if (n < 0 || n > maximum || n > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid journal field length"); return r.ReadBytes(n);
        }
        private static void Text(BinaryWriter w, string s, int maximum = 256) => Bytes(w, Utf8.GetBytes(s), maximum);
        private static string Text(BinaryReader r, int maximum = 256) => Utf8.GetString(Bytes(r, maximum));
        private static void Identity(BinaryWriter w, VaultIdentity v) { Text(w, v.World); Text(w, v.Vault); Id(w, v.Epoch); }
        private static VaultIdentity Identity(BinaryReader r) => new VaultIdentity(Text(r), Text(r), Id(r));
        private static void Address(BinaryWriter w, SessionAddress a) { Identity(w, new VaultIdentity(a.World, a.Vault, a.Epoch)); Text(w, a.Player); Id(w, a.Session); }
        private static SessionAddress Address(BinaryReader r) { var v = Identity(r); return new SessionAddress(v.World, v.Vault, v.Epoch, Text(r), Id(r)); }
        private static void Item(BinaryWriter w, ItemRecord i) { Text(w, i.Prefab); w.Write(i.Quantity); w.Write(i.FormatVersion); Bytes(w, i.CopyPayload(), MaximumItemBytes); }
        private static ItemRecord Item(BinaryReader r) => new ItemRecord(Text(r), r.ReadInt32(), r.ReadInt32(), Bytes(r, MaximumItemBytes));
        private static void Stack(BinaryWriter w, StackRecord s) { Id(w, s.Id); w.Write(s.Slot); Item(w, s.Item); }
        private static StackRecord Stack(BinaryReader r) => new StackRecord(Id(r), r.ReadInt32(), Item(r));
        private static void Snapshot(BinaryWriter w, JournalSnapshot s)
        {
            Identity(w, s.State.Identity); w.Write(s.Schema); w.Write(s.Sequence); Text(w, s.Checksum, 64); w.Write(s.State.Capacity);
            Count(w, s.State.Stacks.Count, 1000000); foreach (var stack in s.State.Stacks) Stack(w, stack);
        }
        private static JournalSnapshot Snapshot(BinaryReader r)
        {
            var v = Identity(r); int schema = r.ReadInt32(); long sequence = r.ReadInt64(); string checksum = Text(r, 64); int capacity = r.ReadInt32();
            int n = Count(r, 1000000, 32); var stacks = new List<StackRecord>(); for (int i = 0; i < n; i++) stacks.Add(Stack(r));
            return new JournalSnapshot(sequence, new VaultState(v, capacity, stacks), checksum, schema);
        }
        private static void Entry(BinaryWriter w, JournalEntry e)
        {
            Identity(w, e.Identity); w.Write(e.Schema); w.Write(e.Sequence); Id(w, e.OperationId); Id(w, e.RequestId); w.Write((byte)e.Kind);
            w.Write(e.Actor != null); if (e.Actor != null) Address(w, e.Actor);
            w.Write(e.ChangedStack != null); if (e.ChangedStack != null) Stack(w, e.ChangedStack);
            w.Write(e.DeletedStack.HasValue); if (e.DeletedStack.HasValue) Id(w, e.DeletedStack.Value);
            w.Write(e.Effect != null);
            if (e.Effect != null) { var f = e.Effect; Id(w, f.EffectId); Id(w, f.OperationId); Id(w, f.RequestId); Address(w, f.Recipient); w.Write((byte)f.Kind); Item(w, f.Item); }
            Text(w, e.Checksum, 64);
        }
        private static JournalEntry Entry(BinaryReader r)
        {
            var v = Identity(r); int schema = r.ReadInt32(); long sequence = r.ReadInt64(); var operation = Id(r); var request = Id(r); var kind = (JournalEntryKind)r.ReadByte();
            var actor = Flag(r) ? Address(r) : null; var stack = Flag(r) ? Stack(r) : null; Guid? deleted = Flag(r) ? Id(r) : (Guid?)null;
            var effect = Flag(r) ? new CharacterEffect(Id(r), Id(r), Id(r), Address(r), (CharacterEffectKind)r.ReadByte(), Item(r)) : null;
            return new JournalEntry(v, sequence, operation, request, kind, actor, stack, deleted, effect, Text(r, 64), schema);
        }
        private static bool Flag(BinaryReader r) { byte b = r.ReadByte(); if (b > 1) throw new InvalidDataException("Invalid boolean"); return b == 1; }
        private static void Count(BinaryWriter w, int n, int maximum) { if (n < 0 || n > maximum) throw new InvalidDataException("Invalid count"); w.Write(n); }
        private static int Count(BinaryReader r, int maximum, int minimumBytes)
        { int n = r.ReadInt32(); if (n < 0 || n > maximum || n > (r.BaseStream.Length - r.BaseStream.Position) / minimumBytes) throw new InvalidDataException("Invalid count"); return n; }
        // Stop encoding before allocating an unbounded snapshot or frame.
        private sealed class LimitedStream : MemoryStream
        {
            private readonly int maximum;
            public LimitedStream(int maximum) { this.maximum = maximum; }
            public override void Write(byte[] buffer, int offset, int count)
            { if (count > maximum - Position) throw new InvalidDataException("Journal message exceeds limit"); base.Write(buffer, offset, count); }
            public override void WriteByte(byte value)
            { if (Position >= maximum) throw new InvalidDataException("Journal message exceeds limit"); base.WriteByte(value); }
        }
    }
}
