using System;
using System.Collections.Generic;
using System.Linq;

namespace ThorsKist.Sync
{
    // Prototype intentionally locks the entire source object, even for a partial
    // reservation. Any mutation invalidates it. Slots/names never identify items.
    public interface IReservationInventory<T> where T : class
    {
        bool ContainsReference(T stack);
        int Quantity(T stack);
        byte[] Capture(T stack);
        bool RemoveExact(T stack, int quantity);
    }
    public sealed class DepositReservations<T> where T : class
    {
        private sealed class Reservation
        {
            public T Stack = null!;
            public int Quantity;
            public byte[] Snapshot = null!;
        }
        private readonly IReservationInventory<T> inventory;
        private readonly Dictionary<Guid, Reservation> pending = new Dictionary<Guid, Reservation>();
        private readonly Dictionary<Guid, (Guid RequestId, int Quantity)> applied = new Dictionary<Guid, (Guid, int)>();
        private readonly HashSet<Guid> completedRequests = new HashSet<Guid>();
        public DepositReservations(IReservationInventory<T> inventory) { this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory)); }
        public bool IsBlocked(T stack) => pending.Values.Any(r => ReferenceEquals(r.Stack, stack));
        public bool TryReserve(Guid requestId, T stack, int quantity)
        {
            if (requestId == Guid.Empty || stack == null || quantity <= 0) return false;
            if (completedRequests.Contains(requestId)) return false;
            if (pending.TryGetValue(requestId, out var old))
                return ReferenceEquals(old.Stack, stack) && old.Quantity == quantity && Matches(old);
            if (IsBlocked(stack) || !inventory.ContainsReference(stack) || inventory.Quantity(stack) < quantity) return false;
            pending.Add(requestId, new Reservation { Stack = stack, Quantity = quantity, Snapshot = (byte[])inventory.Capture(stack).Clone() });
            return true;
        }
        private bool Matches(Reservation reservation) => inventory.ContainsReference(reservation.Stack)
            && inventory.Quantity(reservation.Stack) >= reservation.Quantity
            && inventory.Capture(reservation.Stack).SequenceEqual(reservation.Snapshot);
        public InventoryResult ApplyVerifiedRemoval(Guid requestId, Guid effectId, int quantity)
        {
            if (effectId == Guid.Empty) return InventoryResult.Failed;
            if (applied.TryGetValue(effectId, out var completed))
                return completed.RequestId == requestId && completed.Quantity == quantity
                    ? InventoryResult.AlreadyApplied : InventoryResult.Failed;
            if (!pending.TryGetValue(requestId, out var reservation)) return InventoryResult.MissingReservation;
            if (quantity != reservation.Quantity || !Matches(reservation)) return InventoryResult.InventoryChanged;
            int before = inventory.Quantity(reservation.Stack);
            if (!inventory.RemoveExact(reservation.Stack, quantity)) return InventoryResult.Failed;
            int after = inventory.ContainsReference(reservation.Stack) ? inventory.Quantity(reservation.Stack) : 0;
            if (before - after != quantity) return InventoryResult.Failed;
            applied.Add(effectId, (requestId, quantity)); completedRequests.Add(requestId); pending.Remove(requestId);
            return InventoryResult.Applied;
        }
        public bool ReleaseRejected(Guid requestId) => pending.Remove(requestId);
        // Exclusion clears reservations without refund/removal; preserve known
        // effect identities for this process. No client files are written.
        public void ExcludeSession() => pending.Clear();
    }
}
