using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

static class CatalogTests
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    public static async Task<int> Run()
    {
        checks = 0;
        var root = Path.Combine(Path.GetTempPath(), "thorskist-m7-catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var v = new VaultIdentity("world", "vault", Guid.NewGuid());
            var unknown = new ItemRecord("hash:123", 9, 110, new byte[] { 0, 255, 17 });
            var initial = new VaultState(v, 0, new[] { new StackRecord(Guid.NewGuid(), -1, unknown) });
            var header = new byte[] { 1, 255, 3 };
            var source = Path.Combine(root, "legacy.dat"); var path = Path.Combine(root, "catalog.db");
            File.WriteAllBytes(source, new byte[] { 42, 17 }); int decodes = 0;
            var catalog = await JournalWorldCatalog.OpenAsync(path, "world", source, bytes =>
            { decodes++; Check(bytes.SequenceEqual(new byte[] { 42, 17 }), "Immutable legacy source reaches decoder"); return new[] { new LegacyVaultImport(initial, header) }; });
            var read = (await catalog.ReadAsync()).Single();
            Check(read.Identity.Epoch == v.Epoch && read.CopyMetadata().SequenceEqual(header)
                && JournalChecksum.SameItem(read.State.Current.Stacks.Single().Item, unknown) && read.State.Current.Stacks.Single().Slot == -1,
                "Catalog import retains epoch, header, unknown metadata and overflow");
            Check(File.ReadAllBytes(source).SequenceEqual(new byte[] { 42, 17 }) && Directory.GetFiles(root, "*.journal-backup-*").Length == 1,
                "Startup preserves and backs up legacy bytes");
            File.WriteAllBytes(source, new byte[] { 99 });
            catalog = await JournalWorldCatalog.OpenAsync(path, "world", source, _ => throw new Exception("Stale source was decoded"));
            Check(decodes == 1 && (await catalog.ReadAsync()).Single().Identity.Epoch == v.Epoch, "Restart never consults changed old save");
            var after = new byte[] { 4, 5 };
            Check(await catalog.UpdateMetadataAsync(v, header, after), "Commit membership/anchor metadata");
            Check(!await catalog.UpdateMetadataAsync(v, header, new byte[] { 6 }), "Stale metadata cannot overwrite newer membership");
            using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, v), initial))
            {
                var address = new SessionAddress(v.World, v.Vault, v.Epoch, "account", Guid.NewGuid());
                await writer.ExecuteAsync(new ItemCommand(address, Guid.NewGuid(), initial.Stacks.Single().Id, ItemCommandKind.Withdraw,
                    new ItemRecord(unknown.Prefab, 2, unknown.FormatVersion, unknown.CopyPayload())));
            }
            read = (await catalog.ReadAsync()).Single();
            Check(read.CopyMetadata().SequenceEqual(after) && read.State.Current.Stacks.Single().Item.Quantity == 7,
                "Item commits and catalog metadata do not overwrite each other");
            try { await JournalWorldCatalog.OpenAsync(path, "other-world", source, _ => Array.Empty<LegacyVaultImport>()); throw new Exception("Wrong world accepted"); }
            catch (InvalidDataException) { checks++; }
            var newV = new VaultIdentity("world", "new-vault", Guid.NewGuid());
            await catalog.CreateAsync(new VaultState(newV, 4, Array.Empty<StackRecord>()), header);
            Check((await catalog.ReadAsync()).Count == 2, "New vault state and header committed together");
            try { await catalog.CreateAsync(new VaultState(newV, 4, Array.Empty<StackRecord>()), after); throw new Exception("Duplicate vault overwritten"); }
            catch (InvalidOperationException) { checks++; }
            Check((await catalog.ReadAsync()).Single(x => x.Identity.Vault == newV.Vault).CopyMetadata().SequenceEqual(header), "Duplicate creation preserves original header");
            // Empty worlds also retain authority: a stale .dat appearing later
            // must not resurrect a deleted/disbanded hird.
            var emptyPath = Path.Combine(root, "empty.db");
            await JournalWorldCatalog.OpenAsync(emptyPath, "world", null, _ => throw new Exception("Unexpected decode"));
            var empty = await JournalWorldCatalog.OpenAsync(emptyPath, "world", source, _ => throw new Exception("Empty world resurrected legacy data"));
            Check((await empty.ReadAsync()).Count == 0, "Empty-world marker prevents stale resurrection");
            try { await LegacySaveMigration.ImportAsync(emptyPath, source, _ => new[] { new LegacyVaultImport(initial, header) }); throw new Exception("Late import replaced initialized authority"); }
            catch (InvalidOperationException) { checks++; }
            Check((await empty.ReadAsync()).Count == 0, "Concurrent/late migration cannot resurrect initialized empty world");
            // Import committed but bootstrap marker absent (e.g. power loss):
            // finish from SQLite without relying on the old file still existing.
            var interrupted = Path.Combine(root, "interrupted.db");
            await LegacySaveMigration.ImportAsync(interrupted, source, _ => new[] { new LegacyVaultImport(initial, header) });
            File.Delete(source);
            var resumed = await JournalWorldCatalog.OpenAsync(interrupted, "world", source, _ => throw new Exception("Import repeated"));
            Check((await resumed.ReadAsync()).Single().Identity.Epoch == v.Epoch, "Bootstrap recovers interrupted import without source");
            var wrong = Path.Combine(root, "wrong.db"); File.WriteAllBytes(source, new byte[] { 1 });
            try { await JournalWorldCatalog.OpenAsync(wrong, "other", source, _ => new[] { new LegacyVaultImport(initial, header) }); throw new Exception("Wrong-world import accepted"); }
            catch (InvalidDataException) { checks++; }
            Check((await SqliteJournalStorage.ListVaultsAsync(wrong)).Count == 0 && File.ReadAllBytes(source).Single() == 1,
                "Failed import leaves stock absent and source intact");
            Console.WriteLine($"PASS {checks} M7 catalog assertions"); return checks;
        }
        finally { Directory.Delete(root, true); }
    }
}
