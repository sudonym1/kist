using System;
using System.Linq;
using ThorsKist.Sync;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private void Projection()
        {
            int start = checks;
            var v = new VaultIdentity("m7-view", "projection", Guid.NewGuid());
            var item = JournalItemCodec.Capture(Wood(2), 2);
            var records = Enumerable.Range(0, 64).Select(i => new StackRecord(Guid.NewGuid(), i, item)).ToList();
            var opaque = new StackRecord(Guid.NewGuid(), -1, new ItemRecord("m7-unavailable-item", 7, 110, new byte[] { 1 }));
            records.Add(opaque);
            var state = new VaultState(v, 64, records);
            var view = new JournalStockProjection(); view.Changed(state, null);
            Check(view.Count == 0 && !view.Ready, "Snapshot installation does not restore all prefabs in one Unity action");
            view.Drain(); Check(view.Count == 32 && !view.Ready, "Display restoration respects per-frame stack bound");
            view.Drain(); view.Drain();
            Check(view.Ready && view.Count == 65 && view.Rows.Single(r => r.Stack.Id == opaque.Id).Item == null,
                "Unknown overflow remains represented without Inventory.Load");
            Check(view.MaterialCount("$item_wood", -1) == 128 && view.MaterialCount("$item_wood", 1) == 128, "Material counts are cached across bounded snapshot installation");
            Check(!view.Drain(), "Idle drain does not request a UI refresh");
            var first = view.Rows.Single(r => r.Stack.Id == records[0].Id).Item;
            var unchanged = view.Rows.Single(r => r.Stack.Id == records[1].Id).Item;
            var replacement = new StackRecord(records[0].Id, 0, new ItemRecord(item.Prefab, 1, item.FormatVersion, item.CopyPayload()));
            view.Changed(state, new JournalEntry(v, 1, Guid.NewGuid(), Guid.NewGuid(), JournalEntryKind.Withdraw,
                null, replacement, null, null, JournalChecksum.Compute(state)));
            Check(view.Drain() && view.MaterialCount("$item_wood", -1) == 127, "Incremental quantity delta updates count and requests one UI refresh");
            Check(view.Identify(first) == Guid.Empty && view.Rows.Single(r => r.Stack.Id == replacement.Id).Item.m_stack == 1,
                "Changed row replaces only its display object and original quantity");
            Check(ReferenceEquals(unchanged, view.Rows.Single(r => r.Stack.Id == records[1].Id).Item), "Unchanged rows retain cached display objects");
            view.Changed(state, new JournalEntry(v, 2, Guid.NewGuid(), Guid.NewGuid(), JournalEntryKind.Withdraw,
                null, null, opaque.Id, null, JournalChecksum.Compute(state)));
            Check(view.Count == 64, "Verified deletion removes exact opaque UUID");
            Check(view.MaterialCount("$item_wood", -1) == 127 && view.Drain() && !view.Drain(), "Opaque deletion leaves material counts intact and coalesces UI dirtiness");
            Logger.LogInfo("M7 STOCK PROJECTION PASS " + (checks - start) + " assertions (bounded restoration, stable UUIDs, unknown overflow, incremental deltas)");
        }
    }
}
