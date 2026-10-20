using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ThorsKist.Sync;

namespace ThorsKist
{
    // Session-owned, memory-only adapter. Do not attach to the legacy escrow
    // path: only JournalReplica may supply verified effects to Apply.
    internal sealed class JournalCharacterInventory : ICharacterInventory, IDisposable
    {
        private readonly Inventory inventory;
        private readonly DepositReservations<ItemDrop.ItemData> reservations;
        private readonly Dictionary<Guid, ItemDrop.ItemData> local = new Dictionary<Guid, ItemDrop.ItemData>();
        private readonly Dictionary<Guid, ItemRecord> pending = new Dictionary<Guid, ItemRecord>();
        private readonly Dictionary<Guid, ItemRecord> immediatePayments = new Dictionary<Guid, ItemRecord>();
        private readonly Dictionary<Guid, CharacterEffect> applied = new Dictionary<Guid, CharacterEffect>();
        private readonly HashSet<Guid> attempted = new HashSet<Guid>();
        private readonly Action<bool, bool> changed;
        private readonly Func<Inventory> currentInventory;
        private bool disposed;
        private bool invalidated;
        private int validationCursor;
        internal event Action Invalidated;
        internal bool IsValid => !disposed && !invalidated;

        public JournalCharacterInventory(Inventory inventory, Func<Inventory> currentInventory = null)
        {
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            this.currentInventory = currentInventory;
            var player = Player.m_localPlayer;
            if (this.currentInventory == null && player != null && ReferenceEquals(player.GetInventory(), inventory))
                this.currentInventory = () => ReferenceEquals(Player.m_localPlayer, player) && player != null ? player.GetInventory() : null;
            reservations = new DepositReservations<ItemDrop.ItemData>(new JournalInventoryPrototype(inventory));
            // Changed is private in the shipped assembly; bind once, never call
            // a publicized-only member reference (VerifyPatches checks these).
            var method = typeof(Inventory).GetMethod("Changed", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(bool), typeof(bool) }, null);
            changed = (Action<bool, bool>)Delegate.CreateDelegate(typeof(Action<bool, bool>), inventory,
                method ?? throw new MissingMethodException("Inventory.Changed(bool,bool)"));
            JournalReservationGuard.Register(inventory, this);
        }

        public Guid Identify(ItemDrop.ItemData stack)
        {
            if (disposed || !inventory.GetAllItems().Any(i => ReferenceEquals(i, stack))) return Guid.Empty;
            foreach (var pair in local) if (ReferenceEquals(pair.Value, stack)) return pair.Key;
            var id = Guid.NewGuid(); local.Add(id, stack); return id;
        }
        public bool IsBlocked(ItemDrop.ItemData stack) => reservations.IsBlocked(stack);
        public bool HasReservations => pending.Count != 0;
        public bool TryReserve(Guid requestId, Guid localStackId, int quantity)
        {
            if (!CheckCurrent() || !local.TryGetValue(localStackId, out var stack) || stack.m_shared == null || stack.m_equipped || stack.m_shared.m_questItem
                || (!pending.ContainsKey(requestId) && pending.Count >= 64)) return false;
            ItemRecord record;
            try { using (JournalDiagnostics.Measure("unity.reservation.capture", expected: JournalTimingThread.Main)) record = JournalItemCodec.Capture(stack, quantity); }
            catch { return false; }
            using (JournalDiagnostics.Measure("unity.reservation.snapshot", expected: JournalTimingThread.Main))
                if (!reservations.TryReserve(requestId, stack, quantity)) return false;
            if (!pending.ContainsKey(requestId)) pending.Add(requestId, record);
            return true;
        }
        public bool ReleaseRejectedReservation(Guid requestId)
        {
            immediatePayments.Remove(requestId);
            pending.Remove(requestId);
            return reservations.ReleaseRejected(requestId);
        }
        public InventoryResult Apply(CharacterEffect effect)
        {
            using var correlation = JournalDiagnostics.Correlate(effect.RequestId, JournalDiagnostics.Current.Sequence, "character_effect");
            using var timing = JournalDiagnostics.Measure("unity.inventory_effect.work", JournalTimingKind.Work, JournalTimingThread.Main, effect.Item.Quantity);
            if (disposed) return InventoryResult.Failed;
            if (applied.TryGetValue(effect.EffectId, out var old))
                return SameEffect(old, effect) ? InventoryResult.AlreadyApplied : InventoryResult.Failed;
            if (!CheckCurrent()) return InventoryResult.InventoryChanged;
            // A foreign patch or inventory callback can throw after mutating.
            // Do not repeat an uncertain effect in this process.
            if (!attempted.Add(effect.EffectId)) return InventoryResult.Failed;
            try
            {
                InventoryResult result;
                if (effect.Kind == CharacterEffectKind.RemoveReserved)
                {
                    if (!pending.TryGetValue(effect.RequestId, out var reserved)) return InventoryResult.MissingReservation;
                    if (!JournalItemCodec.Matches(reserved, effect.Item)) return InventoryResult.InventoryChanged;
                    using (JournalDiagnostics.Measure("unity.inventory.remove_reserved", expected: JournalTimingThread.Main))
                        result = reservations.ApplyVerifiedRemoval(effect.RequestId, effect.EffectId, effect.Item.Quantity);
                    if (result == InventoryResult.Applied || result == InventoryResult.AlreadyApplied) pending.Remove(effect.RequestId);
                }
                else if (immediatePayments.TryGetValue(effect.RequestId, out var spent))
                {
                    // Building, station insertion or explicit deletion consumes
                    // this verified withdrawal without delivering it;
                    // it must never materialize spendable items in the pack.
                    if (!JournalChecksum.SameItem(spent, effect.Item)) return InventoryResult.InventoryChanged;
                    immediatePayments.Remove(effect.RequestId);
                    result = InventoryResult.Applied;
                }
                else
                {
                    ItemDrop.ItemData item;
                    using (JournalDiagnostics.Measure("unity.inventory.restore", expected: JournalTimingThread.Main)) item = JournalItemCodec.Restore(effect.Item);
                    result = AddStacked(item, effect.Item);
                }
                // Changed callbacks can replace the inventory or trigger death
                // while the effect is executing. Recheck before authorizing ack.
                if (!CheckCurrent()) return InventoryResult.InventoryChanged;
                if (result == InventoryResult.Applied || result == InventoryResult.AlreadyApplied) applied.Add(effect.EffectId, effect);
                return result;
            }
            catch { return InventoryResult.Failed; }
        }
        private Vector2i EmptySlot()
        {
            for (int y = 0; y < inventory.GetHeight(); y++)
                for (int x = 0; x < inventory.GetWidth(); x++)
                    if (inventory.GetItemAt(x, y) == null) return new Vector2i(x, y);
            return new Vector2i(-1, -1);
        }
        private InventoryResult AddStacked(ItemDrop.ItemData incoming, ItemRecord record)
        {
            var merges = new List<(ItemDrop.ItemData Item, int Before, int Amount)>();
            int remaining = incoming.m_stack;
            Vector2i slot = new Vector2i(-1, -1);
            using (JournalDiagnostics.Measure("unity.inventory.stack_plan", expected: JournalTimingThread.Main))
            {
                foreach (var target in inventory.GetAllItems())
                {
                    if (remaining == 0) break;
                    if (target.m_dropPrefab == null || target.m_shared == null
                        || target.m_stack <= 0 || target.m_stack >= target.m_shared.m_maxStackSize || JournalReservationGuard.Blocks(target)) continue;
                    // Vanilla only matches name/quality/world level. Journal
                    // delivery must also preserve durability, variant, crafter,
                    // custom data and every other canonical persisted field.
                    ItemRecord candidate;
                    try { candidate = JournalItemCodec.Capture(target, 1); }
                    catch { continue; }
                    int room = JournalItemCodec.MergeRoom(candidate, record, target.m_shared.m_maxStackSize, target.m_stack);
                    if (room == 0) continue;
                    int amount = Math.Min(remaining, room);
                    merges.Add((target, target.m_stack, amount)); remaining -= amount;
                }
                if (remaining > 0)
                {
                    slot = EmptySlot();
                    if (slot.x < 0) return InventoryResult.NoCapacity;
                }
            }
            // Capacity is established for the entire effect before mutating any
            // stack. Failure cannot fill part of a stack and return the rest.
            using (JournalDiagnostics.Measure("unity.inventory.add", expected: JournalTimingThread.Main))
            {
                foreach (var merge in merges) merge.Item.m_stack = merge.Before + merge.Amount;
                if (remaining > 0)
                {
                    incoming.m_stack = remaining; incoming.m_gridPos = slot;
                    inventory.GetAllItems().Add(incoming);
                }
            }
            using (JournalDiagnostics.Measure("unity.inventory.changed_callbacks", expected: JournalTimingThread.Main)) changed(false, false);
            using (JournalDiagnostics.Measure("unity.inventory.verify_added", expected: JournalTimingThread.Main))
            {
                foreach (var merge in merges)
                    if (!inventory.GetAllItems().Any(i => ReferenceEquals(i, merge.Item)) || merge.Item.m_stack != merge.Before + merge.Amount
                        || !JournalItemCodec.Matches(JournalItemCodec.Capture(merge.Item, merge.Item.m_stack), record, false))
                        return InventoryResult.Failed;
                if (remaining > 0 && (!inventory.GetAllItems().Any(i => ReferenceEquals(i, incoming)) || incoming.m_stack != remaining
                    || !JournalItemCodec.Matches(JournalItemCodec.Capture(incoming, remaining), record, false))) return InventoryResult.Failed;
            }
            return InventoryResult.Applied;
        }
        private static bool SameEffect(CharacterEffect a, CharacterEffect b)
            => a.OperationId == b.OperationId && a.RequestId == b.RequestId && a.Kind == b.Kind
                && a.Recipient.World == b.Recipient.World && a.Recipient.Vault == b.Recipient.Vault
                && a.Recipient.Epoch == b.Recipient.Epoch && a.Recipient.Player == b.Recipient.Player
                && a.Recipient.Session == b.Recipient.Session && JournalChecksum.SameItem(a.Item, b.Item);
        public void ExcludeSession()
        {
            reservations.ExcludeSession(); pending.Clear(); immediatePayments.Clear(); local.Clear();
        }
        internal void ExpectImmediatePayment(Guid request, ItemRecord item)
        {
            if (!IsValid || immediatePayments.Count >= 48) throw new InvalidOperationException("Too many pending immediate payments");
            immediatePayments.Add(request, item);
        }
        internal void ValidateNextReservation()
        {
            if (!CheckCurrent() || pending.Count == 0) return;
            if (validationCursor >= pending.Count) validationCursor = 0;
            var request = pending.Keys.ElementAt(validationCursor++);
            try { if (!reservations.IsIntact(request)) Invalidate(); }
            catch { Invalidate(); }
        }
        private bool CheckCurrent()
        {
            if (!IsValid) return false;
            try { if (currentInventory != null && !ReferenceEquals(currentInventory(), inventory)) Invalidate(); }
            catch { Invalidate(); }
            return IsValid;
        }
        internal void Invalidate()
        {
            if (!IsValid) return;
            invalidated = true; ExcludeSession();
            // Invalidation is latched before notifying the future session owner.
            // A throwing listener cannot permit subsequent character effects.
            try { Invalidated?.Invoke(); }
            catch (Exception error) { Main.Log?.LogError("Journal reservation invalidation listener failed: " + error); }
        }
        public void Dispose()
        {
            if (disposed) return;
            ExcludeSession(); disposed = true; JournalReservationGuard.Unregister(inventory, this);
        }
    }
}
