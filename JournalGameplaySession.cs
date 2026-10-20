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
            ContinueDeposit(entry);
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
            return SubmitDeposit(new DepositOperation { Source = item, Remaining = quantity });
        }
        internal bool Delete(Guid stackId)
        {
            if (!Stock.Find(stackId, out var stack)) return false;
            return Take(stackId, stack.Item.Quantity, discard: true);
        }

        internal bool Take(Guid stackId, int quantity, bool discard = false)
        {
            if (!AccessEnabled || !Stock.Find(stackId, out var stack) || quantity <= 0 || quantity > stack.Item.Quantity - PendingPaymentStack(stackId)) return false;
            if (discard && immediatePayments.Count >= 48) return false;
            var item = stack.Item;
            try
            {
                var request = Guid.NewGuid();
                using var diagnosticContext = JournalDiagnostics.Correlate(request, 0, "withdraw");
                using var submit = JournalDiagnostics.Measure("unity.withdraw.submit", JournalTimingKind.Work, JournalTimingThread.Main, quantity);
                JournalDiagnostics.Mark("client.withdraw.submitted", request, 0, quantity);
                var command = new ItemCommand(Client.Address, request, stackId, ItemCommandKind.Withdraw,
                    new ItemRecord(item.Prefab, quantity, item.FormatVersion, item.CopyPayload()));
                // Use ordinary withdrawal admission, replication and settlement.
                // As with crafting payments, consume the verified delivery without
                // materializing it in the character inventory.
                if (discard)
                {
                    var display = Stock.Rows.FirstOrDefault(row => row.Stack.Id == stackId)?.Item;
                    immediatePayments.Add(request, new ImmediatePayment { Command = command,
                        Name = display?.m_shared.m_name ?? item.Prefab, Quality = display?.m_quality ?? -1 });
                    character.ExpectImmediatePayment(request, command.Item);
                }
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
