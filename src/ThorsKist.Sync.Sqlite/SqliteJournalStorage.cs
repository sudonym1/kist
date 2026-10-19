using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ThorsKist.Sync;

namespace ThorsKist.Sync.Sqlite
{
    public enum StorageBoundary { AfterCurrentWrite, AfterJournalWrite, BeforeCommit, AfterCommit, AfterBackup, BeforeMigrationCommit, AfterMigrationCommit }

    // One instance per writer. All provider calls (including open, schema, hashes
    // and recovery) execute on a worker. SQLite also serializes independent hosts.
    public sealed class SqliteJournalStorage : IJournalStorage
    {
        private readonly string path;
        private readonly VaultIdentity identity;
        private readonly Action<StorageBoundary>? boundary;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private bool disposed;
        public SqliteJournalStorage(string path, VaultIdentity identity, Action<StorageBoundary>? boundary = null)
        {
            this.path = Path.GetFullPath(path); this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.boundary = boundary;
        }
        public Task<JournalMachineState?> ReadStateAsync(CancellationToken cancellationToken) => Work(db =>
        {
            using (var tx = db.BeginTransaction(deferred: true))
            { var state = Read(db, tx, identity); tx.Commit(); return state; }
        }, cancellationToken);

        public Task<byte[]?> ReadLegacyMetadataAsync(CancellationToken token = default) => Work(db =>
        {
            using (var tx = db.BeginTransaction(deferred: true))
            {
                // Verify epoch and recovered stock before exposing migration metadata.
                if (Read(db, tx, identity) == null) { tx.Commit(); return null; }
                var data = Scalar(db, tx, @"SELECT m.body FROM legacy_metadata m JOIN vaults v ON v.id=m.vault_id
 WHERE v.world=$w AND v.vault=$v;", ("$w", identity.World), ("$v", identity.Vault));
                tx.Commit(); return data as byte[];
            }
        }, token);

        // Bootstrap discovers the persisted epochs instead of inventing new ones
        // on restart. Each returned vault must still pass ReadStateAsync recovery.
        public static Task<IReadOnlyList<VaultIdentity>> ListVaultsAsync(string path, CancellationToken token = default) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using (var db = SqliteConnections.Open(path))
            {
                EnsureSchema(db);
                using (var tx = db.BeginTransaction(deferred: true))
                using (var q = Query(db, tx, "SELECT world,vault,epoch FROM vaults ORDER BY world,vault;"))
                using (var r = q.ExecuteReader())
                {
                    var vaults = new List<VaultIdentity>();
                    while (r.Read()) vaults.Add(new VaultIdentity(r.GetString(0), r.GetString(1), Guid.ParseExact(r.GetString(2), "N")));
                    r.Close(); tx.Commit(); return (IReadOnlyList<VaultIdentity>)vaults.AsReadOnly();
                }
            }
        }, token);

        public Task CommitAsync(StorageCommit commit, CancellationToken cancellationToken) => Work(db =>
        {
            if (!identity.Matches(commit.State.Current.Identity)) throw new InvalidOperationException("Commit belongs to another vault/epoch");
            Validate(commit.State);
            using (var tx = db.BeginTransaction(deferred: false))
            {
                var previous = Read(db, tx, identity);
                if (previous != null && commit.ExpectedReplicationRevision.HasValue && previous.Replication.Revision != commit.ExpectedReplicationRevision.Value) throw new InvalidOperationException("Stale replication revision");
                if ((previous?.Head ?? 0) != commit.ExpectedHead) throw new InvalidOperationException("Stale storage head");
                if (previous != null) ValidateSuccessor(previous, commit.State);
                else if (commit.ExpectedHead != 0) throw new InvalidOperationException("Uninitialized vault");
                Write(db, tx, commit.State, boundary);
                boundary?.Invoke(StorageBoundary.BeforeCommit);
                tx.Commit();
                boundary?.Invoke(StorageBoundary.AfterCommit);
            }
            return true;
        }, cancellationToken);

        private Task<T> Work<T>(Func<SqliteConnection, T> action, CancellationToken token) => Task.Run(async () =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (disposed) throw new ObjectDisposedException(nameof(SqliteJournalStorage));
                token.ThrowIfCancellationRequested();
                using (var db = SqliteConnections.Open(path)) { EnsureSchema(db); return action(db); }
                // Once a transaction starts, cancellation cannot turn a committed
                // result into an apparently canceled request. Query on uncertainty.
            }
            finally { gate.Release(); }
        }, token);

        public void Dispose()
        {
            gate.Wait(); try { disposed = true; } finally { gate.Release(); }
        }

        internal static void EnsureSchema(SqliteConnection db)
        {
            // Existing databases need no writer lock just to open/read them.
            // Recheck under the lock only when creating the first schema.
            using (var q = db.CreateCommand())
            {
                q.CommandText = "PRAGMA user_version;";
                long found = Convert.ToInt64(q.ExecuteScalar());
                if (found == 2) return;
                if (found != 0 && found != 1) throw new InvalidDataException("Unsupported database schema");
            }
            using (var tx = db.BeginTransaction(deferred: false))
            {
                long version = Convert.ToInt64(Scalar(db, tx, "PRAGMA user_version;"));
                if (version == 2) { tx.Commit(); return; }
                if (version == 1)
                {
                    Execute(db, tx, "CREATE TABLE replication (vault_id INTEGER PRIMARY KEY REFERENCES vaults(id), body BLOB NOT NULL, checksum TEXT NOT NULL);");
                    var empty = StorageEncoding.Encode(ReplicationState.Empty, StorageEncoding.Replication);
                    Execute(db, tx, "INSERT INTO replication SELECT id,$body,$hash FROM vaults;", ("$body", empty), ("$hash", Hash(empty)));
                    Execute(db, tx, "PRAGMA user_version=2;"); tx.Commit(); return;
                }
                if (version != 0 || Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';")) != 0)
                    throw new InvalidDataException("Unsupported database schema; refusing automatic replacement");
                Execute(db, tx, @"
CREATE TABLE vaults (id INTEGER PRIMARY KEY, world TEXT NOT NULL, vault TEXT NOT NULL, epoch TEXT NOT NULL,
 capacity INTEGER NOT NULL CHECK(capacity>=0), head INTEGER NOT NULL CHECK(head>=0), checkpoint_seq INTEGER NOT NULL CHECK(checkpoint_seq>=0 AND checkpoint_seq<=head),
 checkpoint_checksum TEXT NOT NULL, current_checksum TEXT NOT NULL, checksum_schema INTEGER NOT NULL, history_checksum TEXT NOT NULL, UNIQUE(world,vault));
CREATE TABLE current_stacks (vault_id INTEGER NOT NULL REFERENCES vaults(id), stack_id TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(vault_id,stack_id));
CREATE TABLE checkpoint_stacks (vault_id INTEGER NOT NULL REFERENCES vaults(id), stack_id TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(vault_id,stack_id));
CREATE TABLE journal (vault_id INTEGER NOT NULL REFERENCES vaults(id), seq INTEGER NOT NULL CHECK(seq>0), body BLOB NOT NULL, PRIMARY KEY(vault_id,seq));
CREATE TABLE request_results (vault_id INTEGER NOT NULL REFERENCES vaults(id), player TEXT NOT NULL, request_id TEXT NOT NULL, seq INTEGER NOT NULL, body BLOB NOT NULL,
 PRIMARY KEY(vault_id,player,request_id), UNIQUE(vault_id,seq));
CREATE TABLE used_stack_ids (vault_id INTEGER NOT NULL REFERENCES vaults(id), stack_id TEXT NOT NULL, PRIMARY KEY(vault_id,stack_id));
CREATE TABLE legacy_metadata (vault_id INTEGER PRIMARY KEY REFERENCES vaults(id), body BLOB NOT NULL);
CREATE TABLE migration (id INTEGER PRIMARY KEY CHECK(id=1), source_hash TEXT NOT NULL, backup_path TEXT NOT NULL, source BLOB NOT NULL);
CREATE TABLE replication (vault_id INTEGER PRIMARY KEY REFERENCES vaults(id), body BLOB NOT NULL, checksum TEXT NOT NULL);
PRAGMA user_version=2;");
                tx.Commit();
            }
        }
        internal static object? Scalar(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object)[] args)
        { using (var q = Query(db, tx, sql, args)) return q.ExecuteScalar(); }
        internal static void Execute(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object)[] args)
        { using (var q = Query(db, tx, sql, args)) q.ExecuteNonQuery(); }
        private static SqliteCommand Query(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object)[] args)
        {
            var q = db.CreateCommand(); q.Transaction = tx; q.CommandText = sql;
            foreach (var arg in args) q.Parameters.AddWithValue(arg.Item1, arg.Item2);
            return q;
        }
        internal static void Validate(JournalMachineState s) => JournalMachineState.Recover(s.Checkpoint, s.Current, s.Head, s.Journal, s.Results, s.UsedStackIds, s.Replication);
        // Stock checksums intentionally exclude deduplication history. Protect its
        // persisted representation separately, even after the journal is pruned.
        private static string HistoryChecksum(IEnumerable<CommandResult> results, IEnumerable<Guid> used)
        {
            using (var hash = SHA256.Create())
            {
                var records = results.OrderBy(r => r.Entry.Sequence).ToArray();
                var ids = used.OrderBy(id => id.ToString("N"), StringComparer.Ordinal).ToArray();
                using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
                using (var w = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
                {
                    w.Write(records.Length);
                    foreach (var result in records) StorageEncoding.Result(w, result);
                    w.Write(ids.Length);
                    foreach (var id in ids) w.Write(id.ToString("N"));
                    w.Flush(); stream.FlushFinalBlock();
                    return BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant();
                }
            }
        }
        private static string Hash(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static void ValidateSuccessor(JournalMachineState old, JournalMachineState next)
        {
            if (next.Replication.Revision < old.Replication.Revision) throw new InvalidOperationException("Replication revision regression");
            foreach (var session in old.Replication.Sessions)
            {
                var retained = next.Replication.Sessions.SingleOrDefault(x => x.Address.Session == session.Address.Session);
                if (retained == null || retained.Address.Player != session.Address.Player || retained.LoginRequest != session.LoginRequest
                    || retained.LoginMarker != session.LoginMarker || retained.Baseline.Sequence != session.Baseline.Sequence
                    || retained.Baseline.Checksum != session.Baseline.Checksum || retained.Acknowledged < session.Acknowledged
                    || retained.QueuedThrough < session.QueuedThrough || (session.Phase == SessionPhase.Closed && retained.Phase != SessionPhase.Closed)
                    || (int)retained.Phase < (int)session.Phase
                    || (session.SuspendedUntil.HasValue && (!retained.SuspendedUntil.HasValue || retained.SuspendedUntil.Value < session.SuspendedUntil.Value)))
                    throw new InvalidOperationException("Commit regresses session history");
            }
            foreach (var settlement in old.Replication.Settlements)
                if (!next.Replication.Settlements.Any(x => x.Operation == settlement.Operation && x.Kind == settlement.Kind && x.Compensation == settlement.Compensation)) throw new InvalidOperationException("Settlement regression");
            if (next.Checkpoint.Sequence < old.Checkpoint.Sequence || next.Current.Capacity != old.Current.Capacity
                || old.UsedStackIds.Except(next.UsedStackIds).Any()) throw new InvalidOperationException("Commit regresses durable history");
            foreach (var result in old.Results)
            {
                var retained = next.Results.SingleOrDefault(r => r.Command.Address.Player == result.Command.Address.Player && r.Command.RequestId == result.Command.RequestId);
                if (retained == null || !StorageEncoding.Encode(result, StorageEncoding.Result).SequenceEqual(StorageEncoding.Encode(retained, StorageEncoding.Result)))
                    throw new InvalidOperationException("Commit forgets or changes a durable result");
            }
            var added = next.Journal.Where(e => e.Sequence > old.Head).ToArray();
            var replay = JournalReplay.Replay(new JournalSnapshot(old.Head, old.Current, JournalChecksum.Compute(old.Current)), added);
            if (replay.Sequence != next.Head || replay.Checksum != JournalChecksum.Compute(next.Current))
                throw new InvalidOperationException("Commit omits appended history");
            var baseline = JournalReplay.Replay(old.Checkpoint, old.Journal.Concat(added).Where(e => e.Sequence <= next.Checkpoint.Sequence));
            if (baseline.Sequence != next.Checkpoint.Sequence || baseline.Checksum != next.Checkpoint.Checksum)
                throw new InvalidOperationException("Checkpoint rewrites durable history");
            foreach (var entry in old.Journal.Where(e => e.Sequence > next.Checkpoint.Sequence))
                if (!next.Journal.Any(e => JournalMachineState.SameEntry(e, entry))) throw new InvalidOperationException("Commit rewrites journal suffix");
            if (next.Results.Where(r => r.Entry.Sequence <= old.Head).Count() != old.Results.Count)
                throw new InvalidOperationException("Commit invents historical results");
        }
        internal static JournalMachineState? Read(SqliteConnection db, SqliteTransaction tx, VaultIdentity identity)
        {
            long id, head, checkpoint; int capacity, schema; string checkpointHash, currentHash, historyHash;
            using (var q = Query(db, tx, "SELECT id,epoch,capacity,head,checkpoint_seq,checkpoint_checksum,current_checksum,checksum_schema,history_checksum FROM vaults WHERE world=$w AND vault=$v;", ("$w", identity.World), ("$v", identity.Vault)))
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) return null;
                id = r.GetInt64(0);
                if (r.GetString(1) != identity.Epoch.ToString("N")) throw new InvalidDataException("Vault epoch mismatch");
                capacity = r.GetInt32(2); head = r.GetInt64(3); checkpoint = r.GetInt64(4);
                checkpointHash = r.GetString(5); currentHash = r.GetString(6); schema = r.GetInt32(7); historyHash = r.GetString(8);
            }
            var current = new VaultState(identity, capacity, Stacks(db, tx, "current_stacks", id));
            var snapshot = new JournalSnapshot(checkpoint, new VaultState(identity, capacity, Stacks(db, tx, "checkpoint_stacks", id)), checkpointHash, schema);
            if (currentHash != JournalChecksum.Compute(current)) throw new InvalidDataException("Materialized current checksum mismatch");
            var entries = new List<JournalEntry>(); var results = new List<CommandResult>(); var used = new List<Guid>();
            using (var q = Query(db, tx, "SELECT seq,body FROM journal WHERE vault_id=$id ORDER BY seq;", ("$id", id)))
            using (var r = q.ExecuteReader()) while (r.Read())
            {
                var entry = StorageEncoding.Decode((byte[])r[1], StorageEncoding.Entry);
                if (entry.Sequence != r.GetInt64(0)) throw new InvalidDataException("Journal row key mismatch");
                entries.Add(entry);
            }
            using (var q = Query(db, tx, "SELECT player,request_id,seq,body FROM request_results WHERE vault_id=$id ORDER BY seq;", ("$id", id)))
            using (var r = q.ExecuteReader()) while (r.Read())
            {
                var result = StorageEncoding.Decode((byte[])r[3], StorageEncoding.Result);
                if (result.Command.Address.Player != r.GetString(0) || result.Command.RequestId.ToString("N") != r.GetString(1) || result.Entry.Sequence != r.GetInt64(2))
                    throw new InvalidDataException("Request result row key mismatch");
                results.Add(result);
            }
            using (var q = Query(db, tx, "SELECT stack_id FROM used_stack_ids WHERE vault_id=$id;", ("$id", id)))
            using (var r = q.ExecuteReader()) while (r.Read()) used.Add(Guid.ParseExact(r.GetString(0), "N"));
            if (historyHash != HistoryChecksum(results, used)) throw new InvalidDataException("Request/stack identity history checksum mismatch");
            ReplicationState protocol;
            using (var q = Query(db, tx, "SELECT body,checksum FROM replication WHERE vault_id=$id;", ("$id", id)))
            using (var r = q.ExecuteReader())
            {
                if (!r.Read() || Hash((byte[])r[0]) != r.GetString(1)) throw new InvalidDataException("Missing/corrupt replication state");
                protocol = StorageEncoding.Decode((byte[])r[0], StorageEncoding.Replication);
            }
            return JournalMachineState.Recover(snapshot, current, head, entries, results, used, protocol);
        }
        private static IEnumerable<StackRecord> Stacks(SqliteConnection db, SqliteTransaction tx, string table, long id)
        {
            var stacks = new List<StackRecord>();
            using (var q = Query(db, tx, "SELECT stack_id,body FROM " + table + " WHERE vault_id=$id;", ("$id", id)))
            using (var r = q.ExecuteReader()) while (r.Read())
            {
                var stack = StorageEncoding.Decode((byte[])r[1], StorageEncoding.Stack);
                if (stack.Id.ToString("N") != r.GetString(0)) throw new InvalidDataException("Stack row key mismatch");
                stacks.Add(stack);
            }
            return stacks;
        }
        internal static long Write(SqliteConnection db, SqliteTransaction tx, JournalMachineState state, Action<StorageBoundary>? boundary)
        {
            var v = state.Current.Identity;
            Execute(db, tx, @"INSERT INTO vaults(world,vault,epoch,capacity,head,checkpoint_seq,checkpoint_checksum,current_checksum,checksum_schema,history_checksum)
 VALUES($w,$v,$e,$c,$h,$s,$ch,$hash,$schema,$history) ON CONFLICT(world,vault) DO UPDATE SET
 epoch=excluded.epoch,capacity=excluded.capacity,head=excluded.head,checkpoint_seq=excluded.checkpoint_seq,
 checkpoint_checksum=excluded.checkpoint_checksum,current_checksum=excluded.current_checksum,checksum_schema=excluded.checksum_schema,history_checksum=excluded.history_checksum;",
                ("$w", v.World), ("$v", v.Vault), ("$e", v.Epoch.ToString("N")), ("$c", state.Current.Capacity), ("$h", state.Head),
                ("$s", state.Checkpoint.Sequence), ("$ch", state.Checkpoint.Checksum), ("$hash", JournalChecksum.Compute(state.Current)), ("$schema", state.Checkpoint.Schema),
                ("$history", HistoryChecksum(state.Results, state.UsedStackIds)));
            long id = Convert.ToInt64(Scalar(db, tx, "SELECT id FROM vaults WHERE world=$w AND vault=$v;", ("$w", v.World), ("$v", v.Vault)));
            foreach (string table in new[] { "current_stacks", "checkpoint_stacks", "journal", "request_results", "used_stack_ids" })
                Execute(db, tx, "DELETE FROM " + table + " WHERE vault_id=$id;", ("$id", id));
            foreach (var s in state.Current.Stacks) InsertStack(db, tx, "current_stacks", id, s);
            boundary?.Invoke(StorageBoundary.AfterCurrentWrite);
            foreach (var s in state.Checkpoint.State.Stacks) InsertStack(db, tx, "checkpoint_stacks", id, s);
            foreach (var e in state.Journal)
                Execute(db, tx, "INSERT INTO journal VALUES($id,$s,$body);", ("$id", id), ("$s", e.Sequence), ("$body", StorageEncoding.Encode(e, StorageEncoding.Entry)));
            boundary?.Invoke(StorageBoundary.AfterJournalWrite);
            foreach (var r in state.Results)
                Execute(db, tx, "INSERT INTO request_results VALUES($id,$p,$r,$s,$body);", ("$id", id), ("$p", r.Command.Address.Player),
                    ("$r", r.Command.RequestId.ToString("N")), ("$s", r.Entry.Sequence), ("$body", StorageEncoding.Encode(r, StorageEncoding.Result)));
            foreach (var stack in state.UsedStackIds) Execute(db, tx, "INSERT INTO used_stack_ids VALUES($id,$stack);", ("$id", id), ("$stack", stack.ToString("N")));
            var protocol = StorageEncoding.Encode(state.Replication, StorageEncoding.Replication);
            Execute(db, tx, "INSERT INTO replication VALUES($id,$body,$hash) ON CONFLICT(vault_id) DO UPDATE SET body=excluded.body,checksum=excluded.checksum;",
                ("$id", id), ("$body", protocol), ("$hash", Hash(protocol)));
            return id;
        }
        private static void InsertStack(SqliteConnection db, SqliteTransaction tx, string table, long id, StackRecord stack) => Execute(db, tx,
            "INSERT INTO " + table + " VALUES($id,$stack,$body);", ("$id", id), ("$stack", stack.Id.ToString("N")), ("$body", StorageEncoding.Encode(stack, StorageEncoding.Stack)));
    }
}
