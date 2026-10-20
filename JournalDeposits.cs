using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ThorsKist.Sync;

namespace ThorsKist
{
    internal sealed partial class JournalGameplaySession
    {
        private sealed class DepositOperation
        {
            internal ItemDrop.ItemData Source;
            internal int Remaining;
            internal ItemCommand Command;
        }
        private readonly Dictionary<Guid, DepositOperation> deposits = new Dictionary<Guid, DepositOperation>();

        private bool SubmitDeposit(DepositOperation operation)
        {
            if (!AccessEnabled) return false;
            ItemRecord captured;
            try { captured = JournalItemCodec.Capture(operation.Source, operation.Remaining); }
            catch { return false; }
            int limit = operation.Source.m_shared.m_maxStackSize;
            // Include in-flight deposits in local capacity planning. The server
            // rechecks the limit in writer order for concurrent remote deposits.
            var candidates = Stock.Rows.Select(r => r.Stack).ToList();
            foreach (var pending in deposits.Values)
                if (!candidates.Any(s => s.Id == pending.Command.StackId))
                    candidates.Add(new StackRecord(pending.Command.StackId, -1,
                        new ItemRecord(pending.Command.Item.Prefab, 1, pending.Command.Item.FormatVersion, pending.Command.Item.CopyPayload())));
            Guid target = Guid.NewGuid();
            ItemRecord record = captured;
            long available = candidates.Count < Stock.Capacity ? limit : 0;
            bool selected = false;
            foreach (var candidate in candidates)
            {
                bool installed = Stock.Find(candidate.Id, out var actual);
                long held = deposits.Values.Where(d => d.Command.StackId == candidate.Id).Sum(d => (long)d.Command.Item.Quantity);
                int room = JournalItemCodec.MergeRoom(captured, candidate.Item, limit,
                    (installed ? actual.Item.Quantity : 0L) + held);
                if (room == 0) continue;
                available += room;
                if (selected) continue;
                target = candidate.Id;
                record = new ItemRecord(candidate.Item.Prefab, Math.Min(operation.Remaining, room),
                    candidate.Item.FormatVersion, candidate.Item.CopyPayload());
                selected = true;
            }
            // Like withdrawal, check the entire quantity before changing any
            // stack. The server still checks each chunk against concurrent peers.
            if (available < operation.Remaining)
            {
                KistClient.Message("The Kist does not have room for this stack.");
                return false;
            }
            var request = Guid.NewGuid();
            using var context = JournalDiagnostics.Correlate(request, 0, "deposit");
            using var timing = JournalDiagnostics.Measure("unity.deposit.submit", expected: JournalTimingThread.Main, count: record.Quantity);
            if (!character.TryReserve(request, character.Identify(operation.Source), record.Quantity)) return false;
            operation.Command = new ItemCommand(Client.Address, request, target, ItemCommandKind.Deposit, record);
            deposits.Add(request, operation);
            JournalDiagnostics.Mark("client.deposit.submitted", request, 0, record.Quantity);
            var command = operation.Command;
            Observe(Task.Run(() => Client.ExecuteAsync(command)));
            return true;
        }

        private void ContinueDeposit(JournalEntry entry)
        {
            if (!deposits.TryGetValue(entry.RequestId, out var operation)) return;
            deposits.Remove(entry.RequestId);
            if (entry.Kind != JournalEntryKind.Deposit)
            {
                KistClient.Message("The Kist could not accept this deposit. Remaining items stayed in your inventory.");
                return;
            }
            operation.Remaining -= operation.Command.Item.Quantity;
            // The verified effect released the old reservation. Reserve the next
            // exact chunk immediately, before another game action can spend it.
            if (operation.Remaining > 0 && !SubmitDeposit(operation))
                KistClient.Message("Deposit stopped. Remaining items stayed in your inventory.");
        }
    }
}
