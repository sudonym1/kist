using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;

namespace ThorsKist.Sync.Sqlite
{
    public sealed class LegacyVaultImport
    {
        public VaultState State { get; }
        private readonly byte[] metadata;
        // Adapter preserves the hird header (members, invites, anchor, etc.).
        // State contains every stack, including raw unknown-prefab records.
        public LegacyVaultImport(VaultState state, byte[] metadata)
        { State = state ?? throw new ArgumentNullException(nameof(state)); this.metadata = (byte[])metadata.Clone(); }
        public byte[] CopyMetadata() => (byte[])metadata.Clone();
    }
    public static class LegacySaveMigration
    {
        // The game adapter must fully decode/validate the supplied immutable bytes
        // before returning. No Inventory.Load here: it can discard unknown items.
        // Decoder failure never creates partially imported vaults. Not live until M5.
        public static Task ImportAsync(string databasePath, string savePath,
            Func<byte[], IReadOnlyList<LegacyVaultImport>> decode, CancellationToken token = default,
            Action<StorageBoundary>? boundary = null) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            string sourcePath = Path.GetFullPath(savePath); string dbPath = Path.GetFullPath(databasePath);
            if (string.Equals(sourcePath, dbPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Save and database must differ");
            byte[] source = File.ReadAllBytes(sourcePath); string hash = Hash(source);
            string backup = sourcePath + ".journal-backup-" + hash;
            // Preserve the original and an independently flushed backup before
            // any import transaction. Interrupted copies are replaced on retry;
            // a mismatching existing backup is never trusted or overwritten.
            if (!File.Exists(backup))
            {
                string temporary = backup + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(source, 0, source.Length); file.Flush(true); }
                    try { File.Move(temporary, backup); }
                    catch (IOException) { if (!File.Exists(backup)) throw; }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (!File.ReadAllBytes(backup).SequenceEqual(source)) throw new InvalidDataException("Legacy backup differs from source");
            boundary?.Invoke(StorageBoundary.AfterBackup);
            using (var db = SqliteConnections.Open(dbPath))
            {
                SqliteJournalStorage.EnsureSchema(db);
                using (var tx = db.BeginTransaction(deferred: false))
                {
                    var previous = SqliteJournalStorage.Scalar(db, tx, "SELECT source_hash FROM migration WHERE id=1;");
                    if (previous != null)
                    {
                        if (!string.Equals((string)previous, hash, StringComparison.Ordinal)) throw new InvalidDataException("A different save was already imported");
                        tx.Commit(); return; // No decoder, no overwrite after restart/retry.
                    }
                    if (Convert.ToInt64(SqliteJournalStorage.Scalar(db, tx, "SELECT count(*) FROM world_catalog;")) != 0)
                        throw new InvalidOperationException("Migration cannot replace an initialized world authority");
                    if (Convert.ToInt64(SqliteJournalStorage.Scalar(db, tx, "SELECT count(*) FROM vaults;")) != 0)
                        throw new InvalidOperationException("Migration requires an empty database");
                    var imports = decode((byte[])source.Clone()) ?? throw new InvalidDataException("Decoder returned no result");
                    if (imports.GroupBy(v => new { v.State.Identity.World, v.State.Identity.Vault }).Any(g => g.Count() != 1))
                        throw new InvalidDataException("Duplicate imported vault");
                    foreach (var import in imports)
                    {
                        var state = JournalMachineState.Create(import.State); SqliteJournalStorage.Validate(state);
                        long id = SqliteJournalStorage.Write(db, tx, state, boundary);
                        SqliteJournalStorage.Execute(db, tx, "INSERT INTO legacy_metadata VALUES($id,$body);", ("$id", id), ("$body", import.CopyMetadata()));
                    }
                    SqliteJournalStorage.Execute(db, tx, "INSERT INTO migration VALUES(1,$hash,$path,$source);", ("$hash", hash), ("$path", backup), ("$source", source));
                    boundary?.Invoke(StorageBoundary.BeforeMigrationCommit);
                    tx.Commit();
                    boundary?.Invoke(StorageBoundary.AfterMigrationCommit);
                }
            }
        }, token);
        private static string Hash(byte[] source)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "").ToLowerInvariant(); }
    }
}
