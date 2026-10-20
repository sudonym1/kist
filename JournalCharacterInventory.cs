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
            try { record = JournalItemCodec.Capture(stack, quantity); }
            catch { return false; }
            if (!reservations.TryReserve(requestId, stack, quantity)) return false;
            if (!pending.ContainsKey(requestId)) pending.Add(requestId, record);
            return true;
        }
        public bool ReleaseRejectedReservation(Guid requestId)
        {
            pending.Remove(requestId);
            return reservations.ReleaseRejected(requestId);
        }
        public InventoryResult Apply(CharacterEffect effect)
        {
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
                    if (!JournalChecksum.SameItem(reserved, effect.Item)) return InventoryResult.InventoryChanged;
                    result = reservations.ApplyVerifiedRemoval(effect.RequestId, effect.EffectId, effect.Item.Quantity);
                    if (result == InventoryResult.Applied || result == InventoryResult.AlreadyApplied) pending.Remove(effect.RequestId);
                }
                else
                {
                    var item = JournalItemCodec.Restore(effect.Item);
                    // Vanilla AddItem merges by name/quality/world level and can
                    // destroy variant, crafter and custom data. Install the exact
                    // stack in an empty slot, preserving every canonical field.
                    var slot = EmptySlot();
                    if (slot.x < 0) return InventoryResult.NoCapacity;
                    item.m_gridPos = slot;
                    inventory.GetAllItems().Add(item);
                    changed(false, false);
                    result = inventory.GetAllItems().Any(i => ReferenceEquals(i, item))
                        && JournalChecksum.SameItem(JournalItemCodec.Capture(item, item.m_stack), effect.Item)
                        ? InventoryResult.Applied : InventoryResult.Failed;
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
        private static bool SameEffect(CharacterEffect a, CharacterEffect b)
            => a.OperationId == b.OperationId && a.RequestId == b.RequestId && a.Kind == b.Kind
                && a.Recipient.World == b.Recipient.World && a.Recipient.Vault == b.Recipient.Vault
                && a.Recipient.Epoch == b.Recipient.Epoch && a.Recipient.Player == b.Recipient.Player
                && a.Recipient.Session == b.Recipient.Session && JournalChecksum.SameItem(a.Item, b.Item);
        public void ExcludeSession()
        {
            reservations.ExcludeSession(); pending.Clear(); local.Clear();
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
