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
            string sessionsPath = Path.Combine(directory, "sessions.db");
            using (var server = ReplicationServer.OpenAsync(new SqliteJournalStorage(sessionsPath, identity), initial, new SystemReplicationClock(), 1).GetAwaiter().GetResult())
            {
                var login = server.LoginAsync("probe-player", Guid.NewGuid()).GetAwaiter().GetResult();
                var session = login.Session;
                server.AcknowledgeAsync(session.Address, session.LoginMarker).GetAwaiter().GetResult();
                var deposit = new ItemCommand(session.Address, Guid.NewGuid(), Guid.NewGuid(), ItemCommandKind.Deposit, command.Item);
                server.ExecuteAsync(deposit).GetAwaiter().GetResult();
                server.QueueDeliveryAsync(session.Address, session.LoginMarker, 4).GetAwaiter().GetResult();
                var head = server.ReadAsync().GetAwaiter().GetResult().Head;
                server.AcknowledgeAsync(session.Address, head).GetAwaiter().GetResult();
                var take = new ItemCommand(session.Address, Guid.NewGuid(), deposit.StackId, ItemCommandKind.Withdraw, command.Item);
                server.ExecuteAsync(take).GetAwaiter().GetResult();
                server.ExcludeAsync(session.Address, true).GetAwaiter().GetResult();
                var state = server.CheckpointAsync().GetAwaiter().GetResult();
                if (state.Current.Stacks.Count != 1 || state.Current.Stacks[0].Slot != -1 || state.Current.Stacks[0].Item.Quantity != 3
                    || state.Replication.Settlements.Count != 2 || state.Checkpoint.Sequence != state.Head)
                    throw new Exception("Session compensation/checkpoint failed");
            }
            using (var server = ReplicationServer.OpenAsync(new SqliteJournalStorage(sessionsPath, identity), initial, new SystemReplicationClock(), 1).GetAwaiter().GetResult())
                if (server.LoginAsync("probe-player", Guid.NewGuid()).GetAwaiter().GetResult().Status != SessionStatus.Suspended)
                    throw new Exception("Session cooldown recovery failed");
            Console.WriteLine("net472 M4 login/ack/admission/overflow/settlement/checkpoint/cooldown reopen passed");
        }
        finally { Directory.Delete(directory, true); }
    }
}
