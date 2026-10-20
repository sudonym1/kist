using System;
using System.Threading.Tasks;
using System.Linq;
using ThorsKist.Sync;

namespace ThorsKist
{
    // Installed by a selected journal world owner, never by RPC registration.
    // Every gameplay method stays on the owning game thread; only verified
    // entries can remove reserved items or add withdrawn items.
    internal sealed partial class JournalGameplaySession : IDisposable
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
        internal int Count(string name, int quality = -1) => Math.Max(0, Stock.MaterialCount(name, quality) - PendingPaymentCount(name, quality));
        internal ItemDrop.ItemData Peek(string name, int quality = -1) => Stock.Rows.FirstOrDefault(r => r.Item != null && r.Item.m_shared.m_name == name && (quality < 0 || r.Item.m_quality == quality))?.Item;
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
            if (entry != null && immediatePayments.Remove(entry.RequestId) && entry.Kind == JournalEntryKind.Rejected)
                Main.Log.LogWarning("Kist payment rejected after optimistic action; request=" + entry.RequestId);
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
            if (Stock.Drain() && ReferenceEquals(Current, this)) KistClient.OnStateChanged?.Invoke();
            if (Failure != null || Client.Failure != null || Client.RequiresSuspension || !character.IsValid)
            { Failure = Failure ?? Client.Failure ?? new InvalidOperationException("Character inventory invalidated"); Dispose(); }
        }
        internal bool Deposit(Inventory from, ItemDrop.ItemData item, int quantity)
        {
            if (!AccessEnabled || !ReferenceEquals(from, inventory) || item == null || quantity <= 0 || quantity > item.m_stack) return false;
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
            if (!AccessEnabled || !Stock.Find(stackId, out var stack) || quantity <= 0 || quantity > stack.Item.Quantity - PendingPaymentStack(stackId)) return false;
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
