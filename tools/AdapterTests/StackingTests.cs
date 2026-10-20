using System;
using System.Linq;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private static void ChangeStackMetadata(ItemDrop.ItemData item, string field)
        {
            if (field == "custom") item.m_customData["unique"] = "retain me";
            if (field == "variant") item.m_variant++;
            if (field == "crafter") item.m_crafterID++;
            if (field == "durability") item.m_durability += 1;
            if (field == "quality") item.m_quality++;
            if (field == "world") item.m_worldLevel++;
            if (field == "picked") item.m_pickedUp = !item.m_pickedUp;
            if (field == "cheated") item.m_cheated = !item.m_cheated;
        }

        private void WithdrawalStacking()
        {
            int start = checks;
            var named = JournalItemCodec.Capture(Wood(5), 5);
            var alias = new ItemRecord("hash:" + "Wood".GetStableHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture),
                named.Quantity, named.FormatVersion, named.CopyPayload());
            foreach (bool merge in new[] { false, true })
            {
                var restored = new Inventory("restored missing prefab", null, 1, 1);
                if (merge) restored.GetAllItems().Add(Wood(40));
                using var adapter = new JournalCharacterInventory(restored);
                var effect = Effect(alias, Guid.NewGuid(), CharacterEffectKind.Add);
                Check(adapter.Apply(effect) == InventoryResult.Applied && restored.GetAllItems().Sum(i => i.m_stack) == (merge ? 45 : 5),
                    "Recovered hash alias completes verified withdrawal, merge=" + merge);
                Check(adapter.Apply(effect) == InventoryResult.AlreadyApplied && restored.GetAllItems().Sum(i => i.m_stack) == (merge ? 45 : 5),
                    "Recovered alias retry cannot duplicate inventory items");
            }
            Check(!JournalItemCodec.Matches(named, new ItemRecord("hash:0", 5, named.FormatVersion, named.CopyPayload())),
                "Alias comparison still rejects a different prefab identity");
            var inventory = new Inventory("stacking full pack", null, 1, 1);
            var target = Wood(40); inventory.GetAllItems().Add(target);
            using (var adapter = new JournalCharacterInventory(inventory))
            {
                int callbacks = 0; inventory.m_onChanged += () => callbacks++;
                var effect = Effect(JournalItemCodec.Capture(Wood(5), 5), Guid.NewGuid(), CharacterEffectKind.Add);
                Check(adapter.Apply(effect) == InventoryResult.Applied && target.m_stack == 45 && inventory.GetAllItems().Count == 1 && callbacks == 1,
                    "Withdrawal fills matching stack even when every grid slot is occupied");
                Check(adapter.Apply(effect) == InventoryResult.AlreadyApplied && target.m_stack == 45 && callbacks == 1,
                    "Duplicate stacked withdrawal never adds items or calls Changed again");
            }
            inventory = new Inventory("stacking spill", null, 3, 1);
            var first = Wood(45); var second = Wood(47); second.m_gridPos = new Vector2i(1, 0);
            inventory.GetAllItems().Add(first); inventory.GetAllItems().Add(second);
            using (var adapter = new JournalCharacterInventory(inventory))
            {
                int callbacks = 0; inventory.m_onChanged += () => callbacks++;
                var effect = Effect(JournalItemCodec.Capture(Wood(10), 10), Guid.NewGuid(), CharacterEffectKind.Add);
                Check(adapter.Apply(effect) == InventoryResult.Applied && first.m_stack == 50 && second.m_stack == 50
                    && inventory.GetItemAt(2, 0).m_stack == 2 && inventory.GetAllItems().Sum(i => i.m_stack) == 102 && callbacks == 1,
                    "Withdrawal fills multiple partial stacks to their limits then puts only the remainder in a new slot");
            }
            inventory = new Inventory("stacking capacity preflight", null, 1, 1); target = Wood(47); inventory.GetAllItems().Add(target);
            using (var adapter = new JournalCharacterInventory(inventory))
            {
                int callbacks = 0; inventory.m_onChanged += () => callbacks++;
                var effect = Effect(JournalItemCodec.Capture(Wood(5), 5), Guid.NewGuid(), CharacterEffectKind.Add);
                Check(adapter.Apply(effect) == InventoryResult.NoCapacity && target.m_stack == 47 && callbacks == 0,
                    "Insufficient total room changes no quantities and emits no callback");
                Check(adapter.Apply(effect) == InventoryResult.Failed && target.m_stack == 47,
                    "Retry of refused effect cannot partially fill a stack");
            }
            foreach (string field in new[] { "custom", "variant", "crafter", "durability", "quality", "world", "picked", "cheated" })
            {
                inventory = new Inventory("stacking metadata " + field, null, 2, 1); target = Wood(40); inventory.GetAllItems().Add(target);
                ChangeStackMetadata(target, field);
                var original = JournalItemCodec.Capture(target, target.m_stack);
                using var adapter = new JournalCharacterInventory(inventory);
                var effect = Effect(JournalItemCodec.Capture(Wood(5), 5), Guid.NewGuid(), CharacterEffectKind.Add);
                Check(adapter.Apply(effect) == InventoryResult.Applied && target.m_stack == 40 && inventory.GetAllItems().Count == 2
                    && JournalChecksum.SameItem(original, JournalItemCodec.Capture(target, 40)), "Metadata difference keeps stacks separate: " + field);
            }
            inventory = new Inventory("stacking reserved", null, 2, 1); target = Wood(40); inventory.GetAllItems().Add(target);
            using (var adapter = new JournalCharacterInventory(inventory))
            {
                var request = Guid.NewGuid(); Check(adapter.TryReserve(request, adapter.Identify(target), 5), "Reserve partial stack before withdrawal");
                var effect = Effect(JournalItemCodec.Capture(Wood(5), 5), Guid.NewGuid(), CharacterEffectKind.Add);
                Check(adapter.Apply(effect) == InventoryResult.Applied && target.m_stack == 40 && inventory.GetAllItems().Count == 2 && adapter.IsBlocked(target),
                    "Incoming withdrawal never merges into an outstanding deposit reservation");
                Check(adapter.Apply(Effect(JournalItemCodec.Capture(target, 5), request, CharacterEffectKind.RemoveReserved)) == InventoryResult.Applied && target.m_stack == 35,
                    "Skipping reserved merge preserves exact later deposit removal");
            }
            inventory = new Inventory("stacking hostile callback", null, 1, 1); target = Wood(40); inventory.GetAllItems().Add(target);
            using (var adapter = new JournalCharacterInventory(inventory))
            {
                int callbacks = 0; inventory.m_onChanged += () => { callbacks++; target.m_stack++; };
                var effect = Effect(JournalItemCodec.Capture(Wood(5), 5), Guid.NewGuid(), CharacterEffectKind.Add);
                Check(adapter.Apply(effect) == InventoryResult.Failed && target.m_stack == 46 && callbacks == 1,
                    "Stack quantity changed by callback is never acknowledged as a verified delivery");
                Check(adapter.Apply(effect) == InventoryResult.Failed && target.m_stack == 46 && callbacks == 1,
                    "Uncertain callback mutation cannot repeat stacked addition");
            }
            Logger.LogInfo("M7 WITHDRAWAL STACKING PASS " + (checks - start) + " native checks");
        }
    }
}
