using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThorsKist.Sync
{
    // Control replies must remain independent of the application queue: an
    // application action may be waiting for an acknowledgment reply on this
    // same connection. Never await application/Unity work from ReceiveAsync.
    public sealed class JournalClientConnection
    {
        private readonly object sync = new object();
        private readonly JournalRequestClient requests;
        private readonly JournalTransferReceiver transfer;
        private readonly JournalTransferReceiver entriesTransfer;
        private readonly JournalReplica replica;
        private readonly ReplicationClient replication;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Task application = Task.CompletedTask;
        private Task? closing;
        private Task? suspending;
        private Guid loginRequest;
        private Guid logoutRequest;
        private VaultIdentity? vault;
        private SessionAddress? address;
        private int pendingFrames, pendingBytes;
        private bool loginQueued;
        private volatile bool stopped;
        private volatile bool processingStarted;
        private Exception? failure;
        public Exception? Failure { get { lock (sync) return failure; } }
        public bool AccessEnabled => !stopped && replication.AccessEnabled;
        public long Sequence => replication.Sequence;
        public VaultState? Current => replica.Current;
        public bool ProcessingStopped => stopped || replication.Stopped;
        // Replication starts stopped while login is in flight. Owners must not
        // interpret that initial state as a terminal session/disconnect.
        public bool LoginPending => !stopped && !processingStarted;
        public bool ProcessingEnded => stopped || (processingStarted && replication.Stopped);
        public bool RequiresSuspension => replica.NeedsSuspension;
        public SessionAddress? Address { get { lock (sync) return address; } }

        public JournalClientConnection(ICharacterInventory inventory, Func<Func<ReplicaResult>, Task<ReplicaResult>> dispatch,
            JournalSendQueue outgoing, IReplicationClock clock, JournalTransferBudget budget, Action<VaultState, JournalEntry?>? changed = null)
        {
            requests = new JournalRequestClient(outgoing.TryEnqueue, clock);
            transfer = new JournalTransferReceiver(budget, clock);
            entriesTransfer = new JournalTransferReceiver(budget, clock, JournalWire.MaximumLogicalBody);
            replica = new JournalReplica(inventory, changed);
            replication = new ReplicationClient(replica, dispatch,
                async (a, sequence) => Flat(await requests.RequestAsync(new JournalFrame(JournalMessage.Acknowledge, Guid.NewGuid(),
                    JournalWire.EncodePosition(a, sequence)), lifetime.Token).ConfigureAwait(false)),
                async a => Flat(await SuspendAsync(a).ConfigureAwait(false)));
        }
        private static SessionReply Flat(JournalReply reply) => new SessionReply(reply.Status, phase: reply.Phase);
        public Task<JournalReply> LoginAsync(VaultIdentity identity, Guid request)
        {
            lock (sync)
            {
                if (stopped || closing != null) throw new ObjectDisposedException(nameof(JournalClientConnection));
                if (loginRequest != Guid.Empty && (loginRequest != request || !vault!.Matches(identity)))
                    throw new InvalidOperationException("Connection already has a login request");
                vault = identity; loginRequest = request;
                return requests.RequestAsync(new JournalFrame(JournalMessage.Login, request, JournalWire.EncodeLogin(identity)), lifetime.Token);
            }
        }
        public Task<JournalReply> ExecuteAsync(ItemCommand command)
        {
            lock (sync)
            {
                if (!AccessEnabled || address == null || !JournalChecksum.SameAddress(address, command.Address))
                    throw new InvalidOperationException("Kist session does not permit requests");
                return requests.ExecuteAsync(command, lifetime.Token);
            }
        }
        public Task<JournalReply> LogoutAsync(Guid request)
        {
            lock (sync)
            {
                if (closing != null || failure != null || address == null || (stopped && logoutRequest != request))
                    throw new InvalidOperationException("No current Kist session");
                if (logoutRequest != Guid.Empty && logoutRequest != request) throw new InvalidOperationException("Logout request already exists");
                logoutRequest = request;
                replication.StopRequests();
                return requests.RequestAsync(new JournalFrame(JournalMessage.Logout, request, JournalWire.EncodePosition(address, 0)), lifetime.Token);
            }
        }
        private Task<JournalReply> SuspendAsync(SessionAddress a)
            => requests.RequestAsync(new JournalFrame(JournalMessage.Suspend, Guid.NewGuid(), JournalWire.EncodePosition(a, 0)), lifetime.Token);

        // The native adapter has already verified the actual current server RPC.
        // Its serial worker owns transfer reassembly. The application queue is
        // bounded separately, so a slow Unity dispatcher cannot grow it forever.
        public Task ReceiveAsync(JournalFrame frame)
        {
            lock (sync)
            {
                if (closing != null) return Task.CompletedTask;
                try
                {
                    if (frame.Message == JournalMessage.Reply)
                    {
                        var reply = JournalReply.Decode(frame.CopyBody());
                        if (reply.Request == JournalMessage.Login && frame.Correlation == loginRequest && reply.Transfer != Guid.Empty)
                        {
                            if (reply.Address == null || vault == null || !vault.Contains(reply.Address) || reply.Transfer != loginRequest)
                                throw new InvalidDataException("Login reply identity mismatch");
                            if (address != null && !JournalChecksum.SameAddress(address, reply.Address)) throw new InvalidDataException("Login session changed");
                            address = reply.Address;
                        }
                        requests.AcceptReply(frame);
                        if (reply.Phase == SessionPhase.Closed && address != null && reply.Address != null && JournalChecksum.SameAddress(address, reply.Address))
                        { stopped = true; replication.StopProcessing(); }
                    }
                    else if (!stopped && frame.Message == JournalMessage.Transfer)
                    {
                        if (frame.Correlation != loginRequest || address == null) throw new InvalidDataException("Unrequested login transfer");
                        var bytes = transfer.Accept(frame);
                        if (bytes != null)
                        {
                            if (loginQueued) throw new InvalidDataException("Repeated login image");
                            var login = JournalWire.DecodeLoginImage(bytes);
                            if (login.Session == null || !JournalChecksum.SameAddress(address, login.Session.Address) || login.Session.LoginRequest != loginRequest)
                                throw new InvalidDataException("Login image identity mismatch");
                            loginQueued = true;
                            Queue(async () =>
                            {
                                if (!await replication.BeginAsync(login).ConfigureAwait(false) || stopped) { replication.StopProcessing(); return; }
                                processingStarted = true;
                                for (int i = 0; i < login.Entries.Count && !stopped && !replication.Stopped; i += JournalWire.MaximumEntries)
                                    await replication.ProcessBatchAsync(login.Session.Address, login.Entries.Skip(i).Take(JournalWire.MaximumEntries).ToArray()).ConfigureAwait(false);
                            }, 0, true);
                        }
                    }
                    else if (!stopped && frame.Message == JournalMessage.Entries)
                    {
                        var delivery = JournalWire.DecodeEntries(frame.CopyBody());
                        if (address == null || !loginQueued) throw new InvalidDataException("Delivery before login image");
                        if (!JournalChecksum.SameAddress(address, delivery.Address)) return Task.CompletedTask;
                        if (frame.Correlation != delivery.Address.Session) throw new InvalidDataException("Delivery correlation mismatch");
                        Queue(async () => { await replication.ProcessBatchAsync(delivery.Address, delivery.Entries).ConfigureAwait(false); }, frame.CopyBody().Length, false);
                    }
                    else if (!stopped && frame.Message == JournalMessage.EntriesChunk)
                    {
                        if (address == null || !loginQueued || frame.Correlation != address.Session) throw new InvalidDataException("Unbound entry transfer");
                        var bytes = entriesTransfer.Accept(frame);
                        if (bytes != null) return ReceiveAsync(new JournalFrame(JournalMessage.Entries, frame.Correlation, bytes));
                    }
                    else if (frame.Message != JournalMessage.Entries && frame.Message != JournalMessage.Transfer && frame.Message != JournalMessage.EntriesChunk)
                        throw new InvalidDataException("Invalid server message direction");
                }
                catch (Exception error) { Fail(error); }
                return Task.CompletedTask;
            }
        }
        private void Queue(Func<Task> action, int bytes, bool login)
        {
            if ((!login && (pendingFrames >= 16 || bytes > 2 * JournalWire.MaximumLogicalBody - pendingBytes))) throw new IOException("Client application queue full");
            pendingFrames++; pendingBytes += bytes;
            var previous = application;
            application = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    if (!stopped) await action().ConfigureAwait(false);
                }
                catch (Exception error) { Fail(error); }
                finally { lock (sync) { pendingFrames--; pendingBytes -= bytes; } }
            });
        }
        private void Fail(Exception error)
        {
            SessionAddress? current;
            lock (sync)
            {
                if (failure != null || closing != null) return;
                failure = error; stopped = true; replication.StopProcessing(); current = address;
            }
            if (current != null) _ = RequestSuspensionAsync(current);
        }
        private async Task RequestSuspensionAsync(SessionAddress current)
        {
            try { await SuspendAsync(current).ConfigureAwait(false); }
            catch { /* Oldest acknowledgment deadline is the fallback. */ }
        }
        public Task SuspendAndCloseAsync()
        {
            lock (sync)
            {
                if (closing != null) return closing;
                if (suspending != null) return suspending;
                stopped = true; replication.StopProcessing();
                var current = address;
                suspending = Task.Run(async () =>
                {
                    if (current != null) await RequestSuspensionAsync(current).ConfigureAwait(false);
                    await CloseAsync().ConfigureAwait(false);
                });
                return suspending;
            }
        }
        public Task CloseAsync()
        {
            lock (sync)
            {
                if (closing != null) return closing;
                stopped = true; replication.StopProcessing(); lifetime.Cancel(); requests.Dispose(); transfer.Dispose(); entriesTransfer.Dispose();
                var pending = application;
                closing = Task.Run(async () => { await pending.ConfigureAwait(false); lifetime.Dispose(); });
                return closing;
            }
        }
    }
}
