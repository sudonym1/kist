using System;
using System.IO;

namespace ThorsKist.Sync
{
    // Compact control reply. Character effects are authorized exclusively by
    // verified Entries/Transfer frames, never by this result packet.
    public sealed class JournalReply
    {
        public JournalMessage Request { get; }
        public SessionStatus Status { get; }
        public SessionAddress? Address { get; }
        public SessionPhase? Phase { get; }
        public long Acknowledged { get; }
        public long Marker { get; }
        public DateTimeOffset? SuspendedUntil { get; }
        public CommandStatus? Command { get; }
        public Guid Operation { get; }
        public long Sequence { get; }
        public Guid Transfer { get; }

        public JournalReply(JournalMessage request, SessionStatus status, SessionAddress? address = null,
            SessionPhase? phase = null, long acknowledged = 0, long marker = 0, DateTimeOffset? suspendedUntil = null,
            CommandStatus? command = null, Guid operation = default, long sequence = 0, Guid transfer = default)
        {
            if (request < JournalMessage.Login || request > JournalMessage.Suspend
                || !Enum.IsDefined(typeof(SessionStatus), status) || acknowledged < 0 || marker < 0 || sequence < 0
                || (address == null) != !phase.HasValue || (phase.HasValue && !Enum.IsDefined(typeof(SessionPhase), phase.Value))
                || (command.HasValue && (!Enum.IsDefined(typeof(CommandStatus), command.Value) || request != JournalMessage.Command))
                || (operation == Guid.Empty) != (sequence == 0)
                || (transfer != Guid.Empty && (request != JournalMessage.Login || status != SessionStatus.Ok || address == null || phase == SessionPhase.Closed)))
                throw new ArgumentException("Invalid journal reply");
            Request = request; Status = status; Address = address; Phase = phase; Acknowledged = acknowledged;
            Marker = marker; SuspendedUntil = suspendedUntil; Command = command; Operation = operation; Sequence = sequence; Transfer = transfer;
        }
        public static JournalReply From(JournalMessage request, SessionReply reply, Guid transfer = default)
        {
            var s = reply.Session; var c = reply.Command;
            return new JournalReply(request, reply.Status, s?.Address, s?.Phase, s?.Acknowledged ?? 0,
                request == JournalMessage.Login ? s?.LoginMarker ?? 0 : s?.LogoutMarker ?? 0,
                s?.SuspendedUntil, c?.Status, c?.Result?.Entry.OperationId ?? Guid.Empty, c?.Result?.Entry.Sequence ?? 0, transfer);
        }
        public byte[] Encode() => JournalWire.Encode(w =>
        {
            w.Write((byte)Request); w.Write((byte)Status); w.Write(Address != null);
            if (Address != null) { JournalWire.Address(w, Address); w.Write((byte)Phase!.Value); }
            w.Write(Acknowledged); w.Write(Marker); w.Write(SuspendedUntil.HasValue);
            if (SuspendedUntil.HasValue) w.Write(SuspendedUntil.Value.UtcDateTime.Ticks);
            w.Write(Command.HasValue); if (Command.HasValue) w.Write((byte)Command.Value);
            JournalWire.Id(w, Operation); w.Write(Sequence); JournalWire.Id(w, Transfer);
        }, JournalWire.MaximumFrameBody);
        public static JournalReply Decode(byte[] bytes) => JournalWire.Decode(bytes, r =>
        {
            var request = (JournalMessage)r.ReadByte(); var status = (SessionStatus)r.ReadByte();
            SessionAddress? address = null; SessionPhase? phase = null;
            if (Flag(r)) { address = JournalWire.Address(r); phase = (SessionPhase)r.ReadByte(); }
            long acknowledged = r.ReadInt64(), marker = r.ReadInt64();
            DateTimeOffset? suspension = Flag(r) ? new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero) : (DateTimeOffset?)null;
            CommandStatus? command = Flag(r) ? (CommandStatus)r.ReadByte() : (CommandStatus?)null;
            return new JournalReply(request, status, address, phase, acknowledged, marker, suspension, command,
                JournalWire.Id(r), r.ReadInt64(), JournalWire.Id(r));
        }, JournalWire.MaximumFrameBody);
        private static bool Flag(BinaryReader r) { byte b = r.ReadByte(); if (b > 1) throw new InvalidDataException("Invalid reply flag"); return b == 1; }
    }
}
