using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThorsKist.Sync
{
    // One connection to one vault, after authentication/catalog/range checks by
    // the game adapter. Handler and delivery pumps share only this peer's gate.
    // Neither performs Unity work or waits for a network recipient.
    public sealed class JournalServerConnection
    {
        private readonly ReplicationServer server;
        private readonly JournalPeerIdentity peer;
        private readonly JournalPeerBinding binding;
        private readonly JournalSendQueue outgoing;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly JournalTransferBudget budget;
        private readonly JournalTransferReceiver commands;
        private JournalTransfer? transfer;
        private int transferIndex, transferBytes;
        private Guid loginRequest;
        private long transferThrough;
        private SessionAddress? boundAddress;
        private long sentThrough;
        private byte[]? pending;
        private long pendingThrough;
        private bool closed;

        public JournalServerConnection(ReplicationServer server, JournalPeerIdentity peer,
            JournalPeerBinding binding, JournalSendQueue outgoing, JournalTransferBudget budget, IReplicationClock? clock = null)
        {
            this.server = server; this.peer = peer; this.binding = binding; this.outgoing = outgoing; this.budget = budget;
            commands = new JournalTransferReceiver(budget, clock ?? new SystemReplicationClock(), JournalWire.MaximumLogicalBody);
        }

        public Task ReceiveAsync(JournalFrame frame, CancellationToken token = default) => ReceiveAsync(frame, peer, token);
        public async Task ReceiveAsync(JournalFrame frame, JournalPeerIdentity current, CancellationToken token = default)
        {
            using var correlation = JournalDiagnostics.Correlate(frame.Correlation, 0, JournalDiagnostics.Operation(frame.Message));
            using (JournalDiagnostics.Measure("server.connection_gate.wait", JournalTimingKind.Wait, JournalTimingThread.Any))
                await gate.WaitAsync(token).ConfigureAwait(false);
            using var elapsed = JournalDiagnostics.Measure("server.request.elapsed", JournalTimingKind.Elapsed, JournalTimingThread.Any);
            try
            {
                if (closed) throw new ObjectDisposedException(nameof(JournalServerConnection));
                if (frame.Message == JournalMessage.CommandChunk)
                {
                    if (binding.Session == null || !binding.Authorizes(current, binding.Session)
                        || (current.Access & (JournalAccess.Deposit | JournalAccess.Take | JournalAccess.Craft | JournalAccess.Station)) == 0)
                        throw new InvalidDataException("Unbound or inaccessible command transfer");
                    var body = commands.Accept(frame); if (body == null) return;
                    frame = new JournalFrame(JournalMessage.Command, frame.Correlation, body);
                }
                if (frame.Message == JournalMessage.Logout && binding.Session == null && boundAddress != null && binding.IsCurrent(current))
                {
                    var retry = JournalWire.DecodePosition(frame.CopyBody());
                    var state = await server.ReadAsync().ConfigureAwait(false);
                    var completed = state.Replication.Sessions.FirstOrDefault(s => JournalChecksum.SameAddress(s.Address, boundAddress));
                    if (retry.Sequence != 0 || !JournalChecksum.SameAddress(retry.Address, boundAddress)
                        || completed?.Phase != SessionPhase.Closed || completed.LogoutRequest != frame.Correlation)
                        throw new InvalidDataException("Invalid closed-session logout retry");
                    SendReply(frame, await server.LogoutAsync(boundAddress, frame.Correlation).ConfigureAwait(false));
                    return;
                }
                JournalRequest request;
                using (JournalDiagnostics.Measure("server.request.decode")) request = JournalRequest.Decode(frame, current, binding);
                using var requestContext = JournalDiagnostics.Correlate(frame.Correlation, request.Sequence, JournalDiagnostics.Operation(frame.Message));
                SessionReply reply;
                switch (request.Message)
                {
                    case JournalMessage.Login:
                        reply = await server.LoginAsync(peer.Account, request.RequestId).ConfigureAwait(false);
                        if (reply.Status == SessionStatus.Ok && reply.Session != null && reply.Session.Phase != SessionPhase.Closed)
                        {
                            // Timeout closure may have happened in the periodic
                            // writer tick while this game connection stayed live.
                            var old = binding.Session;
                            if (old != null && !JournalChecksum.SameAddress(old, reply.Session.Address))
                            {
                                var state = await server.ReadAsync().ConfigureAwait(false);
                                if (state.Replication.Sessions.Any(s => JournalChecksum.SameAddress(s.Address, old) && s.Phase == SessionPhase.Closed))
                                    binding.ReleaseSession(peer, old);
                            }
                            if (!binding.Bind(peer, reply.Session.Address))
                            {
                                await server.ExcludeAsync(reply.Session.Address, false).ConfigureAwait(false);
                                throw new InvalidDataException("Login changed connection session");
                            }
                            boundAddress = reply.Session.Address;
                            if (loginRequest != request.RequestId)
                            {
                                // Encoding failures exclude the newly committed session in the
                                // caller's connection cleanup; protection must never be orphaned.
                                ReleaseTransfer(); pending = null;
                                // Reserve the maximum BEFORE encoding. Concurrent
                                // logins cannot each allocate 64 MiB and only then
                                // discover that their shared budget is exhausted.
                                if (!budget.Reserve(JournalWire.MaximumTransferBytes)) throw new InvalidDataException("Outbound transfer budget exhausted");
                                transferBytes = JournalWire.MaximumTransferBytes;
                                try
                                {
                                    var image = JournalWire.EncodeLoginImage(reply);
                                    transfer = new JournalTransfer(request.RequestId, image);
                                    budget.Release(transferBytes - image.Length); transferBytes = image.Length;
                                }
                                catch { ReleaseTransfer(); throw; }
                                    loginRequest = request.RequestId; transferIndex = 0; sentThrough = reply.Session.LoginMarker; transferThrough = sentThrough;
                            }
                        }
                        break;
                    case JournalMessage.Command:
                        using (JournalDiagnostics.Measure("server.command.validate_wire")) JournalDelivery.ValidateCommand(request.Command!);
                        reply = await server.ExecuteAsync(request.Command!).ConfigureAwait(false);
                        break;
                    case JournalMessage.Acknowledge:
                        reply = await server.AcknowledgeAsync(request.Address!, request.Sequence).ConfigureAwait(false);
                        break;
                    case JournalMessage.Logout:
                        reply = await server.LogoutAsync(request.Address!, request.RequestId).ConfigureAwait(false);
                        break;
                    case JournalMessage.Suspend:
                        reply = await server.ExcludeAsync(request.Address!, true).ConfigureAwait(false);
                        break;
                    default: throw new InvalidDataException("Invalid request direction");
                }
                var transferId = request.Message == JournalMessage.Login && reply.Session?.Phase != SessionPhase.Closed
                    && reply.Status == SessionStatus.Ok ? loginRequest : Guid.Empty;
                // Do not wait for queue space while holding the writer. A peer that
                // cannot receive control replies is excluded by connection cleanup.
                SendReply(frame, reply, transferId);
                if (reply.Session?.Phase == SessionPhase.Closed && boundAddress != null
                    && JournalChecksum.SameAddress(boundAddress, reply.Session.Address))
                {
                    binding.ReleaseSession(peer, reply.Session.Address);
                    ReleaseTransfer(); pending = null;
                }
            }
            finally { gate.Release(); }
        }

        private void SendReply(JournalFrame request, SessionReply reply, Guid transferId = default)
        {
            using var encoding = JournalDiagnostics.Measure("server.reply.encode");
            JournalDiagnostics.Mark("server.reply.ready", request.Correlation, reply.Command?.Result?.Entry.Sequence ?? reply.Session?.Acknowledged ?? 0);
            var response = new JournalFrame(JournalMessage.Reply, request.Correlation, JournalReply.From(request.Message, reply, transferId).Encode());
            if (!outgoing.TryEnqueue(JournalWire.EncodeFrame(response))) throw new IOException("Journal reply queue full");
        }

        // Called by an independent async pump per connection. At most one frame
        // per invocation; retain it across backpressure without re-encoding or
        // resetting the persisted oldest-entry deadline.
        public async Task PumpAsync(CancellationToken token = default)
        {
            using var correlation = JournalDiagnostics.Correlate(Guid.Empty, sentThrough, "delivery");
            using (JournalDiagnostics.Measure("server.delivery_gate.wait", JournalTimingKind.Wait, JournalTimingThread.Any))
                await gate.WaitAsync(token).ConfigureAwait(false);
            using var elapsed = JournalDiagnostics.Measure("server.delivery.elapsed", JournalTimingKind.Elapsed, JournalTimingThread.Any);
            try
            {
                var address = binding.Session;
                if (commands.Expire()) throw new InvalidDataException("Command transfer deadline expired");
                if (closed || address == null || !binding.Authorizes(peer, address)) return;
                var state = await server.ReadAsync().ConfigureAwait(false);
                var session = state.Replication.Sessions.FirstOrDefault(s => JournalChecksum.SameAddress(s.Address, address));
                if (session == null || session.Phase == SessionPhase.Closed)
                {
                    binding.ReleaseSession(peer, address); ReleaseTransfer(); pending = null;
                    if (session != null)
                        SendReply(new JournalFrame(JournalMessage.Login, session.LoginRequest, Array.Empty<byte>()), new SessionReply(SessionStatus.InvalidSession, session));
                    return;
                }
                if (transfer != null)
                {
                    // Reuse the single bounded pending-frame slot under backpressure.
                    pending ??= JournalWire.EncodeFrame(transfer.Frame(transferIndex));
                    if (!outgoing.TryEnqueue(pending)) return;
                    pending = null;
                    if (++transferIndex == transfer.FrameCount) { sentThrough = transferThrough; ReleaseTransfer(); }
                    return;
                }
                if (pending != null)
                {
                    if (outgoing.TryEnqueue(pending)) { sentThrough = pendingThrough; pending = null; }
                    return;
                }
                // One bounded batch in flight per peer. The client processes
                // each entry through a verified Unity effect and durable ack;
                // socket acceptance is not application consumption. Leave new
                // entries in the journal until this batch is acknowledged.
                if (sentThrough > session.Acknowledged) return;
                // Select a byte-bounded prefix before registering its delivery
                // deadlines. QueueDelivery runs again in writer order and may
                // observe exclusion/checkpointing in between these operations.
                var candidates = state.Journal.Where(e => e.Sequence > sentThrough
                    && (session.Phase != SessionPhase.LoggingOut || e.Sequence <= session.LogoutMarker)).Take(JournalWire.MaximumEntries).ToArray();
                if (candidates.Length == 0) return;
                (byte[] Body, int Count) prefix;
                bool large = false;
                using var deliveryContext = JournalDiagnostics.Correlate(candidates[0].RequestId, candidates[0].Sequence, "delivery");
                try { using (JournalDiagnostics.Measure("server.delivery.select_encode", count: candidates.Length)) prefix = JournalDelivery.EncodePrefix(session.Address, candidates); }
                catch (InvalidDataException)
                { prefix = (Array.Empty<byte>(), 1); large = true; }
                var delivery = await server.QueueDeliveryAsync(session.Address, sentThrough, prefix.Count).ConfigureAwait(false);
                if (delivery.Status != SessionStatus.Ok || delivery.Entries.Count == 0) return;
                if (large)
                {
                    if (!budget.Reserve(JournalWire.MaximumLogicalBody)) throw new InvalidDataException("Outbound transfer budget exhausted");
                    transferBytes = JournalWire.MaximumLogicalBody;
                    try
                    {
                        var image = JournalWire.EncodeEntries(session.Address, delivery.Entries, true);
                        transfer = new JournalTransfer(session.Address.Session, image, JournalMessage.EntriesChunk);
                        budget.Release(transferBytes - image.Length); transferBytes = image.Length;
                    }
                    catch { ReleaseTransfer(); throw; }
                    transferIndex = 0; transferThrough = delivery.Entries.Last().Sequence; return;
                }
                byte[] body;
                using (JournalDiagnostics.Measure("server.delivery.encode", count: delivery.Entries.Count)) body = JournalWire.EncodeEntries(session.Address, delivery.Entries);
                pendingThrough = delivery.Entries.Last().Sequence;
                pending = JournalWire.EncodeFrame(new JournalFrame(JournalMessage.Entries, session.Address.Session, body));
                if (outgoing.TryEnqueue(pending)) { sentThrough = pendingThrough; pending = null; }
            }
            finally { gate.Release(); }
        }

        private void ReleaseTransfer()
        {
            if (transferBytes != 0) budget.Release(transferBytes);
            transferBytes = 0; transfer = null;
        }
        public async Task CloseAsync(bool suspend = false)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (closed) return; closed = true;
                var address = boundAddress; binding.Close(); outgoing.Dispose(); pending = null; ReleaseTransfer(); commands.Dispose();
                if (address != null) await server.ExcludeAsync(address, suspend).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
    }
}
