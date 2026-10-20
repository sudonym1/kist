using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private void ReservationInterception()
        {
            int start = checks;
            var inv = new Inventory("M7 reservation scratch", null, 3, 2);
            var source = Wood(10); inv.GetAllItems().Add(source);
            using (var adapter = new JournalCharacterInventory(inv))
            {
                var request = Guid.NewGuid();
                Check(adapter.TryReserve(request, adapter.Identify(source), 3), "Guard reserve exact source");
                Check(!adapter.TryReserve(Guid.NewGuid(), adapter.Identify(source), 1), "Guard repeated deposit refused");
                Check(inv.CountItems(source.m_shared.m_name, -1, false) == 10, "Reserved item remains present in ordinary counts");
                CraftContext.Push();
                try
                {
                    Check(inv.CountItems(source.m_shared.m_name, -1, false) == 0, "Recipes cannot count a frozen reserved source as spendable");
                    CraftContext.EnterRaw();
                    try { Check(inv.CountItems(source.m_shared.m_name, -1, false) == 0, "Raw local payment counts also exclude reserved sources"); }
                    finally { CraftContext.ExitRaw(); }
                }
                finally { CraftContext.Pop(); }
                Check(!KistClient.Deposit(inv, source, 1), "Reserved source cannot enter legacy deposit path");
                Check(!inv.RemoveItem(source) && !inv.RemoveItem(source, 2) && !inv.RemoveOneItem(source)
                    && !inv.RemoveItem(0) && source.m_stack == 10, "Object, quantity, one and index removals blocked");
                inv.RemoveItem(source.m_shared.m_name, 2, -1, false);
                Check(source.m_stack == 10, "String removal cannot mutate reserved source directly");
                inv.RemoveAll(); inv.RemoveUnequipped(); inv.SetHeight(1);
                Check(inv.GetAllItems().Single() == source && inv.GetHeight() == 2, "Bulk removals and resize blocked");
                var incoming = Wood(2);
                Check(!inv.CanAddItem(incoming) && !inv.CanAddItem(incoming.m_dropPrefab, 2)
                    && !inv.AddItem(incoming) && !inv.AddItem(incoming, new Vector2i(1, 0))
                    && !inv.AddItem(incoming.m_dropPrefab, 2), "Incoming merge and capacity paths blocked");
                Check(inv.AddItem("Wood", 1, 1, 0, 0, "", false) == null, "String creation cannot bypass reservation merge guard");
                var low = typeof(Inventory).GetMethod("AddItem", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) }, null);
                Check(!(bool)low.Invoke(inv, new object[] { incoming, 1, 0, 0, false }) && incoming.m_stack == 2 && source.m_stack == 10,
                    "Private merge entry cannot change either stack");
                var target = new Inventory("M7 move destination", null, 3, 2);
                target.MoveItemToThis(inv, source); target.MoveAll(inv);
                Check(!target.MoveItemToThis(inv, source, 1, 0, 0) && target.StackAll(inv) == 0
                    && target.GetAllItems().Count == 0 && source.m_stack == 10, "Moves and stack-all cannot credit destination first");
                var other = Wood(4); other.m_quality = 2; other.m_gridPos = new Vector2i(1, 0); inv.GetAllItems().Add(other);
                inv.RemoveItem(other.m_shared.m_name, 1, 2, false);
                Check(other.m_stack == 3 && source.m_stack == 10, "Unreserved quality remains spendable");
                target.MoveItemToThis(inv, other);
                Check(target.GetAllItems().Single() == other && !inv.ContainsItem(other), "Unreserved object can move out");
                // Uninitialized managed shells exercise actual patched entry
                // points without constructing a character or touching a save.
                // Each prefix must reject before any native field is accessed.
                var humanoid = (Humanoid)FormatterServices.GetUninitializedObject(typeof(Humanoid));
                var player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
                Check(!humanoid.EquipItem(source) && !humanoid.DropItem(inv, source, 1)
                    && !humanoid.ConsumeItem(inv, source) && !player.ConsumeItem(inv, source) && !player.EatFood(source),
                    "Equip, drop, consume and food effects blocked before character access");
                humanoid.UseItem(inv, source, true);
                typeof(Player).GetMethod("QueueEquipAction", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(player, new object[] { source });
                Check(!source.m_equipped && source.m_stack == 10, "Use and queued equip cannot spend source");
                var gui = (InventoryGui)FormatterServices.GetUninitializedObject(typeof(InventoryGui));
                var grid = (InventoryGrid)FormatterServices.GetUninitializedObject(typeof(InventoryGrid));
                typeof(InventoryGrid).GetField("m_inventory", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(grid, target);
                Check(!grid.DropItem(inv, source, 1, Vector2i.zero), "Grid drop blocked before swap/removal");
                typeof(InventoryGui).GetMethod("SetupDragItem", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(gui, new object[] { source, inv, 1 });
                typeof(InventoryGui).GetMethod("OnRightClickItem", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(gui, new object[] { grid, source, Vector2i.zero });
                typeof(InventoryGui).GetMethod("OnSelectedItem", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(gui,
                    new object[] { grid, source, Vector2i.zero, default(InventoryGrid.Modifier) });
                Check(source.m_stack == 10 && inv.ContainsItem(source), "Drag, split selection and right click blocked before GUI access");
                Check(adapter.ReleaseRejectedReservation(request) && inv.RemoveItem(source, 1) && source.m_stack == 9,
                    "Definitive rejection restores ordinary inventory operations");
            }
            foreach (int quantity in new[] { 5, 10 })
            {
                var exact = new Inventory("M7 verified removal", null, 2, 1); var item = Wood(10); exact.GetAllItems().Add(item);
                using var adapter = new JournalCharacterInventory(exact); var request = Guid.NewGuid();
                var record = JournalItemCodec.Capture(item, quantity);
                Check(adapter.TryReserve(request, adapter.Identify(item), quantity), "Reserve verified removal");
                int callbacks = 0;
                exact.m_onChanged += () =>
                {
                    callbacks++;
                    Check(!exact.RemoveItem(item) && !exact.RemoveItem(item, quantity), "Reentrant callbacks cannot reuse verified permit");
                    Check(!exact.AddItem(Wood(1)), "Reentrant merge remains blocked during verified removal");
                };
                var effect = Effect(record, request, CharacterEffectKind.RemoveReserved);
                Check(adapter.Apply(effect) == InventoryResult.Applied && callbacks == 1
                    && (quantity == 10 ? !exact.ContainsItem(item) : item.m_stack == 5), "Verified partial/whole removal succeeds exactly once");
                Check(adapter.Apply(effect) == InventoryResult.AlreadyApplied && callbacks == 1, "Duplicate verified effect has no inventory callbacks");
            }
            var hostile = new Inventory("M7 callback metadata mutation", null, 2, 1); var hostileItem = Wood(10); hostile.GetAllItems().Add(hostileItem);
            using (var adapter = new JournalCharacterInventory(hostile))
            {
                var request = Guid.NewGuid(); var record = JournalItemCodec.Capture(hostileItem, 3);
                Check(adapter.TryReserve(request, adapter.Identify(hostileItem), 3), "Reserve before foreign callback mutation");
                hostile.m_onChanged += () => hostileItem.m_customData["callback"] = "mutated after removal";
                var identity = new VaultIdentity("m7", "callbacks", Guid.NewGuid());
                var address = new SessionAddress(identity.World, identity.Vault, identity.Epoch, "scratch", Guid.NewGuid());
                var initial = JournalMachineState.Create(new VaultState(identity, 2, Array.Empty<StackRecord>()));
                var deposit = JournalMachine.Execute(initial, new ItemCommand(address, request, Guid.NewGuid(), ItemCommandKind.Deposit, record));
                var replica = new JournalReplica(adapter);
                Check(replica.Install(address, initial.Checkpoint, Array.Empty<Guid>()), "Install hostile callback replica");
                Check(replica.Process(address, deposit.Result.Entry) == ReplicaResult.Blocked && replica.Sequence == 0
                    && hostileItem.m_stack == 7, "Post-removal metadata corruption never advances acknowledgment position");
                adapter.ValidateNextReservation();
                Check(!adapter.IsValid && adapter.Apply(deposit.Result.Entry.Effect) == InventoryResult.InventoryChanged && hostileItem.m_stack == 7,
                    "Failed removal cannot be repeated after callback corruption");
            }
            foreach (string mutation in new[] { "quantity", "metadata", "replacement", "load", "grave" })
            {
                var changed = new Inventory("M7 lifecycle scratch", null, 2, 1); var item = Wood(10); changed.GetAllItems().Add(item);
                using var adapter = new JournalCharacterInventory(changed); var request = Guid.NewGuid(); int invalidations = 0;
                adapter.Invalidated += () => invalidations++;
                var effect = Effect(JournalItemCodec.Capture(item, 3), request, CharacterEffectKind.RemoveReserved);
                Check(adapter.TryReserve(request, adapter.Identify(item), 3), "Reserve before lifecycle mutation");
                if (mutation == "quantity") item.m_stack--;
                if (mutation == "metadata") item.m_customData["foreign"] = "changed without Changed callback";
                if (mutation == "replacement") { changed.GetAllItems().Clear(); changed.GetAllItems().Add(Wood(10)); }
                if (mutation == "load")
                {
                    var replacement = new Inventory("M7 load replacement", null, 2, 1); replacement.GetAllItems().Add(Wood(10));
                    var bytes = new ZPackage(); replacement.Save(bytes); changed.Load(new ZPackage(bytes.GetArray()));
                }
                if (mutation == "grave")
                {
                    var grave = new Inventory("M7 grave", null, 2, 1); grave.MoveInventoryToGrave(changed);
                    Check(grave.GetAllItems().Single() == item, "Death transfer proceeds after invalidation");
                }
                adapter.ValidateNextReservation(); adapter.ValidateNextReservation();
                Check(!adapter.IsValid && invalidations == 1 && !adapter.HasReservations, "Lifecycle invalidation is latched: " + mutation);
                Check(adapter.Apply(effect) == InventoryResult.InventoryChanged && item.m_stack != 7,
                    "Late committed removal never touches changed/replacement inventory: " + mutation);
                if (mutation == "load" || mutation == "replacement") Check(changed.GetAllItems().Single().m_stack == 10, "Replacement quantity retained");
                Check(!adapter.TryReserve(Guid.NewGuid(), adapter.Identify(item), 1), "Invalidated inventory cannot submit another reservation");
            }
            Check(!JournalReservationGuard.Busy(inv) && !JournalReservationGuard.Blocks(source), "Disposal unregisters guard ownership");
            var replaced = new Inventory("M7 old inventory object", null, 2, 1); var oldItem = Wood(10); replaced.GetAllItems().Add(oldItem);
            Inventory current = replaced;
            using (var adapter = new JournalCharacterInventory(replaced, () => current))
            {
                var request = Guid.NewGuid(); var effect = Effect(JournalItemCodec.Capture(oldItem, 3), request, CharacterEffectKind.RemoveReserved);
                Check(adapter.TryReserve(request, adapter.Identify(oldItem), 3), "Reserve before inventory object replacement");
                current = new Inventory("M7 new inventory object", null, 2, 1); current.GetAllItems().Add(Wood(10));
                Check(adapter.Apply(effect) == InventoryResult.InventoryChanged && oldItem.m_stack == 10 && current.GetAllItems().Single().m_stack == 10,
                    "Inventory object replacement stops even a queued effect before polling");
                Check(!adapter.IsValid && !adapter.HasReservations, "Replaced inventory owner stays invalidated");
            }
            var adding = new Inventory("M7 replaced during addition", null, 2, 1); current = adding;
            using (var adapter = new JournalCharacterInventory(adding, () => current))
            {
                adding.m_onChanged += () => current = new Inventory("M7 replacement during callback", null, 2, 1);
                var add = Effect(JournalItemCodec.Capture(Wood(3), 3), Guid.NewGuid(), CharacterEffectKind.Add);
                Check(adapter.Apply(add) == InventoryResult.InventoryChanged && !adapter.IsValid && current.GetAllItems().Count == 0,
                    "Replacement during addition callback cannot authorize acknowledgment");
                Check(adapter.Apply(add) == InventoryResult.InventoryChanged && adding.GetAllItems().Sum(i => i.m_stack) == 3,
                    "Uncertain addition to detached inventory is never repeated");
            }
            Logger.LogInfo("M7 RESERVATION INTERCEPTION PASS " + (checks - start) + " assertions (real patched inventory and interaction entry points)");
        }
    }
}
