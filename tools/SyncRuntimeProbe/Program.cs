using System;
using System.IO;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

// Run the unmodified build output on .NET Framework (Windows) or Mono (Linux).
// A modern .NET harness alone cannot establish compatibility with Valheim.
static class Program
{
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error)
        {
            Console.Error.WriteLine("SQLite runtime probe failed: " + error);
            return 1;
        }
    }
    static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "thorskist-runtime-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using (var db = SqliteConnections.Open(Path.Combine(directory, "probe.db")))
            using (var query = db.CreateCommand())
            {
                query.CommandText = "SELECT sqlite_version();";
                Console.WriteLine("net472 provider loaded: SQLite " + query.ExecuteScalar() + ", CLR " + Environment.Version);
            }
            var identity = new VaultIdentity("probe-world", "probe-vault", Guid.NewGuid());
            var initial = new VaultState(identity, 1, Array.Empty<StackRecord>());
            var address = new SessionAddress(identity.World, identity.Vault, identity.Epoch, "probe-player", Guid.NewGuid());
            var command = new ItemCommand(address, Guid.NewGuid(), Guid.NewGuid(), ItemCommandKind.Deposit,
                new ItemRecord("unknown-prefab", 3, 109, new byte[] { 0, 128, 255 }));
            string journalPath = Path.Combine(directory, "journal.db");
            Guid operation;
            using (var writer = JournalWriter.OpenAsync(new SqliteJournalStorage(journalPath, identity), initial).GetAwaiter().GetResult())
            {
                operation = writer.ExecuteAsync(command).GetAwaiter().GetResult().Result.Entry.OperationId;
                writer.CheckpointAsync(1).GetAwaiter().GetResult();
            }
            using (var writer = JournalWriter.OpenAsync(new SqliteJournalStorage(journalPath, identity), initial).GetAwaiter().GetResult())
            {
                var retry = writer.ExecuteAsync(command).GetAwaiter().GetResult();
                if (!retry.IsRetry || retry.Result.Entry.OperationId != operation || retry.State.Head != 1 || retry.State.Current.Stacks[0].Item.Quantity != 3)
                    throw new Exception("Journal recovery/deduplication failed");
                Console.WriteLine("net472 M3 journal commit/checkpoint/reopen/retry passed");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
