using System;
using System.Linq;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private JournalCharacterInventory manualInventory;
        private ItemDrop.ItemData manualSource;
        private ItemCommand manualCommand;
        private JournalReplica manualReplica;
        private JournalMachineState manualAuthority;
        private int manualBefore;
        private void ManualMessage(string text)
        {
            Logger.LogInfo("M7 MANUAL " + text);
            KistClient.Message(text);
        }
        private void ManualKeys()
        {
            if (Player.m_localPlayer == null) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.F5)) ManualKit();
                if (Input.GetKeyDown(KeyCode.F6)) ManualHold();
                if (Input.GetKeyDown(KeyCode.F7)) ManualRelease();
                if (Input.GetKeyDown(KeyCode.F10)) ManualCommit();
            }
            catch (Exception error) { Logger.LogError("M7 MANUAL FAIL " + error); }
        }
        private void ManualKit()
        {
            if (manualInventory != null) { ManualMessage("Release the held reservation with F7 first."); return; }
            var inventory = Player.m_localPlayer.GetInventory();
            foreach (var spec in new[] { ("Wood", 10), ("Stone", 10), ("Raspberries", 5), ("ArrowWood", 10), ("Club", 1) })
            {
                var prefab = ObjectDB.instance.GetItemPrefab(spec.Item1);
                if (prefab == null) throw new InvalidOperationException("Missing test prefab " + spec.Item1);
                var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab = prefab; item.m_stack = spec.Item2;
                if (!inventory.AddItem(item)) throw new InvalidOperationException("No room for test kit");
            }
            ManualMessage("Added disposable test kit. Put an unequipped item in hotbar slot 1, then press F6.");
        }
        private void ManualHold()
        {
            if (manualInventory != null) { ManualMessage("A reservation already exists. F7 releases; F10 commits."); return; }
            var player = Player.m_localPlayer; var inventory = player.GetInventory();
            var source = inventory.GetAllItems().FirstOrDefault(i => i.m_gridPos.x == 0 && i.m_gridPos.y == 0);
            if (source == null || source.m_equipped || source.m_shared.m_questItem)
            { ManualMessage("Put an unequipped, non-quest item in hotbar slot 1 first."); return; }
            var adapter = new JournalCharacterInventory(inventory);
            var request = Guid.NewGuid();
            if (!adapter.TryReserve(request, adapter.Identify(source), 1))
            { adapter.Dispose(); ManualMessage("Reservation refused; no items changed."); return; }
            manualInventory = adapter; manualSource = source; manualBefore = source.m_stack;
            var v = new VaultIdentity("manual-fixture", "loaded-character", Guid.NewGuid());
            var a = new SessionAddress(v.World, v.Vault, v.Epoch, "test-only:loaded-character", Guid.NewGuid());
            manualAuthority = JournalMachineState.Create(new VaultState(v, 4, Array.Empty<StackRecord>()));
            manualReplica = new JournalReplica(adapter);
            if (!manualReplica.Install(a, manualAuthority.Checkpoint, Array.Empty<Guid>())) throw new InvalidOperationException("Fixture snapshot rejected");
            manualCommand = new ItemCommand(a, request, Guid.NewGuid(), ItemCommandKind.Deposit, JournalItemCodec.Capture(source, 1));
            adapter.Invalidated += () => ManualMessage("Reservation INVALIDATED; character effects stopped. No removal or refund was performed.");
            ManualMessage("HELD " + source.m_shared.m_name + " x1; source quantity=" + manualBefore + ". Try equip/use/drop/split/move/merge. F7 releases; F10 commits one.");
        }
        private void ManualRelease()
        {
            if (manualInventory == null) { ManualMessage("No held reservation."); return; }
            int after = manualSource.m_stack; bool valid = manualInventory.IsValid;
            manualInventory.Dispose(); manualInventory = null;
            ManualMessage("RELEASED without removal/refund; source quantity before=" + manualBefore + ", now=" + after + ", adapter valid before release=" + valid);
            manualSource = null; manualCommand = null; manualReplica = null; manualAuthority = null;
        }
        private void ManualCommit()
        {
            if (manualInventory == null || !manualInventory.IsValid) { ManualMessage("No valid reservation to commit. F7 clears an invalidated fixture."); return; }
            var transition = JournalMachine.Execute(manualAuthority, manualCommand);
            var entry = transition.Result.Entry;
            var verified = JournalReplay.Prepare(manualReplica.Current, manualReplica.Sequence, entry);
            var result = manualReplica.ProcessVerified(manualCommand.Address, verified);
            int after = manualSource.m_stack;
            if (result != ReplicaResult.Applied || after != manualBefore - 1 || manualInventory.HasReservations)
                throw new InvalidOperationException("Loaded-character verified removal failed: " + result + "; quantity=" + after);
            if (manualReplica.ProcessVerified(manualCommand.Address, verified) != ReplicaResult.Duplicate || manualSource.m_stack != after)
                throw new InvalidOperationException("Loaded-character duplicate repeated an effect");
            manualAuthority = transition.State;
            ManualMessage("COMMIT PASS: exactly one removed; duplicate made no change. Test-only deterministic authority; this is not live peer authentication.");
            manualInventory.Dispose(); manualInventory = null; manualSource = null; manualCommand = null; manualReplica = null; manualAuthority = null;
        }
    }
}
