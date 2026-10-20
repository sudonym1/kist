using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThorsKist.Sync
{
    // UTC persists cooldowns; elapsed time protects live deadlines from wall-clock jumps.
    public interface IReplicationClock : IClock { TimeSpan Elapsed { get; } }
    public sealed class SystemReplicationClock : IReplicationClock
    {
        private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public TimeSpan Elapsed => elapsed.Elapsed;
        public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
    }
    public enum SessionStatus { Ok, SessionExists, Suspended, InvalidSession, InvalidAcknowledgment, RequestConflict, TransactionDepthExceeded }
    public sealed class SessionReply
    {
        public SessionStatus Status { get; }
        public ReplicationSession? Session { get; }
        private readonly SessionPhase? phase;
        public SessionPhase? Phase => Session?.Phase ?? phase;
        public CommandTransition? Command { get; }
        public IReadOnlyList<JournalEntry> Entries { get; }
        public IReadOnlyList<Guid> SettledEffects { get; }
        public SessionReply(SessionStatus status, ReplicationSession? session = null, CommandTransition? command = null,
            IEnumerable<JournalEntry>? entries = null, IEnumerable<Guid>? settled = null, SessionPhase? phase = null)
        { if (!Enum.IsDefined(typeof(SessionStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
            if (phase.HasValue && !Enum.IsDefined(typeof(SessionPhase), phase.Value)) throw new ArgumentOutOfRangeException(nameof(phase));
            Status = status; Session = session; this.phase = phase; Command = command; Entries = Array.AsReadOnly((entries ?? Array.Empty<JournalEntry>()).ToArray()); SettledEffects = Array.AsReadOnly((settled ?? Array.Empty<Guid>()).ToArray()); }
    }
    // Owns its writer exclusively. Calls enqueue in invocation order; no transport,
    // Unity callbacks or waits for peers run under the writer. Authentication and
    // membership checks precede this boundary; player strings must be authenticated IDs.
    public sealed class ReplicationServer : IDisposable
    {
        private readonly JournalWriter writer;
        private readonly IReplicationClock clock;
        private readonly DateTimeOffset originUtc;
        private readonly TimeSpan originElapsed;
        private readonly int maximumDepth;
        private readonly object sync = new object();
        private Task tail = Task.CompletedTask;
        private JournalMachineState installed;
        private readonly Dictionary<string, (ItemCommand command, Task<SessionReply> task)> admitted = new Dictionary<string, (ItemCommand, Task<SessionReply>)>();
        private bool disposed;
        private DateTimeOffset Now => originUtc + (clock.Elapsed - originElapsed);
        private ReplicationServer(JournalWriter writer, JournalMachineState state, IReplicationClock clock, int maximumDepth)
        { this.writer = writer; installed = state; this.clock = clock; this.maximumDepth = maximumDepth; originUtc = clock.UtcNow; originElapsed = clock.Elapsed; }
        public static async Task<ReplicationServer> OpenAsync(IJournalStorage storage, VaultState initial, IReplicationClock clock, int maximumDepth)
        {
            if (maximumDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
            var writer = await JournalWriter.OpenAsync(storage, initial).ConfigureAwait(false);
            try
            {
                var server = new ReplicationServer(writer, await writer.ReadAsync().ConfigureAwait(false), clock, maximumDepth);
                // Invalidate and settle all previous sessions before access/checkpointing.
                await server.Change(state =>
                {
                    var sessions = Copies(state); var settlements = state.Replication.Settlements.ToList();
                    foreach (var session in sessions.Where(s => s.Phase != SessionPhase.Closed)) state = Close(state, session, settlements, null);
                    // Pre-session M3 images may contain accepted effects without a
                    // session row. Their delivery is unknown: settle conservatively.
                    foreach (var address in state.Results.Where(r => r.Entry.Effect != null && !sessions.Any(s => JournalChecksum.SameAddress(s.Address, r.Command.Address)))
                        .Select(r => r.Command.Address).GroupBy(a => a.Session).Select(g => g.First()).ToArray())
                        state = Settle(state, address, settlements);
                    return Finish(state, sessions, settlements, server.Now + TimeSpan.FromSeconds(10));
                }).ConfigureAwait(false);
                return server;
            }
            catch { writer.Dispose(); throw; }
        }
        private Task<T> Enqueue<T>(Func<Task<T>> action)
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(ReplicationServer));
                var previous = tail;
                var waiting = JournalDiagnostics.Measure("server.serial_queue.wait", JournalTimingKind.Wait, JournalTimingThread.Any);
                var task = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { /* Writer faults remain latched. */ }
                    waiting.Dispose();
                    using (JournalDiagnostics.Measure("server.operation.elapsed", JournalTimingKind.Elapsed, JournalTimingThread.Any))
                        return await action().ConfigureAwait(false);
                });
                tail = task; return task;
            }
        }
        private async Task<JournalMachineState> Change(Func<JournalMachineState, JournalMachineState> change)
        {
            var next = await writer.TransitionAsync(change).ConfigureAwait(false);
            lock (sync) installed = next;
            return next;
        }
        public Task<JournalMachineState> ReadAsync() => Enqueue(() => writer.ReadAsync());
        private static List<ReplicationSession> Copies(JournalMachineState s) => s.Replication.Sessions.Select(x => x.Copy()).ToList();
        private static ReplicationSession? Find(IEnumerable<ReplicationSession> sessions, SessionAddress address) => sessions.FirstOrDefault(s => JournalChecksum.SameAddress(s.Address, address));
        private static JournalMachineState Finish(JournalMachineState s, IEnumerable<ReplicationSession> sessions, IEnumerable<EffectSettlement> settlements, DateTimeOffset? next = null)
            => s.WithReplication(new ReplicationState(checked(s.Replication.Revision + 1), next ?? s.Replication.NextHeartbeat, sessions, settlements));
        private static IEnumerable<Guid> Settled(JournalMachineState state, ReplicationSession? session) => state.Results.Where(r => session != null
            && r.Entry.Sequence > session.Baseline.Sequence && r.Entry.Sequence <= session.LoginMarker && r.Entry.Effect != null
            && state.Replication.Settlements.Any(s => s.Operation == r.Entry.OperationId)).Select(r => r.Entry.Effect!.EffectId);
        private static JournalMachineState Marker(JournalMachineState state, SessionAddress address, Guid request, JournalEntryKind kind)
        {
            var entry = new JournalEntry(state.Current.Identity, checked(state.Head + 1), Guid.NewGuid(), request, kind,
                address, null, null, null, JournalChecksum.Compute(state.Current));
            return new JournalMachineState(state.Checkpoint, state.Current, entry.Sequence, state.Journal.Concat(new[] { entry }), state.Results, state.UsedStackIds, state.Replication);
        }
        public Task<SessionReply> LoginAsync(string authenticatedPlayer, Guid request) => Enqueue(async () =>
        {
            if (string.IsNullOrWhiteSpace(authenticatedPlayer) || request == Guid.Empty) throw new ArgumentException("Login identity required");
            SessionStatus status = SessionStatus.Ok; ReplicationSession? answer = null;
            var state = await Change(s =>
            {
                answer = s.Replication.Sessions.FirstOrDefault(x => x.Address.Player == authenticatedPlayer && x.LoginRequest == request);
                if (answer != null) return s;
                if (s.Replication.Sessions.Any(x => x.Address.Player == authenticatedPlayer && x.SuspendedUntil > Now)) { status = SessionStatus.Suspended; return s; }
                if (s.Replication.Sessions.Any(x => x.Address.Player == authenticatedPlayer && x.Phase != SessionPhase.Closed)) { status = SessionStatus.SessionExists; return s; }
                var v = s.Current.Identity; var address = new SessionAddress(v.World, v.Vault, v.Epoch, authenticatedPlayer, Guid.NewGuid());
                var baseline = s.Checkpoint; s = Marker(s, address, request, JournalEntryKind.Login);
                var catchup = s.Journal.ToArray();
                answer = new ReplicationSession(address, request, baseline, catchup, s.Head, SessionPhase.LoggingIn,
                    baseline.Sequence, s.Head, Guid.Empty, 0, Now + TimeSpan.FromSeconds(30), null,
                    catchup.Select(e => new DeliveryDeadline(e.Sequence, Now + TimeSpan.FromSeconds(30))));
                return Finish(s, s.Replication.Sessions.Concat(new[] { answer }), s.Replication.Settlements);
            }).ConfigureAwait(false);
            return new SessionReply(status, answer, entries: answer?.Catchup, settled: Settled(state, answer));
        });
        private static bool Same(ItemCommand a, ItemCommand b) => JournalChecksum.SameAddress(a.Address, b.Address)
            && a.Kind == b.Kind && a.Purpose == b.Purpose && a.StackId == b.StackId && JournalChecksum.SameItem(a.Item, b.Item);
        public Task<SessionReply> ExecuteAsync(ItemCommand command)
        {
            lock (sync)
            {
                string key = command.Address.Player + ":" + command.RequestId.ToString("N");
                if (admitted.TryGetValue(key, out var queued))
                    return Same(command, queued.command) ? queued.task : Task.FromResult(new SessionReply(SessionStatus.RequestConflict));
                bool known = installed.Current.Identity.Contains(command.Address) && installed.Results.Any(r => r.Command.Address.Player == command.Address.Player && r.Command.RequestId == command.RequestId);
                // Admission checks and reservation happen atomically before queuing.
                if (!known)
                {
                    var session = Find(installed.Replication.Sessions, command.Address);
                    if (session == null || session.Phase != SessionPhase.Active) return Task.FromResult(new SessionReply(SessionStatus.InvalidSession));
                    int pending = installed.Results.Count(r => r.Entry.Effect != null && !installed.Replication.Settlements.Any(s => s.Operation == r.Entry.OperationId));
                    if (pending + admitted.Values.Count(q => !installed.Results.Any(r => r.Command.Address.Player == q.command.Address.Player && r.Command.RequestId == q.command.RequestId)) >= maximumDepth) return Task.FromResult(new SessionReply(SessionStatus.TransactionDepthExceeded));
                }
                var task = Enqueue(async () =>
                {
                    try
                    {
                        CommandTransition? transition = null; var status = SessionStatus.Ok;
                        await Change(s =>
                        {
                            bool retry = s.Current.Identity.Contains(command.Address) && s.Results.Any(r => r.Command.Address.Player == command.Address.Player && r.Command.RequestId == command.RequestId);
                            var session = Find(s.Replication.Sessions, command.Address);
                            if (!retry && (session == null || session.Phase != SessionPhase.Active)) { status = SessionStatus.InvalidSession; return s; }
                            transition = JournalMachine.Execute(s, command); return transition.State;
                        }).ConfigureAwait(false);
                        return new SessionReply(status, command: transition);
                    }
                    finally { lock (sync) admitted.Remove(key); }
                });
                if (!known) admitted.Add(key, (command, task));
                return task;
            }
        }
        public Task<SessionReply> QueueDeliveryAsync(SessionAddress address, long after, int maximumEntries) => Enqueue(async () =>
        {
            if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
            SessionReply reply = new SessionReply(SessionStatus.InvalidSession);
            await Change(s =>
            {
                var sessions = Copies(s); var session = Find(sessions, address);
                if (session == null || session.Phase == SessionPhase.Closed) return s;
                if (after < session.Acknowledged || after > session.QueuedThrough) { reply = new SessionReply(SessionStatus.InvalidAcknowledgment); return s; }
                var entries = s.Journal.Where(e => e.Sequence > after && (session.Phase != SessionPhase.LoggingOut || e.Sequence <= session.LogoutMarker)).Take(maximumEntries).ToArray();
                var deadlines = session.Deadlines.ToList();
                foreach (var e in entries.Where(e => e.Sequence > session.QueuedThrough)) deadlines.Add(new DeliveryDeadline(e.Sequence, Now + TimeSpan.FromSeconds(30)));
                session.Deadlines = deadlines.AsReadOnly();
                if (entries.Length > 0) session.QueuedThrough = Math.Max(session.QueuedThrough, entries.Last().Sequence);
                reply = new SessionReply(SessionStatus.Ok, session, entries: entries);
                return Finish(s, sessions, s.Replication.Settlements);
            }).ConfigureAwait(false); return reply;
        });
        public Task<SessionReply> AcknowledgeAsync(SessionAddress address, long sequence) => Enqueue(async () =>
        {
            SessionReply reply = new SessionReply(SessionStatus.InvalidSession);
            await Change(s =>
            {
                var sessions = Copies(s); var session = Find(sessions, address);
                if (session == null || session.Phase == SessionPhase.Closed) return s;
                if (sequence < session.Baseline.Sequence || sequence > session.QueuedThrough) { reply = new SessionReply(SessionStatus.InvalidAcknowledgment); return s; }
                bool hadActive = sessions.Any(x => x.Phase == SessionPhase.Active);
                session.Acknowledged = Math.Max(session.Acknowledged, sequence);
                session.Deadlines = Array.AsReadOnly(session.Deadlines.Where(d => d.Sequence > sequence).ToArray());
                var settlements = s.Replication.Settlements.ToList();
                foreach (var r in s.Results.Where(r => r.Entry.Effect != null && JournalChecksum.SameAddress(r.Command.Address, address) && r.Entry.Sequence <= sequence))
                    if (!settlements.Any(x => x.Operation == r.Entry.OperationId)) settlements.Add(new EffectSettlement(r.Entry.OperationId, SettlementKind.Processed, Guid.Empty));
                if (session.Phase == SessionPhase.LoggingIn && sequence >= session.LoginMarker) session.Phase = SessionPhase.Active;
                if (session.Phase == SessionPhase.LoggingOut && sequence >= session.LogoutMarker) { session.Phase = SessionPhase.Closed; session.Deadlines = Array.Empty<DeliveryDeadline>(); session.ReleaseLoginImage(); }
                reply = new SessionReply(SessionStatus.Ok, session);
                return Finish(s, sessions, settlements, !hadActive && session.Phase == SessionPhase.Active ? Now + TimeSpan.FromSeconds(10) : (DateTimeOffset?)null);
            }).ConfigureAwait(false); return reply;
        });
        public Task<SessionReply> LogoutAsync(SessionAddress address, Guid request) => Enqueue(async () =>
        {
            if (request == Guid.Empty) throw new ArgumentException("Logout request required");
            SessionReply reply = new SessionReply(SessionStatus.InvalidSession);
            await Change(s =>
            {
                var sessions = Copies(s); var session = Find(sessions, address);
                if (session == null) return s;
                if (session.LogoutRequest == request) { reply = new SessionReply(SessionStatus.Ok, session); return s; }
                if (session.Phase != SessionPhase.Active) return s;
                s = Marker(s, address, request, JournalEntryKind.Logout);
                session.Phase = SessionPhase.LoggingOut; session.LogoutRequest = request; session.LogoutMarker = s.Head;
                session.BarrierDue = Now + TimeSpan.FromSeconds(30);
                reply = new SessionReply(SessionStatus.Ok, session);
                return Finish(s, sessions, s.Replication.Settlements);
            }).ConfigureAwait(false); return reply;
        });
        private static JournalMachineState Close(JournalMachineState state, ReplicationSession session, List<EffectSettlement> settlements, DateTimeOffset? suspension)
        {
            state = Settle(state, session.Address, settlements);
            session.Phase = SessionPhase.Closed; session.Deadlines = Array.Empty<DeliveryDeadline>(); session.SuspendedUntil = suspension;
            session.ReleaseLoginImage();
            return state;
        }
        private static JournalMachineState Settle(JournalMachineState state, SessionAddress address, List<EffectSettlement> settlements)
        {
            foreach (var r in state.Results.Where(r => r.Entry.Effect != null && JournalChecksum.SameAddress(r.Command.Address, address)).ToArray())
            {
                if (settlements.Any(x => x.Operation == r.Entry.OperationId)) continue;
                Guid compensation = Guid.Empty;
                var kind = SettlementKind.DepositCompleted;
                if (r.Command.Kind == ItemCommandKind.Withdraw)
                {
                    // Separate overflow stack avoids game-specific merge/stack limits.
                    // M5 may add bounded merging with the real item schema.
                    var stack = new StackRecord(Guid.NewGuid(), -1, r.Command.Item);
                    var current = new VaultState(state.Current.Identity, state.Current.Capacity, state.Current.Stacks.Concat(new[] { stack }));
                    compensation = Guid.NewGuid(); kind = SettlementKind.WithdrawalReturned;
                    var entry = new JournalEntry(current.Identity, checked(state.Head + 1), compensation, r.Entry.OperationId,
                        JournalEntryKind.Compensation, address, stack, null, null, JournalChecksum.Compute(current));
                    state = new JournalMachineState(state.Checkpoint, current, entry.Sequence, state.Journal.Concat(new[] { entry }),
                        state.Results, state.UsedStackIds.Concat(new[] { stack.Id }), state.Replication);
                }
                settlements.Add(new EffectSettlement(r.Entry.OperationId, kind, compensation));
            }
            return state;
        }
        public Task<SessionReply> ExcludeAsync(SessionAddress address, bool suspend) => Enqueue(async () =>
        {
            SessionReply reply = new SessionReply(SessionStatus.InvalidSession);
            await Change(s =>
            {
                var sessions = Copies(s); var session = Find(sessions, address);
                if (session == null) return s;
                reply = new SessionReply(SessionStatus.Ok, session);
                if (session.Phase == SessionPhase.Closed) return s; // retries never extend cooldown/return twice
                var settlements = s.Replication.Settlements.ToList();
                s = Close(s, session, settlements, suspend ? Now + TimeSpan.FromSeconds(30) : (DateTimeOffset?)null);
                return Finish(s, sessions, settlements);
            }).ConfigureAwait(false); return reply;
        });
        public Task<JournalMachineState> TickAsync()
        {
            using var context = JournalDiagnostics.Correlate(Guid.Empty, 0, "tick");
            return Enqueue(() => Change(s =>
        {
            var sessions = Copies(s); var settlements = s.Replication.Settlements.ToList();
            foreach (var session in sessions.Where(x => x.Phase != SessionPhase.Closed))
                if ((session.Phase != SessionPhase.Active && Now >= session.BarrierDue) || session.Deadlines.Any(d => Now >= d.Due))
                    s = Close(s, session, settlements, Now + TimeSpan.FromSeconds(30));
            var next = s.Replication.NextHeartbeat;
            if (sessions.Any(x => x.Phase == SessionPhase.Active) && Now >= next)
            { s = JournalMachine.AppendNoOp(s); next = Now + TimeSpan.FromSeconds(10); }
            return Finish(s, sessions, settlements, next);
        }));
        }
        public Task<JournalMachineState> CheckpointAsync()
        {
            using var context = JournalDiagnostics.Correlate(Guid.Empty, 0, "checkpoint");
            return Enqueue(() => Change(s =>
        {
            long cutoff = s.Replication.Sessions.Where(x => x.Phase != SessionPhase.Closed).Select(x => x.Acknowledged).DefaultIfEmpty(s.Head).Min();
            // Protect even unexpected/unowned pending effects rather than prune them.
            cutoff = Math.Min(cutoff, s.Results.Where(r => r.Entry.Effect != null && !s.Replication.Settlements.Any(x => x.Operation == r.Entry.OperationId))
                .Select(r => r.Entry.Sequence - 1).DefaultIfEmpty(s.Head).Min());
            return JournalMachine.Checkpoint(s, cutoff);
        }));
        }
        public void Dispose()
        {
            Task pending; lock (sync) { disposed = true; pending = tail; }
            try { pending.GetAwaiter().GetResult(); } catch { /* Operation callers observe faults; disposal still releases storage. */ } finally { writer.Dispose(); }
        }
    }
    public static class AdmissionRetry
    {
        public static async Task<SessionReply> ExecuteAsync(ItemCommand command, Func<ItemCommand, Task<SessionReply>> send, IClock clock, CancellationToken token = default)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested(); var reply = await send(command).ConfigureAwait(false);
                if (reply.Status != SessionStatus.TransactionDepthExceeded) return reply;
                await clock.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
            }
        }
    }
}
