using System;
using System.Threading.Tasks;
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
        private readonly JournalCharacterInventory character;
        private readonly JournalGameThread dispatch = new JournalGameThread(16);
        internal readonly JournalStockProjection Stock = new JournalStockProjection();
        internal readonly JournalClientConnection Client;
        private Task closing;
        private bool disposed;
        internal Exception Failure { get; private set; }
        internal bool AccessEnabled => !disposed && Failure == null && character.IsValid && Stock.Ready && Client.AccessEnabled;
        internal JournalGameplaySession(Inventory inventory, JournalSendQueue outgoing, IReplicationClock clock,
            JournalTransferBudget budget, Func<Inventory> currentInventory = null)
        {
            this.inventory = inventory;
            character = new JournalCharacterInventory(inventory, currentInventory);
            Client = new JournalClientConnection(character, dispatch.DispatchAsync, outgoing, clock, budget, Stock.Changed);
            character.Invalidated += Dispose;
        }
        internal void Tick()
        {
            if (disposed) return;
            dispatch.Drain(4);
            if (Stock.Drain() && ReferenceEquals(Current, this)) KistClient.OnStateChanged?.Invoke();
            if (Failure != null || Client.Failure != null || Client.RequiresSuspension || !character.IsValid)
            { Failure = Client.Failure ?? new InvalidOperationException("Character inventory invalidated"); Dispose(); }
        }
        internal bool Deposit(Inventory from, ItemDrop.ItemData item, int quantity)
        {
            if (!AccessEnabled || !ReferenceEquals(from, inventory) || item == null || quantity <= 0 || quantity > item.m_stack) return false;
            var request = Guid.NewGuid(); var local = character.Identify(item);
            if (!character.TryReserve(request, local, quantity)) return false;
            ItemRecord record;
            try { record = JournalItemCodec.Capture(item, quantity); }
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
            if (!AccessEnabled || !Stock.Find(stackId, out var stack) || quantity <= 0 || quantity > stack.Item.Quantity) return false;
            var item = stack.Item;
            try
            {
                var command = new ItemCommand(Client.Address, Guid.NewGuid(), stackId, ItemCommandKind.Withdraw,
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
