using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using ThorsKist.Sync;

namespace ThorsKist.Sync.Sqlite
{
    public sealed partial class SqliteJournalStorage
    {
        // Constant-size optimistic concurrency check. Recovery verifies all row
        // bodies once; live commits require the exact recovered writer lineage.
        private sealed class CommitStamp
        {
            internal readonly long Id;
            internal readonly string CurrentHash, HistoryHash, Fingerprint;
            internal CommitStamp(long id, string current, string history, string fingerprint)
            { Id = id; CurrentHash = current; HistoryHash = history; Fingerprint = fingerprint; }
            internal bool Same(CommitStamp? other) => other != null && Fingerprint == other.Fingerprint;
        }
        private CommitStamp? ReadCommitStamp(SqliteConnection db, SqliteTransaction tx)
        {
            using (var q = Query(db, tx, @"SELECT v.id,v.epoch,v.capacity,v.head,v.checkpoint_seq,
v.checkpoint_checksum,v.current_checksum,v.checksum_schema,v.history_checksum,r.body,r.checksum
FROM vaults v LEFT JOIN replication r ON r.vault_id=v.id WHERE v.world=$w AND v.vault=$v;",
                ("$w", identity.World), ("$v", identity.Vault)))
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) return null;
                if (r.GetString(1) != identity.Epoch.ToString("N") || r.IsDBNull(9) || r.IsDBNull(10))
                    throw new InvalidDataException("Missing or changed durable vault identity/protocol");
                var parts = new string[11];
                for (int i = 0; i < parts.Length; i++) parts[i] = i == 9 ? Convert.ToBase64String((byte[])r[i])
                    : Convert.ToString(r[i], System.Globalization.CultureInfo.InvariantCulture)!;
                return new CommitStamp(r.GetInt64(0), r.GetString(6), r.GetString(8), string.Join("|", parts));
            }
        }
        private void WriteDelta(SqliteConnection db, SqliteTransaction tx, JournalMachineState previous, JournalMachineState next)
        {
            var stamp = ReadCommitStamp(db, tx) ?? throw new InvalidOperationException("Missing durable baseline");
            long id = stamp.Id;
            var added = next.Journal.Where(e => e.Sequence > previous.Head).ToArray();
            string currentHash = added.Length == 0 ? stamp.CurrentHash : added[added.Length - 1].Checksum;
            // Acknowledgments and delivery bookkeeping do not hash retained
            // results/identities again. The existing checksum format is retained.
            string historyHash = previous.Results.Count == next.Results.Count && previous.UsedStackIds.Count == next.UsedStackIds.Count
                ? stamp.HistoryHash : HistoryChecksum(next.Results, next.UsedStackIds);
            Execute(db, tx, @"UPDATE vaults SET head=$h,checkpoint_seq=$s,checkpoint_checksum=$ch,
current_checksum=$hash,checksum_schema=$schema,history_checksum=$history WHERE id=$id;",
                ("$h", next.Head), ("$s", next.Checkpoint.Sequence), ("$ch", next.Checkpoint.Checksum),
                ("$hash", currentHash), ("$schema", next.Checkpoint.Schema), ("$history", historyHash), ("$id", id));
            ApplyStackDeltas(db, tx, "current_stacks", id, added, "sqlite.current_stacks.delta");
            boundary?.Invoke(StorageBoundary.AfterCurrentWrite);
            if (next.Checkpoint.Sequence != previous.Checkpoint.Sequence)
            {
                var checkpointEntries = previous.Journal.Concat(added).Where(e => e.Sequence > previous.Checkpoint.Sequence
                    && e.Sequence <= next.Checkpoint.Sequence);
                ApplyStackDeltas(db, tx, "checkpoint_stacks", id, checkpointEntries, "sqlite.checkpoint_stacks.delta");
                using (JournalDiagnostics.Measure("sqlite.journal.prune"))
                    Execute(db, tx, "DELETE FROM journal WHERE vault_id=$id AND seq<=$cutoff;", ("$id", id), ("$cutoff", next.Checkpoint.Sequence));
            }
            using (JournalDiagnostics.Measure("sqlite.journal.append", count: added.Length))
                foreach (var e in added.Where(e => e.Sequence > next.Checkpoint.Sequence))
                    Execute(db, tx, "INSERT INTO journal VALUES($id,$s,$body);", ("$id", id), ("$s", e.Sequence), ("$body", StorageEncoding.Encode(e, StorageEncoding.Entry)));
            boundary?.Invoke(StorageBoundary.AfterJournalWrite);
            using (JournalDiagnostics.Measure("sqlite.results.append", count: next.Results.Count - previous.Results.Count))
                foreach (var r in next.Results.Where(r => r.Entry.Sequence > previous.Head))
                    Execute(db, tx, "INSERT INTO request_results VALUES($id,$p,$r,$s,$body);", ("$id", id), ("$p", r.Command.Address.Player),
                        ("$r", r.Command.RequestId.ToString("N")), ("$s", r.Entry.Sequence), ("$body", StorageEncoding.Encode(r, StorageEncoding.Result)));
            using (JournalDiagnostics.Measure("sqlite.used_ids.append", count: next.UsedStackIds.Count - previous.UsedStackIds.Count))
                foreach (var stack in next.UsedStackIds.Except(previous.UsedStackIds))
                    Execute(db, tx, "INSERT INTO used_stack_ids VALUES($id,$stack);", ("$id", id), ("$stack", stack.ToString("N")));
            using (JournalDiagnostics.Measure("sqlite.protocol.delta")) WriteProtocolDelta(db, tx, id, previous.Replication, next.Replication);
        }
        private static void ApplyStackDeltas(SqliteConnection db, SqliteTransaction tx, string table, long id,
            IEnumerable<JournalEntry> entries, string stage)
        {
            var changed = new Dictionary<Guid, StackRecord?>();
            foreach (var e in entries)
            {
                if (e.ChangedStack != null) changed[e.ChangedStack.Id] = e.ChangedStack;
                if (e.DeletedStack.HasValue) changed[e.DeletedStack.Value] = null;
            }
            using (JournalDiagnostics.Measure(stage, count: changed.Count))
                foreach (var row in changed)
                {
                    if (row.Value == null) Execute(db, tx, "DELETE FROM " + table + " WHERE vault_id=$id AND stack_id=$stack;", ("$id", id), ("$stack", row.Key.ToString("N")));
                    else Execute(db, tx, "INSERT INTO " + table + " VALUES($id,$stack,$body) ON CONFLICT(vault_id,stack_id) DO UPDATE SET body=excluded.body;",
                        ("$id", id), ("$stack", row.Key.ToString("N")), ("$body", StorageEncoding.Encode(row.Value, StorageEncoding.Stack)));
                }
        }
    }
}
