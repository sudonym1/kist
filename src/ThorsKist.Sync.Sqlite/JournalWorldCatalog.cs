using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;

namespace ThorsKist.Sync.Sqlite
{
    public sealed class JournalCatalogVault
    {
        public VaultIdentity Identity { get; }
        public JournalMachineState State { get; }
        private readonly byte[] metadata;
        internal JournalCatalogVault(JournalMachineState state, byte[] metadata)
        { State = state; Identity = state.Current.Identity; this.metadata = (byte[])metadata.Clone(); }
        public byte[] CopyMetadata() => (byte[])metadata.Clone();
    }

    // World startup boundary. Once marked, the database is the authority even
    // for an empty world. Never consult legacy bytes on subsequent opens.
    // The caller must stop the legacy writer before invoking this boundary.
    public sealed class JournalWorldCatalog
    {
        private readonly string path;
        public string World { get; }
        private JournalWorldCatalog(string path, string world) { this.path = path; World = world; }

        public static async Task<JournalWorldCatalog> OpenAsync(string databasePath, string world, string? legacyPath,
            Func<byte[], IReadOnlyList<LegacyVaultImport>> decode, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("World required", nameof(world));
            var catalog = new JournalWorldCatalog(Path.GetFullPath(databasePath), world);
            // Read the authority marker before opening even a changed/missing
            // old save. A successful import also retains its source in SQLite,
            // so a crash before marker commit can finish without that old file.
            var initialized = await catalog.InspectAsync(token).ConfigureAwait(false);
            if (!initialized.Marked)
            {
                if (!initialized.HasHistory && legacyPath != null && File.Exists(legacyPath))
                    await LegacySaveMigration.ImportAsync(catalog.path, legacyPath, bytes =>
                    {
                        var imports = decode(bytes);
                        if (imports == null || imports.Any(v => v.State.Identity.World != world)) throw new InvalidDataException("Imported vault belongs to another world");
                        return imports;
                    }, token).ConfigureAwait(false);
                await catalog.MarkAsync(token).ConfigureAwait(false);
            }
            // Validate every vault and metadata before returning an authority.
            // Any failure leaves admission disabled at the caller.
            await catalog.ReadAsync(token).ConfigureAwait(false);
            return catalog;
        }

        private Task<(bool Marked, bool HasHistory)> InspectAsync(CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using (var db = SqliteConnections.Open(path))
            {
                SqliteJournalStorage.EnsureSchema(db);
                using (var tx = db.BeginTransaction(deferred: true))
                {
                    var marked = SqliteJournalStorage.Scalar(db, tx, "SELECT world FROM world_catalog WHERE id=1;");
                    if (marked != null && (string)marked != World) throw new InvalidDataException("Catalog world mismatch");
                    bool history = Convert.ToInt64(SqliteJournalStorage.Scalar(db, tx, "SELECT (SELECT count(*) FROM vaults)+(SELECT count(*) FROM migration);")) != 0;
                    tx.Commit(); return (marked != null, history);
                }
            }
        }, token);

        private Task MarkAsync(CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using (var db = SqliteConnections.Open(path))
            using (var tx = db.BeginTransaction(deferred: false))
            {
                // Never bless a database copied from a different world.
                if (Convert.ToInt64(SqliteJournalStorage.Scalar(db, tx, "SELECT count(*) FROM vaults WHERE world<>$world;", ("$world", World))) != 0)
                    throw new InvalidDataException("Catalog contains another world");
                SqliteJournalStorage.Execute(db, tx, "INSERT OR IGNORE INTO world_catalog VALUES(1,$world);", ("$world", World));
                RequireWorld(db, tx); tx.Commit();
            }
        }, token);

        private void RequireWorld(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx)
        {
            if (!Equals(SqliteJournalStorage.Scalar(db, tx, "SELECT world FROM world_catalog WHERE id=1;"), World))
                throw new InvalidDataException("World catalog is not initialized");
        }

        public async Task<IReadOnlyList<JournalCatalogVault>> ReadAsync(CancellationToken token = default)
        {
            if (!(await InspectAsync(token).ConfigureAwait(false)).Marked) throw new InvalidDataException("World catalog is not initialized");
            var identities = await SqliteJournalStorage.ListVaultsAsync(path, token).ConfigureAwait(false);
            var retired = await Task.Run(() =>
            {
                using (var db = SqliteConnections.Open(path))
                using (var tx = db.BeginTransaction(deferred: true))
                using (var q = db.CreateCommand())
                {
                    q.Transaction = tx; q.CommandText = "SELECT v.vault FROM retired_vaults r JOIN vaults v ON v.id=r.vault_id;";
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    using (var reader = q.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
                    tx.Commit(); return ids;
                }
            }, token).ConfigureAwait(false);
            var result = new List<JournalCatalogVault>();
            foreach (var identity in identities)
            {
                if (identity.World != World) throw new InvalidDataException("Catalog contains another world");
                if (retired.Contains(identity.Vault)) continue;
                using (var storage = new SqliteJournalStorage(path, identity))
                {
                    var state = await storage.ReadStateAsync(token).ConfigureAwait(false) ?? throw new InvalidDataException("Missing catalog state");
                    var metadata = await storage.ReadLegacyMetadataAsync(token).ConfigureAwait(false) ?? throw new InvalidDataException("Missing catalog metadata");
                    result.Add(new JournalCatalogVault(state, metadata));
                }
            }
            return result.AsReadOnly();
        }

        // New hird identity and its metadata become visible together. There is
        // no empty inventory placeholder that can outlive a failed metadata write.
        public Task CreateAsync(VaultState initial, byte[] metadata, CancellationToken token = default)
        {
            var immutable = (byte[])metadata.Clone();
            if (initial.Identity.World != World) throw new ArgumentException("Wrong catalog world");
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using (var db = SqliteConnections.Open(path))
                using (var tx = db.BeginTransaction(deferred: false))
                {
                    RequireWorld(db, tx);
                    if (Convert.ToInt64(SqliteJournalStorage.Scalar(db, tx, "SELECT count(*) FROM vaults WHERE world=$w AND vault=$v;",
                        ("$w", initial.Identity.World), ("$v", initial.Identity.Vault))) != 0) throw new InvalidOperationException("Vault identity already exists, including retired history");
                    long id = SqliteJournalStorage.InsertNewVault(db, tx, JournalMachineState.Create(initial), null);
                    SqliteJournalStorage.Execute(db, tx, "INSERT INTO legacy_metadata VALUES($id,$body);", ("$id", id), ("$body", immutable));
                    tx.Commit();
                }
            }, token);
        }

        // Optimistic metadata comparison prevents overlapping membership/anchor
        // actions from silently replacing one another. Item writers use their
        // separate journal revision and never overwrite this row.
        public Task<bool> UpdateMetadataAsync(VaultIdentity identity, byte[] expected, byte[] metadata, CancellationToken token = default)
        {
            var before = (byte[])expected.Clone(); var after = (byte[])metadata.Clone();
            if (identity.World != World) throw new ArgumentException("Wrong catalog world");
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using (var db = SqliteConnections.Open(path))
                using (var tx = db.BeginTransaction(deferred: false))
                {
                    RequireWorld(db, tx);
                    if (SqliteJournalStorage.Read(db, tx, identity) == null) throw new InvalidDataException("Missing vault");
                    var old = SqliteJournalStorage.Scalar(db, tx, @"SELECT m.body FROM legacy_metadata m JOIN vaults v ON v.id=m.vault_id
WHERE v.world=$w AND v.vault=$v;", ("$w", identity.World), ("$v", identity.Vault)) as byte[];
                    if (old == null || !old.SequenceEqual(before)) { tx.Commit(); return false; }
                    SqliteJournalStorage.Execute(db, tx, @"UPDATE legacy_metadata SET body=$body WHERE vault_id=
(SELECT id FROM vaults WHERE world=$w AND vault=$v);", ("$body", after), ("$w", identity.World), ("$v", identity.Vault));
                    tx.Commit(); return true;
                }
            }, token);
        }

        // Retirement preserves epochs, request identities, stock history and the
        // original metadata. It cannot make an old retry executable as a new
        // vault, and a stale legacy save cannot resurrect the hird on restart.
        public Task RetireEmptyAsync(VaultIdentity identity, CancellationToken token = default) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using (var db = SqliteConnections.Open(path))
            using (var tx = db.BeginTransaction(deferred: false))
            {
                RequireWorld(db, tx);
                var state = SqliteJournalStorage.Read(db, tx, identity) ?? throw new InvalidDataException("Missing vault");
                if (state.Current.Stacks.Count != 0 || state.Replication.Sessions.Any(s => s.Phase != SessionPhase.Closed)
                    || state.Results.Any(r => r.Entry.Effect != null && !state.Replication.Settlements.Any(s => s.Operation == r.Entry.OperationId)))
                    throw new InvalidOperationException("Vault must be empty with every session and item effect settled");
                SqliteJournalStorage.Execute(db, tx, @"INSERT OR IGNORE INTO retired_vaults SELECT id FROM vaults WHERE world=$w AND vault=$v;",
                    ("$w", identity.World), ("$v", identity.Vault));
                tx.Commit();
            }
        }, token);
    }
}
