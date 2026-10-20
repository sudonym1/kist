using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ThorsKist.Sync
{
    // Adapter supplies a dispatcher that schedules one bounded inventory action on
    // the game thread. Verification and transport run on workers. No client files.
    public sealed class ReplicationClient
    {
        private readonly JournalReplica replica;
        private readonly Func<Func<ReplicaResult>, Task<ReplicaResult>> dispatch;
        private readonly Func<SessionAddress, long, Task<SessionReply>> acknowledge;
        private readonly Func<SessionAddress, Task<SessionReply>> suspend;
        private readonly int maximumBatch;
        private readonly object sync = new object();
        private Task tail = Task.CompletedTask;
        private volatile bool requestsStopped;
        private SessionAddress? address;
        private volatile bool stopped = true, accessEnabled;
        public bool Stopped { get => stopped; private set => stopped = value; }
        public bool AccessEnabled { get => accessEnabled; private set => accessEnabled = value; }
        public long Sequence => replica.Sequence;
        public ReplicationClient(JournalReplica replica, Func<Func<ReplicaResult>, Task<ReplicaResult>> dispatch,
            Func<SessionAddress, long, Task<SessionReply>> acknowledge, Func<SessionAddress, Task<SessionReply>> suspend, int maximumBatch = 32)
        {
            if (maximumBatch <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBatch));
            this.replica = replica; this.dispatch = dispatch; this.acknowledge = acknowledge; this.suspend = suspend; this.maximumBatch = maximumBatch;
        }
        private Task<T> Enqueue<T>(Func<Task<T>> action)
        {
            lock (sync)
            {
                var previous = tail;
                var task = Task.Run(async () => { try { await previous.ConfigureAwait(false); } catch { /* The caller observes transport errors; queued retries can continue. */ } return await action().ConfigureAwait(false); });
                tail = task; return task;
            }
        }
        public Task<bool> BeginAsync(SessionReply login) => Enqueue(async () =>
        {
            if (login.Session != null && address != null && JournalChecksum.SameAddress(address, login.Session.Address))
            {
                if (login.Status != SessionStatus.Ok || login.Session.Phase == SessionPhase.Closed) { Stopped = true; AccessEnabled = false; }
                return !Stopped;
            }
            AccessEnabled = false; Stopped = true;
            if (login.Status != SessionStatus.Ok || login.Session == null || login.Session.Phase == SessionPhase.Closed) return false;
            address = login.Session.Address;
            if (!replica.Install(address, login.Session.Baseline, login.SettledEffects)) { await suspend(address).ConfigureAwait(false); return false; }
            requestsStopped = false; Stopped = false; return true;
        });
        public Task<int> ProcessBatchAsync(SessionAddress deliverySession, IReadOnlyList<JournalEntry> entries) => Enqueue(async () =>
        {
            if (entries.Count > maximumBatch) throw new ArgumentException("Split journal delivery into bounded batches");
            if (Stopped || address == null || !JournalChecksum.SameAddress(address, deliverySession)) return 0;
            int processed = 0;
            foreach (var entry in entries)
            {
                if (Stopped) break;
                ReplicaResult result;
                if (entry.Sequence <= replica.Sequence) result = replica.Process(deliverySession, entry);
                else
                {
                    VerifiedJournalUpdate update;
                    try { update = JournalReplay.Prepare(replica.Current!, replica.Sequence, entry); }
                    catch (System.IO.InvalidDataException)
                    {
                        replica.VerificationFailed(deliverySession); Stopped = true; AccessEnabled = false;
                        await suspend(address).ConfigureAwait(false); break;
                    }
                    result = await dispatch(() => Stopped ? ReplicaResult.Blocked : replica.ProcessVerified(deliverySession, update)).ConfigureAwait(false);
                }
                if (result == ReplicaResult.NeedsSuspension)
                { Stopped = true; AccessEnabled = false; await suspend(address).ConfigureAwait(false); break; }
                if (result != ReplicaResult.Applied && result != ReplicaResult.Duplicate)
                { Stopped = true; AccessEnabled = false; break; }
                processed++;
                var reply = await acknowledge(address, replica.Sequence).ConfigureAwait(false);
                AccessEnabled = !requestsStopped && reply.Status == SessionStatus.Ok && reply.Phase == SessionPhase.Active;
                if (reply.Status != SessionStatus.Ok || reply.Phase == SessionPhase.Closed)
                { Stopped = true; AccessEnabled = false; break; }
            }
            return processed;
        });
        public Task RetrySuspensionAsync() => Enqueue(async () =>
        {
            if (Stopped && replica.NeedsSuspension && address != null) await suspend(address).ConfigureAwait(false);
            return true;
        });
        public void StopRequests() { requestsStopped = true; AccessEnabled = false; }
        public void StopProcessing() { requestsStopped = true; Stopped = true; AccessEnabled = false; }
    }
}
