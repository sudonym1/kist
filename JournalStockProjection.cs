using System;
using System.Collections.Generic;
using ThorsKist.Sync;

namespace ThorsKist
{
    // Display objects are never authority. Their reference maps to an immutable
    // stack UUID and original record, including overflow and unknown prefabs.
    internal sealed class JournalStockProjection
    {
        internal sealed class Row
        {
            internal StackRecord Stack;
            internal ItemDrop.ItemData Item;
        }
        private readonly Dictionary<Guid, Row> rows = new Dictionary<Guid, Row>();
        private readonly Dictionary<ItemDrop.ItemData, Guid> identities = new Dictionary<ItemDrop.ItemData, Guid>();
        private IEnumerator<StackRecord> loading;
        private readonly Queue<JournalEntry> deferred = new Queue<JournalEntry>();
        internal IEnumerable<Row> Rows => rows.Values;
        internal bool Ready => loading == null && deferred.Count == 0;
        internal int Count => rows.Count;
        internal Guid Identify(ItemDrop.ItemData item) => item != null && identities.TryGetValue(item, out var id) ? id : Guid.Empty;
        internal bool Find(Guid id, out StackRecord stack)
        {
            if (rows.TryGetValue(id, out var row)) { stack = row.Stack; return true; }
            stack = null; return false;
        }
        internal void Changed(VaultState state, JournalEntry entry)
        {
            if (entry == null)
            {
                loading?.Dispose(); rows.Clear(); identities.Clear(); deferred.Clear();
                loading = state.Stacks.GetEnumerator(); return;
            }
            if (loading != null || deferred.Count != 0)
            {
                if (deferred.Count >= 256) throw new InvalidOperationException("Journal display queue full");
                deferred.Enqueue(entry);
            }
            else Apply(entry);
        }
        // Called from Update; prefab restoration never runs for an entire vault
        // in the login installation action or for every heartbeat.
        internal bool Drain(int maximumStacks = 32)
        {
            bool changed = false;
            for (int i = 0; i < maximumStacks; i++)
            {
                if (loading != null)
                {
                    if (loading.MoveNext()) { Put(loading.Current); changed = true; continue; }
                    loading.Dispose(); loading = null; changed = true;
                }
                if (deferred.Count == 0) break;
                Apply(deferred.Dequeue()); changed = true;
            }
            return changed;
        }
        private void Apply(JournalEntry entry)
        {
            if (entry.DeletedStack.HasValue && rows.TryGetValue(entry.DeletedStack.Value, out var old))
            { if (old.Item != null) identities.Remove(old.Item); rows.Remove(entry.DeletedStack.Value); }
            if (entry.ChangedStack != null) Put(entry.ChangedStack);
        }
        private void Put(StackRecord stack)
        {
            if (rows.TryGetValue(stack.Id, out var old) && old.Item != null) identities.Remove(old.Item);
            ItemDrop.ItemData item = null;
            try { item = JournalItemCodec.Restore(stack.Item); }
            catch { /* Preserve unsupported records as visible opaque rows. */ }
            rows[stack.Id] = new Row { Stack = stack, Item = item };
            if (item != null) identities[item] = stack.Id;
        }
    }
}
