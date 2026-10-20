using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using ThorsKist.Sync;
using UnityEngine;
using BepInEx;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private sealed class ImportJob
        {
            internal string Directory, Hash, Destination;
            internal long World;
            internal Hird Hird;
            internal byte[] RawInventory;
            internal IReadOnlyList<StackRecord> Records;
            internal Inventory Inventory;
            internal int Cursor;
        }
        private ImportJob importJob;
        private bool importChecked;
        private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private void TickManualImport()
        {
            if (Player.m_localPlayer == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                if (!importChecked)
                {
                    importChecked = true;
                    string directory = Path.Combine(Paths.BepInExRootPath, "manual-import");
                    string request = Path.Combine(directory, "import.request");
                    if (!File.Exists(request)) return;
                    var lines = File.ReadAllLines(request);
                    if (lines.Length != 2 || !long.TryParse(lines[0], out var world) || world != ZNet.instance.GetWorldUID())
                        throw new InvalidDataException("Import request does not name this isolated test world");
                    byte[] source = File.ReadAllBytes(Path.Combine(directory, "source.dat")); string hash;
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "");
                    if (!hash.Equals(lines[1], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Import source checksum changed");
                    if (File.Exists(Path.Combine(directory, "done-" + world + "-" + hash))) return;
                    if (manualInventory != null || JournalGameplaySession.Current != null) throw new InvalidOperationException("Close active reservation/session fixtures before import");
                    var decoded = JournalLegacyDecoder.Decode(source, world.ToString(), 2048);
                    if (decoded.Count != 1) throw new InvalidDataException("This test import expects exactly one source hird");
                    var package = new ZPackage(source);
                    package.ReadInt(); package.ReadInt(); var hird = Hird.ReadHeader(package); var raw = package.ReadPackage().GetArray();
                    if (package.GetPos() != package.Size()) throw new InvalidDataException("Trailing import source bytes");
                    int rows = Math.Max(Main.KistRows.Value, decoded[0].State.Stacks.Count == 0 ? 4 : decoded[0].State.Stacks.Max(s => s.Slot / 8 + 1));
                    if (decoded[0].State.Stacks.Any(s => s.Slot < 0) || rows > 256) throw new InvalidDataException("Source has overlapping/invalid positions; refusing a lossy legacy import");
                    Main.KistRows.Value = rows;
                    Main.MinHirdSize.Value = 1;
                    var player = Player.m_localPlayer;
                    hird.OwnerId = player.GetPlayerID(); hird.Anchor = player.transform.position;
                    if (!hird.IsMember(hird.OwnerId)) hird.Members.Add(new HirdMember(hird.OwnerId, player.GetPlayerName()));
                    KistServer.EnsureLoaded();
                    var vaults = (IList)typeof(KistServer).GetField("Vaults", PrivateStatic).GetValue(null);
                    foreach (var vault in vaults)
                        if (((Inventory)vault.GetType().GetField("Inventory").GetValue(vault)).NrOfItems() != 0)
                            throw new InvalidOperationException("Destination Kist is not empty; no import performed");
                    importJob = new ImportJob { Directory = directory, Hash = hash, World = world, Hird = hird, RawInventory = raw,
                        Records = decoded[0].State.Stacks, Inventory = new Inventory("Kist_" + hird.Id, null, 8, rows),
                        Destination = Path.Combine(TestSaveIsolation.Root, "ThorsKist", "kist_" + world + ".dat") };
                    ManualMessage("Checking " + importJob.Records.Count + " imported stacks before installing the test Kist...");
                }
                if (importJob == null) return;
                if (ZNet.instance.GetWorldUID() != importJob.World) throw new InvalidOperationException("World changed during import");
                for (int n = 0; n < 32 && importJob.Cursor < importJob.Records.Count; n++, importJob.Cursor++)
                {
                    var record = importJob.Records[importJob.Cursor];
                    var item = JournalItemCodec.Restore(record.Item);
                    if (item == null || item.m_shared == null || item.m_stack > item.m_shared.m_maxStackSize
                        || !JournalChecksum.SameItem(JournalItemCodec.Capture(item, item.m_stack), record.Item))
                        throw new InvalidDataException("Unknown/changed/oversized item at stack " + importJob.Cursor + "; no import performed");
                    item.m_gridPos = new Vector2i(record.Slot % 8, record.Slot / 8);
                    importJob.Inventory.GetAllItems().Add(item);
                }
                if (importJob.Cursor != importJob.Records.Count) return;
                CommitManualImport(importJob); importJob = null;
            }
            catch (Exception error)
            {
                importJob = null; importChecked = true;
                Logger.LogError("M7 DESKTOP IMPORT FAIL (source retained) " + error);
                KistClient.Message("Test import stopped. No items were dropped; inspect the import log.");
            }
        }
        private void CommitManualImport(ImportJob job)
        {
            if (Player.m_localPlayer == null || Player.m_localPlayer.GetPlayerID() != job.Hird.OwnerId
                || ZNet.instance == null || ZNet.instance.GetWorldUID() != job.World)
                throw new InvalidOperationException("Import destination character/world changed");
            var liveVaults = (IList)typeof(KistServer).GetField("Vaults", PrivateStatic).GetValue(null);
            foreach (var existing in liveVaults)
                if (((Inventory)existing.GetType().GetField("Inventory").GetValue(existing)).NrOfItems() != 0)
                    throw new InvalidOperationException("Destination received items during preflight; refusing to replace them");
            // Keep every inventory byte, including its original version. Only
            // this test copy's owner/membership and anchor header are adapted.
            var output = new ZPackage(); output.Write(Wire.SaveFormat); output.Write(1); job.Hird.WriteHeader(output);
            output.Write(new ZPackage(job.RawInventory)); var bytes = output.GetArray();
            var verify = new ZPackage(bytes); verify.ReadInt(); verify.ReadInt(); Hird.ReadHeader(verify);
            if (!verify.ReadPackage().GetArray().SequenceEqual(job.RawInventory) || verify.GetPos() != verify.Size()) throw new InvalidDataException("Import inventory bytes changed");
            Directory.CreateDirectory(Path.GetDirectoryName(job.Destination));
            string backup = job.Destination + ".before-desktop-import-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            if (File.Exists(job.Destination)) File.Copy(job.Destination, backup, false);
            File.WriteAllBytes(Path.Combine(job.Directory, "prepared-test-vault.dat"), bytes);
            var type = typeof(KistServer).GetNestedType("Vault", BindingFlags.NonPublic);
            var vault = Activator.CreateInstance(type, true);
            type.GetField("Hird").SetValue(vault, job.Hird); type.GetField("Inventory").SetValue(vault, job.Inventory);
            string temporary = job.Destination + ".import-" + Guid.NewGuid().ToString("N");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            if (File.Exists(job.Destination)) File.Replace(temporary, job.Destination, null); else File.Move(temporary, job.Destination);
            var vaults = (IList)typeof(KistServer).GetField("Vaults", PrivateStatic).GetValue(null);
            vaults.Clear(); vaults.Add(vault);
            typeof(KistServer).GetField("_loaded", PrivateStatic).SetValue(null, true);
            typeof(KistServer).GetField("_dirty", PrivateStatic).SetValue(null, false);
            File.WriteAllText(Path.Combine(job.Directory, "done-" + job.World + "-" + job.Hash),
                "stacks=" + job.Records.Count + "\nquantity=" + job.Records.Sum(s => (long)s.Item.Quantity) + "\nsource=" + job.Hash);
            File.Move(Path.Combine(job.Directory, "import.request"), Path.Combine(job.Directory, "import.completed-" + Guid.NewGuid().ToString("N")));
            Logger.LogInfo("M7 DESKTOP IMPORT PASS stacks=" + job.Records.Count + " quantity=" + job.Records.Sum(s => (long)s.Item.Quantity)
                + " inventory-bytes=" + job.RawInventory.Length + " preserved exactly; test owner=" + job.Hird.OwnerId + " anchor=" + job.Hird.Anchor + " destination=" + job.Destination);
            KistClient.RequestState();
            ManualMessage("Imported " + job.Records.Count + " stacks. Test hird owner/anchor updated; minimum members is 1. Open the Kist.");
        }
    }
}
