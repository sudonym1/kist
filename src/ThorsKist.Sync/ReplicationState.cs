using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ThorsKist.Sync
{
    public enum SessionPhase { LoggingIn, Active, LoggingOut, Closed }
    public enum SettlementKind { Processed, DepositCompleted, WithdrawalReturned }
    public sealed class EffectSettlement
    {
        public Guid Operation { get; }
        public SettlementKind Kind { get; }
        public Guid Compensation { get; }
        public EffectSettlement(Guid operation, SettlementKind kind, Guid compensation)
        { Operation = operation; Kind = kind; Compensation = compensation; }
    }
    public sealed class DeliveryDeadline
    {
        public long Sequence { get; }
        public DateTimeOffset Due { get; }
        public DeliveryDeadline(long sequence, DateTimeOffset due) { Sequence = sequence; Due = due; }
    }
    public sealed class ReplicationSession
    {
        public SessionAddress Address { get; }
        public Guid LoginRequest { get; }
        public JournalSnapshot Baseline { get; private set; }
        public IReadOnlyList<JournalEntry> Catchup { get; private set; }
        public bool LoginImageReleased { get; private set; }
        public long LoginMarker { get; }
        public SessionPhase Phase { get; internal set; }
        public long Acknowledged { get; internal set; }
        public long QueuedThrough { get; internal set; }
        public Guid LogoutRequest { get; internal set; }
        public long LogoutMarker { get; internal set; }
        public DateTimeOffset BarrierDue { get; internal set; }
        public DateTimeOffset? SuspendedUntil { get; internal set; }
        public IReadOnlyList<DeliveryDeadline> Deadlines { get; internal set; }
        public ReplicationSession(SessionAddress address, Guid loginRequest, JournalSnapshot baseline,
            IEnumerable<JournalEntry> catchup, long loginMarker, SessionPhase phase, long acknowledged, long queuedThrough,
            Guid logoutRequest, long logoutMarker, DateTimeOffset barrierDue, DateTimeOffset? suspendedUntil,
            IEnumerable<DeliveryDeadline> deadlines, bool loginImageReleased = false)
        {
            Address = address; LoginRequest = loginRequest; Baseline = baseline;
            Catchup = Array.AsReadOnly(catchup.ToArray()); LoginMarker = loginMarker; Phase = phase;
            Acknowledged = acknowledged; QueuedThrough = queuedThrough; LogoutRequest = logoutRequest;
            LogoutMarker = logoutMarker; BarrierDue = barrierDue; SuspendedUntil = suspendedUntil;
            Deadlines = Array.AsReadOnly(deadlines.ToArray());
            LoginImageReleased = loginImageReleased;
        }
        internal void ReleaseLoginImage()
        {
            if (Phase != SessionPhase.Closed) throw new InvalidOperationException("Open session needs its immutable login image");
            var empty = new VaultState(Baseline.State.Identity, Baseline.State.Capacity, Array.Empty<StackRecord>());
            Baseline = new JournalSnapshot(Baseline.Sequence, empty, JournalChecksum.Compute(empty));
            Catchup = Array.Empty<JournalEntry>(); LoginImageReleased = true;
        }
        public ReplicationSession CompactClosed()
        {
            var copy = Copy(); copy.ReleaseLoginImage(); return copy;
        }
        internal ReplicationSession Copy() => new ReplicationSession(Address, LoginRequest, Baseline, Catchup,
            LoginMarker, Phase, Acknowledged, QueuedThrough, LogoutRequest, LogoutMarker, BarrierDue, SuspendedUntil, Deadlines, LoginImageReleased);
    }
    public sealed class ReplicationState
    {
        public static ReplicationState Empty { get; } = new ReplicationState(0, DateTimeOffset.MinValue,
            Array.Empty<ReplicationSession>(), Array.Empty<EffectSettlement>());
        public long Revision { get; }
        public DateTimeOffset NextHeartbeat { get; }
        public IReadOnlyList<ReplicationSession> Sessions { get; }
        public IReadOnlyList<EffectSettlement> Settlements { get; }
        public ReplicationState(long revision, DateTimeOffset nextHeartbeat, IEnumerable<ReplicationSession> sessions, IEnumerable<EffectSettlement> settlements)
        { Revision = revision; NextHeartbeat = nextHeartbeat; Sessions = Array.AsReadOnly(sessions.ToArray()); Settlements = Array.AsReadOnly(settlements.ToArray()); }
        public void Validate(JournalMachineState state)
        {
            if (Revision < 0 || Sessions.Select(s => s.Address.Session).Distinct().Count() != Sessions.Count
                || Sessions.GroupBy(s => new { s.Address.Player, s.LoginRequest }).Any(g => g.Count() > 1)
                || Sessions.Where(s => s.Phase != SessionPhase.Closed).GroupBy(s => s.Address.Player).Any(g => g.Count() > 1)
                || Settlements.Select(s => s.Operation).Distinct().Count() != Settlements.Count)
                throw new InvalidDataException("Invalid replication identities");
            foreach (var s in Sessions)
            {
                if (!state.Current.Identity.Contains(s.Address) || !Enum.IsDefined(typeof(SessionPhase), s.Phase)
                    || s.LoginRequest == Guid.Empty || s.LoginMarker <= s.Baseline.Sequence || s.LoginMarker > state.Head
                    || s.Acknowledged < s.Baseline.Sequence || s.Acknowledged > s.QueuedThrough || s.QueuedThrough > state.Head
                    || (s.Phase != SessionPhase.Closed && s.Acknowledged < state.Checkpoint.Sequence)
                    || (s.Phase == SessionPhase.Active && s.Acknowledged < s.LoginMarker)
                    || (s.Phase == SessionPhase.LoggingIn && s.Acknowledged >= s.LoginMarker)
                    || (s.LogoutMarker != 0 && (s.LogoutRequest == Guid.Empty || s.LogoutMarker <= s.LoginMarker || s.LogoutMarker > state.Head))
                    || (s.Phase == SessionPhase.LoggingOut && s.LogoutMarker == 0)
                    || s.Deadlines.Any(d => d.Sequence <= s.Acknowledged || d.Sequence > s.QueuedThrough)
                    || s.Deadlines.Select(d => d.Sequence).Distinct().Count() != s.Deadlines.Count
                    || (s.Phase != SessionPhase.Closed && s.Deadlines.Count != s.QueuedThrough - s.Acknowledged))
                    throw new InvalidDataException("Invalid session position");
                if (s.LoginImageReleased)
                {
                    if (s.Phase != SessionPhase.Closed || s.Baseline.State.Stacks.Count != 0 || s.Catchup.Count != 0
                        || s.Deadlines.Count != 0 || s.Baseline.Checksum != JournalChecksum.Compute(s.Baseline.State))
                        throw new InvalidDataException("Invalid closed session tombstone");
                    continue;
                }
                var catchup = JournalReplay.Replay(s.Baseline, s.Catchup);
                var marker = s.Catchup.LastOrDefault();
                if (catchup.Sequence != s.LoginMarker || marker == null || marker.Kind != JournalEntryKind.Login
                    || marker.Actor == null || !JournalChecksum.SameAddress(marker.Actor, s.Address) || marker.RequestId != s.LoginRequest)
                    throw new InvalidDataException("Invalid login barrier");
            }
            foreach (var marker in state.Journal.Where(e => e.Kind == JournalEntryKind.Login || e.Kind == JournalEntryKind.Logout))
            {
                var session = Sessions.SingleOrDefault(s => marker.Actor != null && JournalChecksum.SameAddress(s.Address, marker.Actor));
                if (session == null || (marker.Kind == JournalEntryKind.Login
                    ? marker.Sequence != session.LoginMarker || marker.RequestId != session.LoginRequest
                    : marker.Sequence != session.LogoutMarker || marker.RequestId != session.LogoutRequest))
                    throw new InvalidDataException("Unbound session barrier");
            }
            foreach (var settled in Settlements)
            {
                var result = state.Results.SingleOrDefault(r => r.Entry.OperationId == settled.Operation);
                if (result?.Entry.Effect == null || !Enum.IsDefined(typeof(SettlementKind), settled.Kind)
                    || (settled.Kind == SettlementKind.WithdrawalReturned ? result.Command.Kind != ItemCommandKind.Withdraw || settled.Compensation == Guid.Empty : settled.Compensation != Guid.Empty)
                    || (settled.Kind == SettlementKind.DepositCompleted && result.Command.Kind != ItemCommandKind.Deposit))
                    throw new InvalidDataException("Invalid effect settlement");
            }
            foreach (var result in state.Results.Where(r => r.Entry.Effect != null))
            {
                var session = Sessions.SingleOrDefault(s => JournalChecksum.SameAddress(s.Address, result.Command.Address));
                if (session != null && (session.Phase == SessionPhase.Closed || result.Entry.Sequence <= session.Acknowledged)
                    && !Settlements.Any(s => s.Operation == result.Entry.OperationId))
                    throw new InvalidDataException("Missing effect settlement");
            }
            foreach (var entry in state.Journal.Where(e => e.Kind == JournalEntryKind.Compensation))
                if (!Settlements.Any(s => s.Operation == entry.RequestId && s.Compensation == entry.OperationId && s.Kind == SettlementKind.WithdrawalReturned))
                    throw new InvalidDataException("Unsettled compensation");
        }
    }
}
