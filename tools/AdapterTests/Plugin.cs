using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;
using UnityEngine;

namespace ThorsKist
{
    // Deliberately separate from the shipped plugin. Run only in an isolated
    // test installation with -savedir; never alters the player's inventory.
    [BepInPlugin("posixone_ThorsKist_AdapterTests", "ThorsKist Adapter Tests", "1.0.0")]
    [BepInDependency(Main.GUID)]
    public sealed partial class AdapterTests : BaseUnityPlugin
    {
        private bool ran;
        private int checks;
        private TcpProbe tcp;
        private JournalTcpProbe journalTcp;
        private JournalSessionTcpProbe journalSessionTcp;
        private JournalGameThread gameThread;
        private AuthenticatedPeerProbe authenticatedPeers;
        private Action<string> timingLog;
        private Harmony harmony;
        private byte[] legacyBytes;
        private LegacyVaultImport[] legacyImports;
        private System.Collections.Generic.IReadOnlyDictionary<int, string> legacyPrefabNames;
        private int legacyOwnerThread, legacyVersion;
        private void Awake()
        {
            var arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-savedir");
            string expected = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Paths.BepInExRootPath), "saves"));
            if (!arguments.Contains("-thorskist-adapter-tests") || index < 0 || index + 1 >= arguments.Length
                || !string.Equals(Path.GetFullPath(arguments[index + 1]), expected, StringComparison.OrdinalIgnoreCase))
            {
                enabled = false; Logger.LogWarning("M5 tests disabled: explicit test switch and isolated savedir required"); return;
            }
            // Valheim 1.0.20 only parses -savedir in its dedicated-server path.
            // Set it before menu startup too; this affects only this test process.
            Utils.SetSaveDataPath(expected);
            TestSaveIsolation.Root = expected;
            Main.MinHirdSize.Value = 1;
            harmony = new Harmony("posixone_ThorsKist_AdapterTests");
            harmony.PatchAll(typeof(AdapterTests).Assembly);
            if (FileHelpers.CloudStorageSupported || FileHelpers.CloudStorageSupportedAndEnabled
                || !string.Equals(Utils.GetSaveDataPath(FileHelpers.FileSource.Legacy), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test process save isolation did not install");
            authenticatedPeers = new AuthenticatedPeerProbe(message => Logger.LogInfo(message));
            timingLog = message => Logger.LogInfo(message);
            Logger.LogInfo("M7 MAIN THREAD TIMING ready: " + MainThreadProfiler.TargetCount + " methods; F4 starts/stops a capture in BepInEx/timings");
            Logger.LogInfo("M5 TEST SAVE ISOLATION " + expected);
            Logger.LogInfo("M7 LOCAL-ONLY SAVE ISOLATION installed; Steam cloud provider/enumeration hidden in this process; Steam settings unchanged");
        }
        private void Check(bool ok, string message)
        { if (!ok) throw new Exception(message); checks++; }
        private void Update()
        {
            MainThreadProfiler.Tick(timingLog);
            tcp?.Tick();
            journalTcp?.Tick();
            journalSessionTcp?.Tick();
            gameThread?.Drain(4);
            if (JournalGameplaySession.Current == null) ManualKeys();
            // Desktop legacy fixture importer is retired after the SQLite cutover.
            authenticatedPeers?.Tick();
            if (Player.m_localPlayer != null && JournalGameplaySession.Current == null && Input.GetKeyDown(KeyCode.F8)) ViewFixture();
            if (Player.m_localPlayer != null && Input.GetKeyDown(KeyCode.F9)) { KistUI.Close(); KistClient.RequestState(); }
            if (ran || ObjectDB.instance == null || ObjectDB.instance.GetItemPrefab("Wood") == null) return;
            ran = true;
            if (Environment.GetCommandLineArgs().Contains("-thorskist-control-verify"))
            {
                try { LocalControl(); } catch (Exception error) { Logger.LogError("M7 LOCAL CONTROL FAIL " + error); }
                Application.Quit(); return;
            }
            if (Environment.GetCommandLineArgs().Contains("-thorskist-cutover-verify"))
            { StartCoroutine(RunCutoverVerification()); return; }
            // Normal manual host/client play must not run SQLite fixtures in
            // the menu. Standalone adapter probes require an explicit switch.
            if (!Environment.GetCommandLineArgs().Contains("-thorskist-adapter-probes"))
            { Logger.LogInfo("M7 manual gameplay: adapter/database fixtures disabled; F4 profiling active"); return; }
            try
            {
                LocalControl(); Items(); ReservationInterception(); Projection(); Legacy(); Dispatch();
                tcp = new TcpProbe(message => Logger.LogInfo(message)); journalTcp = new JournalTcpProbe(message => Logger.LogInfo(message));
                journalSessionTcp = new JournalSessionTcpProbe(Wood(10), Path.Combine(Paths.BepInExRootPath, "m7-session-" + Guid.NewGuid().ToString("N") + ".db"), message => Logger.LogInfo(message));
                _ = Storage();
            }
            catch (Exception error) { Logger.LogError("M5 ADAPTER FAIL " + error); }
        }
        private void ViewFixture()
        {
            try
            {
                var player = Player.m_localPlayer;
                var hird = new Hird { Id = "m5-view-fixture", Name = "M5 disposable view", OwnerId = player.GetPlayerID(), Anchor = player.transform.position };
                hird.Members.Add(new HirdMember(player.GetPlayerID(), "M5 test"));
                // Opening synchronously requests authoritative state on a listen
                // host. Install the view-only fixture after that reply, then draw.
                KistUI.Open();
                KistClient.Hird = hird; KistClient.MinHirdSize = 1;
                var inventory = new Inventory("M5 view scratch", null, 8, 1);
                for (int i = 0; i < 3; i++) { var item = Wood(5 + i * 10); item.m_gridPos = new Vector2i(i, 0); inventory.GetAllItems().Add(item); }
                KistClient.Kist = inventory;
                var tab = typeof(KistUI).GetField("_tab", BindingFlags.Static | BindingFlags.NonPublic);
                tab.SetValue(null, Enum.ToObject(tab.FieldType, 0));
                KistUI.MarkDirty(); KistUI.Tick();
                var field = typeof(KistUI).GetField("_itemRows", BindingFlags.Static | BindingFlags.NonPublic);
                var rows = ((IEnumerable)field.GetValue(null)).Cast<object>().ToArray();
                var roots = rows.Select(r => (GameObject)r.GetType().GetField("Root").GetValue(r)).ToArray();
                Check(roots.Length == 3 && roots.All(r => r.activeSelf), "View fixture builds three pooled rows");
                KistUI.MarkDirty(); KistUI.Tick();
                var after = ((IEnumerable)field.GetValue(null)).Cast<object>().ToArray();
                Check(after.Length == 3 && after.Select((row, i) => ReferenceEquals(rows[i], row)).All(same => same), "Redraw reuses row views");
                inventory.RemoveItem(inventory.GetAllItems().Last()); KistUI.MarkDirty(); KistUI.Tick();
                Check(roots.Count(r => r.activeSelf) == 2, "Shrinking list releases surplus row to pool");
                Logger.LogInfo("M5 POOLED UI PASS " + checks + " cumulative assertions; F9 clears the scratch view");
            }
            catch (Exception error) { Logger.LogError("M5 POOLED UI FAIL " + error); }
        }
        private void OnDestroy() { MainThreadProfiler.Stop(); authenticatedPeers?.Dispose(); manualInventory?.Dispose(); tcp?.Dispose(); journalTcp?.Dispose(); journalSessionTcp?.Dispose(); gameThread?.Dispose(); harmony?.UnpatchSelf(); }
        private void Dispatch()
        {
            var dispatcher = new JournalGameThread(2); int actions = 0;
            var first = dispatcher.DispatchAsync(() => { actions++; return ReplicaResult.Applied; });
            var second = dispatcher.DispatchAsync(() => { actions++; return ReplicaResult.Applied; });
            var overflow = dispatcher.DispatchAsync(() => { actions++; return ReplicaResult.Applied; });
            Check(overflow.IsFaulted && actions == 0 && !first.IsCompleted, "Dispatcher admits bounded work without inline effects");
            Check(dispatcher.Drain(1) == 1 && actions == 1 && first.IsCompleted && !second.IsCompleted, "Per-frame dispatch limit");
            dispatcher.Dispose();
            Check(second.IsCanceled && dispatcher.DispatchAsync(() => ReplicaResult.Applied).IsFaulted, "World shutdown cancels queued effects");
            Logger.LogInfo("M5 DISPATCH PASS " + checks + " cumulative assertions");
        }
        private void Legacy()
        {
            var known = Wood(5); known.m_gridPos = Vector2i.zero;
            var unknown = Wood(7); unknown.m_gridPos = new Vector2i(1, 0);
            var missingPrefab = new GameObject("M5UnavailableModItem"); unknown.m_dropPrefab = missingPrefab;
            try
            {
                var hird = new Hird { Id = "m5-fixture", Name = "M5 disposable fixture", OwnerId = 123 };
                hird.Members.Add(new HirdMember(123, "M5"));
                var header = new ZPackage(); hird.WriteHeader(header);
                var inventory = new Inventory("M5 migration scratch", null, 8, 1);
                inventory.GetAllItems().Add(known); inventory.GetAllItems().Add(unknown);
                var items = new ZPackage(); inventory.Save(items);
                var save = new ZPackage(); save.Write(Wire.SaveFormat); save.Write(1); hird.WriteHeader(save); save.Write(items);
                var bytes = save.GetArray(); var decoded = JournalLegacyDecoder.Decode(bytes, "m5-world", 1).Single();
                legacyBytes = bytes;
                legacyImports = new[] { new LegacyVaultImport(decoded.State, decoded.CopyHeader()) };
                legacyPrefabNames = new System.Collections.ObjectModel.ReadOnlyDictionary<int, string>(
                    new System.Collections.Generic.Dictionary<int, string> { [known.m_dropPrefab.name.GetStableHashCode()] = known.m_dropPrefab.name });
                legacyOwnerThread = System.Threading.Thread.CurrentThread.ManagedThreadId; legacyVersion = ItemWire.InventoryVersion;
                Check(decoded.CopyHeader().SequenceEqual(header.GetArray()), "Legacy hird header preserved byte for byte");
                Check(decoded.State.Stacks.Count == 2 && decoded.State.Stacks.Sum(s => s.Item.Quantity) == 12, "Legacy quantities retained");
                var missing = decoded.State.Stacks.Single(s => s.Item.Prefab.StartsWith("hash:"));
                Check(missing.Slot == -1 && missing.Item.Quantity == 7 && missing.Item.CopyPayload().Length != 0, "Unavailable prefab and overflow retained");
                Check(JournalItemCodec.Restore(decoded.State.Stacks.Single(s => s.Item.Prefab == "Wood").Item).m_stack == 5, "Known legacy stack remains usable");
                var oldItems = items.GetArray(); Array.Copy(BitConverter.GetBytes(109), oldItems, 4);
                var oldSave = new ZPackage(); oldSave.Write(Wire.SaveFormat); oldSave.Write(1); hird.WriteHeader(oldSave); oldSave.Write(new ZPackage(oldItems));
                Check(JournalLegacyDecoder.Decode(oldSave.GetArray(), "m5-world", 1).Single().State.Stacks.All(s => s.Item.FormatVersion == 110),
                    "Legacy compact format 109 upgrades to current canonical metadata");
                // Format 108 predates the trailing cheat flags byte. Use actual
                // vanilla Save records, removing that byte from EACH item, not
                // just relabeling a modern inventory (which would misalign it).
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(108); writer.Write((ushort)2);
                    foreach (var item in new[] { known, unknown })
                    {
                        var entry = new ZPackage(); item.Save(entry); var raw = entry.GetArray();
                        writer.Write(raw, 0, raw.Length - 1);
                    }
                    writer.Flush();
                    var compact = new ZPackage(); compact.Write(Wire.SaveFormat); compact.Write(1); hird.WriteHeader(compact);
                    compact.Write(new ZPackage(stream.ToArray()));
                    var upgraded = JournalLegacyDecoder.Decode(compact.GetArray(), "m5-world", 1).Single();
                    Check(upgraded.CopyHeader().SequenceEqual(header.GetArray()) && upgraded.State.Stacks.Count == 2
                        && upgraded.State.Stacks.Sum(s => s.Item.Quantity) == 12, "Format 108 preserves header and every quantity");
                    Check(upgraded.State.Stacks.All(s => s.Item.FormatVersion == 110)
                        && upgraded.State.Stacks.Single(s => s.Item.Prefab.StartsWith("hash:")).Slot == -1,
                        "Format 108 preserves unknown prefab and overflow");
                    Check(JournalChecksum.SameItem(upgraded.State.Stacks.Single(s => s.Item.Prefab == "Wood").Item,
                        decoded.State.Stacks.Single(s => s.Item.Prefab == "Wood").Item), "Format 108 metadata canonicalizes identically to format 110");
                }
                try { JournalLegacyDecoder.Decode(bytes.Concat(new byte[] { 1 }).ToArray(), "m5-world", 1); throw new Exception("Trailing save bytes accepted"); }
                catch (InvalidDataException) { checks++; }
                Logger.LogInfo("M5 LEGACY DECODER PASS " + checks + " cumulative assertions (unknown prefab preserved)");
            }
            finally { UnityEngine.Object.Destroy(missingPrefab); }
        }
        private ItemDrop.ItemData Wood(int quantity = 10)
        {
            var prefab = ObjectDB.instance.GetItemPrefab("Wood");
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_stack = quantity;
            item.m_customData["m5-z"] = "last"; item.m_customData["m5-a"] = "first";
            item.m_crafterID = 1234; item.m_crafterName = "M5 disposable fixture";
            return item;
        }
        private CharacterEffect Effect(ItemRecord record, Guid request, CharacterEffectKind kind)
            => new CharacterEffect(Guid.NewGuid(), Guid.NewGuid(), request,
                new SessionAddress("m5-world", "m5-vault", Guid.NewGuid(), "m5-player", Guid.NewGuid()), kind, record);
        private void Items()
        {
            Check(ItemWire.InventoryVersion == 110, "Running game inventory version");
            var original = Wood(); original.m_gridPos = new Vector2i(3, 2); original.m_equipped = true;
            var record = JournalItemCodec.Capture(original, 3);
            var restored = JournalItemCodec.Restore(record);
            Check(restored.m_stack == 3 && restored.m_gridPos == Vector2i.zero && !restored.m_equipped, "Quantity/slot/equipment separation");
            Check(restored.m_crafterID == 1234 && restored.m_crafterName == original.m_crafterName
                && restored.m_customData["m5-a"] == "first" && restored.m_customData["m5-z"] == "last", "Vanilla metadata roundtrip");
            var reversed = original.Clone(); reversed.m_customData.Clear();
            reversed.m_customData["m5-a"] = "first"; reversed.m_customData["m5-z"] = "last";
            reversed.m_gridPos = new Vector2i(1, 1); reversed.m_equipped = false;
            Check(JournalChecksum.SameItem(record, JournalItemCodec.Capture(reversed, 3)), "Dictionary ordering and position independence");
            Check(JournalChecksum.SameItem(record, JournalItemCodec.Capture(original, 1), false), "Partial stack metadata independent of quantity");
            foreach (float durability in new[] { 0.01f, 0.07f, 1.13f, 2.01f, 999.99f })
            {
                var varied = Wood(); varied.m_durability = durability; varied.m_variant = 1;
                varied.m_worldLevel = 2; varied.m_pickedUp = true; varied.m_cheated = true;
                var encoded = JournalItemCodec.Capture(varied, 2);
                var roundtrip = JournalItemCodec.Restore(encoded);
                Check(JournalChecksum.SameItem(encoded, JournalItemCodec.Capture(roundtrip, 2))
                    && roundtrip.m_cheated && roundtrip.m_pickedUp && roundtrip.m_worldLevel == 2 && roundtrip.m_variant == 1,
                    "Durability/variant/world-level/cheat canonical roundtrip " + durability);
            }
            var wire = new ZPackage(); ItemWire.WriteItem(wire, original);
            var decoded = ItemWire.ReadItems(new ZPackage(wire.GetArray()));
            Check(decoded.Count == 1 && decoded[0].m_stack == 10 && decoded[0].m_crafterID == 1234, "Current ItemWire roundtrip");
            foreach (var mutation in new[] { "none", "merge", "split", "metadata", "slot", "death", "replacement" })
            {
                var inv = new Inventory("M5 disposable scratch", null, 2, 2);
                var stack = Wood(); inv.GetAllItems().Add(stack);
                using var adapter = new JournalCharacterInventory(inv);
                var request = Guid.NewGuid(); var local = adapter.Identify(stack);
                Check(adapter.TryReserve(request, local, 3) && adapter.IsBlocked(stack) && stack.m_stack == 10, "Reserve without removing " + mutation);
                Check(!adapter.TryReserve(Guid.NewGuid(), local, 1), "Duplicate deposit blocked " + mutation);
                var removal = Effect(JournalItemCodec.Capture(stack, 3), request, CharacterEffectKind.RemoveReserved);
                if (mutation == "merge") stack.m_stack++;
                if (mutation == "split") stack.m_stack--;
                if (mutation == "metadata") stack.m_customData["other"] = "changed";
                if (mutation == "slot") stack.m_gridPos = new Vector2i(1, 1);
                if (mutation == "death") inv.GetAllItems().Clear();
                if (mutation == "replacement") { inv.GetAllItems().Clear(); inv.GetAllItems().Add(Wood()); }
                var result = adapter.Apply(removal);
                if (mutation == "none")
                {
                    Check(result == InventoryResult.Applied && stack.m_stack == 7, "Exact verified removal");
                    Check(adapter.Apply(removal) == InventoryResult.AlreadyApplied && stack.m_stack == 7, "No duplicate removal");
                }
                else Check(result == InventoryResult.InventoryChanged && stack.m_stack != 7, "Changed identity refuses removal " + mutation);
                adapter.ExcludeSession(); Check(!adapter.IsBlocked(stack), "Exclusion releases without refund " + mutation);
                if (mutation == "replacement") Check(inv.GetAllItems()[0].m_stack == 10, "Replacement untouched");
            }
            var target = new Inventory("M5 addition scratch", null, 1, 1);
            using var receiver = new JournalCharacterInventory(target);
            var add = Effect(record, Guid.NewGuid(), CharacterEffectKind.Add);
            Check(receiver.Apply(add) == InventoryResult.Applied && target.GetAllItems().Single().m_stack == 3, "Exact addition");
            Check(receiver.Apply(add) == InventoryResult.AlreadyApplied && target.GetAllItems().Single().m_stack == 3, "No repeated addition");
            Check(receiver.Apply(Effect(record, Guid.NewGuid(), CharacterEffectKind.Add)) == InventoryResult.NoCapacity
                && target.GetAllItems().Single().m_stack == 3, "Full inventory refuses without partial merge/drop");
            var conflicting = new CharacterEffect(add.EffectId, add.OperationId, add.RequestId, add.Recipient, add.Kind,
                new ItemRecord(record.Prefab, 2, record.FormatVersion, record.CopyPayload()));
            Check(receiver.Apply(conflicting) == InventoryResult.Failed, "Effect identity cannot change quantity");
            Check(KistStock.Count(target, restored.m_shared.m_name, -1) == 3, "Cached count");
            target.RemoveItem(target.GetAllItems()[0], 1);
            Check(KistStock.Count(target, restored.m_shared.m_name, -1) == 2, "Inventory notification invalidates cached count");
            var bad = record.CopyPayload().Concat(new byte[] { 0 }).ToArray();
            try { JournalItemCodec.Restore(new ItemRecord(record.Prefab, 3, record.FormatVersion, bad)); throw new Exception("Trailing bytes accepted"); }
            catch (InvalidDataException) { checks++; }
            Logger.LogInfo("M5 ITEM ADAPTER PASS " + checks + " assertions (real Unity/Valheim inventory)");
        }
        private async Task Storage()
        {
            try
            {
                var inventory = new Inventory("M5 journal integration scratch", null, 2, 2);
                var source = Wood(10); inventory.GetAllItems().Add(source);
                using var character = new JournalCharacterInventory(inventory);
                gameThread = new JournalGameThread(8);
                string path = Path.Combine(Paths.BepInExRootPath, "m5-runtime-" + Guid.NewGuid().ToString("N") + ".db");
                string catalogPath = path + ".catalog.db", legacyPath = path + ".legacy.dat";
                int decoderThread = 0;
                var capturedDecode = await Task.Run(() =>
                {
                    decoderThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    return JournalLegacyDecoder.DecodeCaptured(legacyBytes, "m5-world", 1, legacyVersion, legacyPrefabNames).Single();
                });
                Check(decoderThread != legacyOwnerThread && capturedDecode.CopyHeader().SequenceEqual(legacyImports[0].CopyMetadata()),
                    "Captured migration decoder runs off Unity and retains hird header");
                Check(capturedDecode.State.Stacks.All(s => legacyImports[0].State.Stacks.Any(old => old.Slot == s.Slot && JournalChecksum.SameItem(old.Item, s.Item))),
                    "Worker migration preserves known and unavailable prefab records exactly");
                Logger.LogInfo("M7 WORKER MIGRATION DECODER PASS canonical items and unknowns decoded outside Unity thread");
                File.WriteAllBytes(legacyPath, legacyBytes);
                var catalog = await JournalWorldCatalog.OpenAsync(catalogPath, "m5-world", legacyPath, bytes =>
                {
                    Check(bytes.SequenceEqual(legacyBytes), "Real save bytes reach catalog import unchanged");
                    return legacyImports; // Decoded on the Unity thread above.
                });
                var imported = (await catalog.ReadAsync()).Single();
                Check(imported.CopyMetadata().SequenceEqual(legacyImports[0].CopyMetadata())
                    && JournalChecksum.Compute(imported.State.Current) == JournalChecksum.Compute(legacyImports[0].State),
                    "Real legacy import preserves all item records, header, unknowns and overflow");
                Check(File.ReadAllBytes(legacyPath).SequenceEqual(legacyBytes)
                    && Directory.GetFiles(Path.GetDirectoryName(legacyPath), Path.GetFileName(legacyPath) + ".journal-backup-*").Length == 1,
                    "Real legacy import leaves source intact with verified backup");
                File.WriteAllBytes(legacyPath, new byte[] { 255 });
                catalog = await JournalWorldCatalog.OpenAsync(catalogPath, "m5-world", legacyPath, _ => throw new Exception("Old save reimported"));
                Check((await catalog.ReadAsync()).Single().Identity.Epoch == imported.Identity.Epoch, "Mono catalog reopen ignores stale .dat and retains epoch");
                Logger.LogInfo("M7 CATALOG MONO PASS real decoder/import/backup/reopen " + catalogPath);
                var identity = new VaultIdentity("m5-world", "m5-vault", Guid.NewGuid());
                using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, identity),
                    new VaultState(identity, 1, Array.Empty<StackRecord>()), new SystemReplicationClock(), 4))
                {
                    var login = await server.LoginAsync("m5-player", Guid.NewGuid());
                    var replica = new JournalReplica(character);
                    var client = new ReplicationClient(replica, gameThread.DispatchAsync,
                        (address, sequence) => server.AcknowledgeAsync(address, sequence), address => server.ExcludeAsync(address, true));
                    Check(await client.BeginAsync(login), "Real inventory journal login");
                    await client.ProcessBatchAsync(login.Session.Address, login.Session.Catchup);
                    Check(client.AccessEnabled, "Mono session activation after marker");
                    var request = Guid.NewGuid(); var stackId = Guid.NewGuid();
                    var item = JournalItemCodec.Capture(source, 3);
                    Check(character.TryReserve(request, character.Identify(source), 3), "Integration deposit reservation");
                    await server.ExecuteAsync(new ItemCommand(login.Session.Address, request, stackId, ItemCommandKind.Deposit, item));
                    Check(source.m_stack == 10, "Server commit alone cannot remove character items");
                    var delivery = await server.QueueDeliveryAsync(login.Session.Address, client.Sequence, 4);
                    await client.ProcessBatchAsync(login.Session.Address, delivery.Entries);
                    Check(source.m_stack == 7 && client.AccessEnabled, "Verified deposit removes then acknowledges");
                    await client.ProcessBatchAsync(login.Session.Address, delivery.Entries);
                    Check(source.m_stack == 7, "Real journal duplicate deposit suppresses removal");
                    await server.ExecuteAsync(new ItemCommand(login.Session.Address, Guid.NewGuid(), stackId, ItemCommandKind.Withdraw,
                        new ItemRecord(item.Prefab, 2, item.FormatVersion, item.CopyPayload())));
                    delivery = await server.QueueDeliveryAsync(login.Session.Address, client.Sequence, 4);
                    await client.ProcessBatchAsync(login.Session.Address, delivery.Entries);
                    Check(inventory.GetAllItems().Sum(i => i.m_stack) == 9 && client.AccessEnabled, "Verified take adds exact quantity before acknowledgment");
                    long acknowledged = client.Sequence;
                    for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++)
                        if (inventory.GetItemAt(x, y) == null) { var filler = Wood(1); filler.m_gridPos = new Vector2i(x, y); inventory.GetAllItems().Add(filler); }
                    await server.ExecuteAsync(new ItemCommand(login.Session.Address, Guid.NewGuid(), stackId, ItemCommandKind.Withdraw,
                        new ItemRecord(item.Prefab, 1, item.FormatVersion, item.CopyPayload())));
                    delivery = await server.QueueDeliveryAsync(login.Session.Address, client.Sequence, 4);
                    await client.ProcessBatchAsync(login.Session.Address, delivery.Entries);
                    Check(client.Stopped && client.Sequence == acknowledged, "Full inventory stops journal without acknowledging take");
                    await server.ExcludeAsync(login.Session.Address, true);
                    var state = await server.CheckpointAsync();
                    Check(state.Current.Stacks.Single().Slot == -1 && state.Current.Stacks.Single().Item.Quantity == 1,
                        "Failed real addition is compensated as retained usable overflow");
                    character.ExcludeSession();
                }
                Logger.LogInfo("M5 SQLITE MONO PASS login/commit/ack/session activation " + path);
                Logger.LogInfo("M5 JOURNAL INVENTORY PASS " + checks + " cumulative assertions (deposit/take/duplicate/no-capacity/compensation)");
            }
            catch (Exception error) { Logger.LogError("M5 SQLITE MONO FAIL " + error); }
            finally { gameThread?.Dispose(); gameThread = null; }
        }
    }

    [HarmonyPatch]
    internal static class LocalFixtures
    {
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnNewCharacterDone))]
        [HarmonyPrefix]
        private static void LocalCharacter(ref bool forceLocal) { forceLocal = true; }
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnNewWorldDone))]
        [HarmonyPrefix]
        private static void LocalWorld(ref bool forceLocal) { forceLocal = true; }
    }
}
