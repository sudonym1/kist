using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using ThorsKist.Sync;

namespace ThorsKist
{
    // Installed by a selected journal world owner, never by RPC registration.
    // Every gameplay method stays on the owning game thread; only verified
    // entries can remove reserved items or add withdrawn items.
    internal sealed class JournalGameplaySession : IDisposable
    {
        internal static JournalGameplaySession Current;
        private readonly Inventory inventory;
        private readonly IReplicationClock clock;
        private readonly TimeSpan loginStarted;
        private readonly JournalCharacterInventory character;
        private readonly JournalGameThread dispatch = new JournalGameThread(16);
        internal readonly JournalStockProjection Stock = new JournalStockProjection();
        internal readonly JournalClientConnection Client;
        private Task closing;
        private Task procurement;
        private volatile bool procuring;
        private readonly ConcurrentQueue<Action> continuations = new ConcurrentQueue<Action>();
        internal bool Procuring => procuring;
        internal int Count(string name, int quality = -1) => Stock.MaterialCount(name, quality);
        internal ItemDrop.ItemData Peek(string name, int quality = -1) => Stock.Rows.FirstOrDefault(r => r.Item != null && r.Item.m_shared.m_name == name && (quality < 0 || r.Item.m_quality == quality))?.Item;
        // Vanilla runs only after every requested single-stack withdrawal has
        // been verified and delivered. A partial refusal leaves delivered items
        // in the character inventory and does not create a craft/station effect.
        internal bool Procure(IReadOnlyList<(string Name, int Quality, int Quantity)> needs, ItemCommandPurpose purpose, Action completed)
        {
            if (!AccessEnabled || Procuring || JournalReservationGuard.Busy(inventory)) return false;
            var commands = new List<ItemCommand>();
            foreach (var need in needs)
            {
                int remaining = need.Quantity;
                foreach (var row in Stock.Rows.Where(r => r.Item != null && r.Item.m_shared.m_name == need.Name && (need.Quality < 0 || r.Item.m_quality == need.Quality)))
                {
                    int take = Math.Min(remaining, row.Stack.Item.Quantity); if (take <= 0) break;
                    var item = row.Stack.Item;
                    commands.Add(new ItemCommand(Client.Address, Guid.NewGuid(), row.Stack.Id, ItemCommandKind.Withdraw,
                        new ItemRecord(item.Prefab, take, item.FormatVersion, item.CopyPayload()), purpose));
                    remaining -= take;
                }
                if (remaining != 0) return false;
            }
            procuring = true;
            procurement = Task.Run(async () =>
            {
                try
                {
                    foreach (var command in commands)
                    {
                        var reply = await Client.ExecuteAsync(command);
                        if (reply.Status != SessionStatus.Ok) throw new InvalidOperationException("Material session refused: " + reply.Status);
                        if (reply.Command != CommandStatus.Accepted)
                        { continuations.Enqueue(() => { procuring = false; KistClient.Message("Kist material withdrawal refused: " + reply.Command + ". Delivered materials remain in your inventory."); }); return; }
                        var deadline = System.Diagnostics.Stopwatch.StartNew();
                        while (!disposed && Failure == null && Client.Failure == null && Client.Sequence < reply.Sequence)
                        { if (deadline.Elapsed > TimeSpan.FromSeconds(31)) throw new TimeoutException("Material delivery was not verified"); await Task.Delay(10); }
                        if (disposed || Failure != null || Client.Failure != null) return;
                    }
                    continuations.Enqueue(() => { procuring = false; completed(); });
                }
                catch (Exception error) { Failure = error; procuring = false; }
            });
            return true;
        }
        private bool disposed;
        private bool loggingOut;
        internal bool LoggingOut => loggingOut;
        internal Exception Failure { get; private set; }
        internal bool AccessEnabled => !disposed && !loggingOut && Failure == null && character.IsValid && Stock.Ready && Client.AccessEnabled;
        internal JournalGameplaySession(Inventory inventory, JournalSendQueue outgoing, IReplicationClock clock,
            JournalTransferBudget budget, Func<Inventory> currentInventory = null)
        {
            this.inventory = inventory; this.clock = clock; loginStarted = clock.Elapsed;
            character = new JournalCharacterInventory(inventory, currentInventory);
            Client = new JournalClientConnection(character, dispatch.DispatchAsync, outgoing, clock, budget, Changed);
            character.Invalidated += Dispose;
        }
        private void Changed(VaultState state, JournalEntry entry)
        {
            if (entry == null)
            {
                // Snapshot verification runs on a worker. Enqueue installation
                // ahead of its entry effects on the same ordered Unity queue.
                var install = dispatch.DispatchAsync(() => { Stock.Changed(state, null); return ReplicaResult.Applied; });
                _ = install.ContinueWith(t => { if (t.IsFaulted) Failure = t.Exception.GetBaseException(); }, TaskScheduler.Default);
                return;
            }
            Stock.Changed(state, entry);
            if (entry != null && (entry.Kind == JournalEntryKind.Deposit || entry.Kind == JournalEntryKind.Withdraw || entry.Kind == JournalEntryKind.Rejected))
                Main.Log.LogInfo("KIST JOURNAL applied sequence=" + entry.Sequence + " kind=" + entry.Kind + " stack=" + (entry.ChangedStack?.Id.ToString() ?? entry.DeletedStack?.ToString() ?? "none") + "; authority=SQLite/journal");
        }
        internal Task<JournalReply> Logout() { loggingOut = true; return Task.Run(() => Client.LogoutAsync(Guid.NewGuid())); }
        internal void Login(VaultIdentity identity) { Observe(Task.Run(() => Client.LoginAsync(identity, Guid.NewGuid()))); }
        internal void Tick()
        {
            if (disposed) return;
            if (Client.LoginPending && clock.Elapsed - loginStarted >= TimeSpan.FromSeconds(31))
                Failure = new TimeoutException("Journal login did not complete within 31 seconds");
            dispatch.Drain(4);
            if (continuations.TryDequeue(out var continuation) && AccessEnabled) continuation();
            if (Stock.Drain() && ReferenceEquals(Current, this)) KistClient.OnStateChanged?.Invoke();
            if (Failure != null || Client.Failure != null || Client.RequiresSuspension || !character.IsValid)
            { Failure = Failure ?? Client.Failure ?? new InvalidOperationException("Character inventory invalidated"); Dispose(); }
        }
        internal bool Deposit(Inventory from, ItemDrop.ItemData item, int quantity)
        {
            if (!AccessEnabled || Procuring || !ReferenceEquals(from, inventory) || item == null || quantity <= 0 || quantity > item.m_stack) return false;
            var request = Guid.NewGuid();
            using var diagnosticContext = JournalDiagnostics.Correlate(request, 0, "deposit");
            using var submit = JournalDiagnostics.Measure("unity.deposit.submit", JournalTimingKind.Work, JournalTimingThread.Main, quantity);
            JournalDiagnostics.Mark("client.deposit.submitted", request, 0, quantity);
            Guid local;
            using (JournalDiagnostics.Measure("unity.deposit.identify", expected: JournalTimingThread.Main)) local = character.Identify(item);
            using (JournalDiagnostics.Measure("unity.deposit.reserve", expected: JournalTimingThread.Main))
                if (!character.TryReserve(request, local, quantity)) return false;
            ItemRecord record;
            try { using (JournalDiagnostics.Measure("unity.deposit.capture", expected: JournalTimingThread.Main)) record = JournalItemCodec.Capture(item, quantity); }
            catch { character.ReleaseRejectedReservation(request); return false; }
            // Request rejection/failure is not authority to touch the character.
            // Committed rejection entries release reservations; uncertain sends
            // exclude this session and let the server settle accepted effects.
            try
            {
                var command = new ItemCommand(Client.Address, request, Guid.NewGuid(), ItemCommandKind.Deposit, record);
                Observe(Task.Run(() => Client.ExecuteAsync(command)));
            }
            catch (Exception error) { Failure = error; Dispose(); return false; }
            return true;
        }
        internal bool Take(Guid stackId, int quantity)
        {
            if (!AccessEnabled || Procuring || !Stock.Find(stackId, out var stack) || quantity <= 0 || quantity > stack.Item.Quantity) return false;
            var item = stack.Item;
            try
            {
                var request = Guid.NewGuid();
                using var diagnosticContext = JournalDiagnostics.Correlate(request, 0, "withdraw");
                using var submit = JournalDiagnostics.Measure("unity.withdraw.submit", JournalTimingKind.Work, JournalTimingThread.Main, quantity);
                JournalDiagnostics.Mark("client.withdraw.submitted", request, 0, quantity);
                var command = new ItemCommand(Client.Address, request, stackId, ItemCommandKind.Withdraw,
                    new ItemRecord(item.Prefab, quantity, item.FormatVersion, item.CopyPayload()));
                Observe(Task.Run(() => Client.ExecuteAsync(command)));
            }
            catch (Exception error) { Failure = error; Dispose(); return false; }
            return true;
        }
        private void Observe(Task<JournalReply> request)
        {
            _ = request.ContinueWith(t =>
            {
                if (t.IsFaulted) Failure = t.Exception.GetBaseException();
                else if (t.IsCanceled) Failure = new OperationCanceledException("Journal request canceled");
                else if (t.Result.Status != SessionStatus.Ok) Failure = new InvalidOperationException("Journal request refused: " + t.Result.Status);
            }, TaskScheduler.Default);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            // Stop before canceling queued Unity effects. Reservations are
            // released locally without removal; server exclusion settles stock.
            closing = Client.SuspendAndCloseAsync(); dispatch.Dispose(); character.Dispose();
            _ = closing.ContinueWith(t => { if (t.IsFaulted) Failure = t.Exception.GetBaseException(); }, TaskScheduler.Default);
        }
    }
}
