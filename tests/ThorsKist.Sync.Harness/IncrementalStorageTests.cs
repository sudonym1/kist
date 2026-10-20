using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

static class IncrementalStorageTests
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        var root = Path.Combine(Path.GetTempPath(), "thorskist-delta-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "delta.db");
            var identity = new VaultIdentity("delta", "vault", Guid.NewGuid());
            var stacks = Enumerable.Range(0, 2000).Select(i => new StackRecord(Guid.NewGuid(), i, new ItemRecord("Wood", 10, 110, new byte[128]))).ToArray();
            var initial = new VaultState(identity, 2048, stacks);
            var address = new SessionAddress(identity.World, identity.Vault, identity.Epoch, "alice", Guid.NewGuid());
            using var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, identity), initial);
            using var db = SqliteConnections.Open(path);
            void Sql(string sql) { using var q = db.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
            long Count(string table, string? operation = null)
            {
                using var q = db.CreateCommand(); q.CommandText = "SELECT count(*) FROM audit WHERE tab=$tab" + (operation == null ? "" : " AND op=$op");
                q.Parameters.AddWithValue("$tab", table); if (operation != null) q.Parameters.AddWithValue("$op", operation);
                return Convert.ToInt64(q.ExecuteScalar());
            }
            Sql("CREATE TABLE audit(tab TEXT,op TEXT);");
            foreach (string table in new[] { "current_stacks", "checkpoint_stacks", "journal", "request_results", "used_stack_ids", "replication_sessions", "replication_settlements" })
                foreach (string op in new[] { "INSERT", "UPDATE", "DELETE" })
                    Sql($"CREATE TRIGGER audit_{table}_{op} AFTER {op} ON {table} BEGIN INSERT INTO audit VALUES('{table}','{op}'); END;");
            var command = new ItemCommand(address, Guid.NewGuid(), stacks[0].Id, ItemCommandKind.Withdraw, new ItemRecord("Wood", 3, 110, new byte[128]));
            var trace = JournalDiagnostics.StartCapture(Environment.CurrentManagedThreadId);
            try
            {
                await writer.ExecuteAsync(command);
                Check(Count("current_stacks") == 1 && Count("current_stacks", "UPDATE") == 1, "Withdrawal updates one of 2000 stack rows exactly once");
                Check(Count("checkpoint_stacks") == 0 && Count("used_stack_ids") == 0, "Transfer does not rewrite checkpoint or retained stack IDs");
                Check(Count("journal", "INSERT") == 1 && Count("request_results", "INSERT") == 1, "Transfer appends only its journal entry and result");
                Sql("DELETE FROM audit;"); await writer.AppendNoOpAsync();
                Check(Count("current_stacks") == 0 && Count("checkpoint_stacks") == 0 && Count("request_results") == 0 && Count("used_stack_ids") == 0,
                    "Heartbeat touches no stack, checkpoint, result or used-ID rows");
                Sql("DELETE FROM audit;"); await writer.CheckpointAsync(2);
                Check(Count("current_stacks") == 0 && Count("checkpoint_stacks") == 1 && Count("checkpoint_stacks", "UPDATE") == 1,
                    "Checkpoint applies only changed stack since prior cutoff");
                Check(Count("journal", "DELETE") == 2 && Count("request_results") == 0 && Count("used_stack_ids") == 0,
                    "Checkpoint prunes journal prefix without rewriting retry history");
                Sql("DELETE FROM audit;"); var retry = await writer.ExecuteAsync(command);
                Check(retry.IsRetry && Count("current_stacks") == 0 && Count("journal") == 0 && Count("request_results") == 0, "Retry after pruning performs no row writes");
                var second = new ItemCommand(address, Guid.NewGuid(), stacks[0].Id, ItemCommandKind.Withdraw, new ItemRecord("Wood", 7, 110, new byte[128]));
                await writer.ExecuteAsync(second);
                Check(Count("current_stacks", "DELETE") == 1 && Count("checkpoint_stacks") == 0, "Full-stack withdrawal deletes only its exact current row");
            }
            finally { JournalDiagnostics.StopCapture(); }
            Check(!trace.Snapshot().Any(s => s.Stage == "sqlite.full_recovery" || s.Stage == "sqlite.previous.read_verify" || s.Stage == "sqlite.candidate.validate" || s.Stage == "sqlite.successor.validate"),
                "Live transfers, heartbeat and checkpoint do not recover or validate a full snapshot");
            using var reopened = new SqliteJournalStorage(path, identity);
            var recovered = (await reopened.ReadStateAsync(default))!;
            Check(recovered.Head == 3 && recovered.Checkpoint.Sequence == 2 && recovered.Current.Stacks.Count == 1999
                && recovered.Results.Count == 2 && recovered.UsedStackIds.Count == 2000, "Full recovery verifies incremental commits and retained retry identities");
            // A second writer with the same recovered head must fail rather than
            // install deltas over a different writer's committed baseline.
            using var stale = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, identity), initial);
            await writer.AppendNoOpAsync();
            try { await stale.AppendNoOpAsync(); throw new Exception("Stale live writer overwrote newer history"); }
            catch (InvalidOperationException) { checks++; }
            using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, identity), initial, new SystemReplicationClock(), 8))
            {
                var alice = (await server.LoginAsync("alice", Guid.NewGuid())).Session!;
                await server.AcknowledgeAsync(alice.Address, alice.LoginMarker);
                var bob = (await server.LoginAsync("bob", Guid.NewGuid())).Session!;
                Sql("DELETE FROM audit;");
                await server.AcknowledgeAsync(bob.Address, bob.LoginMarker);
                Check(Count("current_stacks") == 0 && Count("checkpoint_stacks") == 0 && Count("journal") == 0
                    && Count("request_results") == 0 && Count("used_stack_ids") == 0,
                    "Acknowledgment writes no vault stacks, journal entries or deduplication history");
                Check(Count("replication_sessions", "UPDATE") == 1 && Count("replication_sessions", "DELETE") == 0
                    && Count("replication_settlements") == 0, "Acknowledgment updates only its own session; other peer remains untouched");
            }
            Console.WriteLine($"PASS {checks} incremental SQLite row-audit and recovery assertions"); return checks;
        }
        finally { Directory.Delete(root, true); }
    }
}
