using System;
using ThorsKist.Sync;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private void HammerPaymentEffects()
        {
            var inventory = new Inventory("hammer payment full pack", null, 1, 1);
            var wood = Wood(50); inventory.GetAllItems().Add(wood);
            using (var adapter = new JournalCharacterInventory(inventory))
            {
                int callbacks = 0; inventory.m_onChanged += () => callbacks++;
                var record = JournalItemCodec.Capture(Wood(2), 2); var request = Guid.NewGuid();
                adapter.ExpectBuildPayment(request, record);
                var effect = Effect(record, request, CharacterEffectKind.Add);
                Check(adapter.Apply(effect) == InventoryResult.Applied && wood.m_stack == 50 && callbacks == 0,
                    "Already placed hammer piece settles its payment even with full inventory, without adding materials");
                Check(adapter.Apply(effect) == InventoryResult.AlreadyApplied && wood.m_stack == 50 && callbacks == 0,
                    "Repeated hammer payment effect cannot charge or deliver twice");
                request = Guid.NewGuid(); adapter.ExpectBuildPayment(request, record);
                Check(adapter.Apply(Effect(JournalItemCodec.Capture(Wood(3), 3), request, CharacterEffectKind.Add)) == InventoryResult.InventoryChanged,
                    "Hammer payment must exactly match the expected canonical item and quantity");
                request = Guid.NewGuid(); adapter.ExpectBuildPayment(request, record); adapter.ReleaseRejectedReservation(request);
                Check(adapter.Apply(Effect(record, request, CharacterEffectKind.Add)) == InventoryResult.NoCapacity,
                    "Rejected hammer request releases its payment expectation rather than swallowing an unrelated addition");
            }
        }
    }
}
