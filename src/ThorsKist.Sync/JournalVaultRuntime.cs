using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThorsKist.Sync
{
    // Worker-owned lifetime of one vault authority. The game adapter only queues
    // frames and drains network bytes; timers, commits, fan-out and settlement
    // continue independently of the Unity update loop and other recipients.
    public sealed class JournalVaultRuntime
    {
        private sealed class Connection
        {
            internal readonly JournalPeerBinding Binding;
            internal readonly JournalServerConnection Server;
            internal readonly CancellationTokenSource Cancellation;
            internal Task Completion = Task.CompletedTask;
            internal Connection(JournalPeerIdentity peer, JournalPeerBinding binding, JournalSendQueue outgoing,
                JournalVaultRuntime owner)
            {
                Binding = binding;
                Server = new JournalServerConnection(owner.server, peer, binding, outgoing, owner.transfers, owner.clock);
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.lifetime.Token);
            }
        }
        private readonly object sync = new object();
        private readonly Dictionary<JournalPeerBinding, Connection> connections = new Dictionary<JournalPeerBinding, Connection>();
        private readonly ReplicationServer server;
        private readonly VaultIdentity identity;
        private readonly IReplicationClock clock;
        private readonly JournalTransferBudget transfers;
        private readonly int maximumConnections;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Task timer;
        private Task? stopping;
        private Exception? failure;
        private Exception? peerFailure;
        public Exception? Failure { get { lock (sync) return failure; } }
        public Exception? LastPeerFailure { get { lock (sync) return peerFailure; } }
        public int ConnectionCount { get { lock (sync) return connections.Count; } }

        private JournalVaultRuntime(ReplicationServer server, VaultIdentity identity, IReplicationClock clock,
            JournalTransferBudget transfers, int maximumConnections)
        {
            this.server = server; this.identity = identity; this.clock = clock; this.transfers = transfers;
            this.maximumConnections = maximumConnections;
            timer = Task.Run(TimersAsync);
        }
        public static async Task<JournalVaultRuntime> OpenAsync(IJournalStorage storage, VaultState initial,
            IReplicationClock clock, int maximumDepth, JournalTransferBudget transfers, int maximumConnections = 64, Func<ItemRecord, int>? stackLimit = null)
        {
            if (maximumConnections <= 0) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
            if (clock == null || transfers == null) throw new ArgumentNullException();
            var server = await ReplicationServer.OpenAsync(storage, initial, clock, maximumDepth, stackLimit).ConfigureAwait(false);
            return new JournalVaultRuntime(server, initial.Identity, clock, transfers, maximumConnections);
        }
        private void RequireRunning()
        {
            if (failure != null) throw new InvalidOperationException("Journal authority failed closed", failure);
            if (stopping != null || lifetime.IsCancellationRequested) throw new ObjectDisposedException(nameof(JournalVaultRuntime));
        }
        public Task<JournalMachineState> ReadAsync()
        { lock (sync) { RequireRunning(); return server.ReadAsync(); } }

        public async Task ReceiveAsync(JournalPeerIdentity peer, JournalPeerBinding binding,
            JournalSendQueue outgoing, JournalFrame frame, CancellationToken token = default)
        {
            Connection connection;
            lock (sync)
            {
                RequireRunning();
                if (!identity.Matches(peer.Vault) || !binding.IsCurrent(peer)) throw new InvalidDataException("Stale or wrong-vault connection");
                if (!connections.TryGetValue(binding, out connection!))
                {
                    if (connections.Count >= maximumConnections) throw new IOException("Journal connection limit reached");
                    connection = new Connection(peer, binding, outgoing, this);
                    connections.Add(binding, connection);
                    var captured = connection;
                    connection.Completion = Task.Run(() => PumpAsync(captured));
                }
            }
            try { await connection.Server.ReceiveAsync(frame, peer, token).ConfigureAwait(false); }
            catch
            {
                Cancel(connection.Cancellation);
                // Observe cleanup before returning the peer fault. This waits on
                // this peer only; it never waits for a recipient acknowledgment.
                await connection.Completion.ConfigureAwait(false);
                throw;
            }
        }
        private async Task PumpAsync(Connection connection)
        {
            try
            {
                while (true)
                {
                    await clock.Delay(TimeSpan.FromMilliseconds(100), connection.Cancellation.Token).ConfigureAwait(false);
                    if (connection.Binding.IsClosed) break;
                    await connection.Server.PumpAsync(connection.Cancellation.Token).ConfigureAwait(false);
                    // The direct adapter closes its binding before notifying us.
                    // Stop an externally closed connection even before first login.
                    // Normal Kist logout releases only the session, not the peer.
                    if (connection.Cancellation.IsCancellationRequested) break;
                }
            }
            catch (OperationCanceledException) when (connection.Cancellation.IsCancellationRequested) { }
            catch (Exception error) { lock (sync) peerFailure = error; }
            finally
            {
                try { await connection.Server.CloseAsync().ConfigureAwait(false); }
                catch (Exception error) { Fail(error); }
                lock (sync) connections.Remove(connection.Binding);
                connection.Cancellation.Dispose();
            }
        }
        private async Task TimersAsync()
        {
            var checkpointDue = clock.Elapsed + TimeSpan.FromSeconds(1);
            try
            {
                while (true)
                {
                    await clock.Delay(TimeSpan.FromMilliseconds(100), lifetime.Token).ConfigureAwait(false);
                    var state = await server.ReadAsync().ConfigureAwait(false);
                    if (state.Replication.Sessions.Any(s => s.Phase != SessionPhase.Closed))
                        state = await server.TickAsync().ConfigureAwait(false);
                    if (clock.Elapsed >= checkpointDue)
                    {
                        if (state.Head > state.Checkpoint.Sequence) await server.CheckpointAsync().ConfigureAwait(false);
                        checkpointDue = clock.Elapsed + TimeSpan.FromSeconds(1);
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Fail(error); }
        }
        private void Fail(Exception error)
        {
            lock (sync) { if (failure == null) failure = error; }
            lifetime.Cancel();
        }
        private static void Cancel(CancellationTokenSource source)
        { try { source.Cancel(); } catch (ObjectDisposedException) { /* Concurrent cleanup already completed. */ } }
        // Safe to enqueue from peer-loss, hird departure or world teardown on
        // Unity's thread. The caller observes the returned task asynchronously.
        public Task DisconnectAsync(JournalPeerBinding binding)
        {
            lock (sync)
            {
                if (!connections.TryGetValue(binding, out var connection)) return Task.CompletedTask;
                Cancel(connection.Cancellation); return connection.Completion;
            }
        }
        public Task StopAsync()
        {
            lock (sync)
            {
                if (stopping != null) return stopping;
                lifetime.Cancel();
                var peers = connections.Values.ToArray();
                stopping = Task.Run(async () =>
                {
                    try
                    {
                        await timer.ConfigureAwait(false);
                        await Task.WhenAll(peers.Select(c => c.Completion)).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Dispose may wait for the writer; keep that wait off Unity.
                        try { await Task.Run(() => server.Dispose()).ConfigureAwait(false); }
                        finally { lifetime.Dispose(); }
                    }
                    if (Failure != null) throw new InvalidOperationException("Journal authority stopped after storage failure", Failure);
                });
                return stopping;
            }
        }
    }
}
