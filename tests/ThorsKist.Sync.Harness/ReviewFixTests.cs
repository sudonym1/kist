using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

static class ReviewFixTests
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        var id = new VaultIdentity("resize", "vault", Guid.NewGuid());
        var address = new SessionAddress(id.World, id.Vault, id.Epoch, "alice", Guid.NewGuid());
        ItemRecord Item(int n) => new ItemRecord("Wood", n, 110, new byte[] { 1, 2, 3 });
        var stack = new StackRecord(Guid.NewGuid(), 0, Item(48));
        var initial = new VaultState(id, 1, new[] { stack });
        var state = JournalMachineState.Create(initial);
        var deposit = new ItemCommand(address, Guid.NewGuid(), stack.Id, ItemCommandKind.Deposit, Item(2));
        var accepted = JournalMachine.Execute(state, deposit, 50);
        Check(accepted.Status == CommandStatus.Accepted && accepted.State.Current.Stacks.Single().Item.Quantity == 50,
            "Deposit fills compatible stack in an otherwise full vault");
        var concurrent = JournalMachine.Execute(accepted.State,
            new ItemCommand(address, Guid.NewGuid(), stack.Id, ItemCommandKind.Deposit, Item(2)), 50);
        Check(concurrent.Status == CommandStatus.CapacityExceeded && concurrent.State.Current.Stacks.Single().Item.Quantity == 50
            && concurrent.Result!.Entry.Effect == null, "Concurrent deposit cannot overfill or remove rejected character items");
        Check(JournalMachine.Execute(concurrent.State, deposit, 1).IsRetry, "Committed retry survives changed stack limit");
        var different = new ItemCommand(address, Guid.NewGuid(), stack.Id, ItemCommandKind.Deposit,
            new ItemRecord("Wood", 1, 110, new byte[] { 9 }));
        Check(JournalMachine.Execute(state, different, 50).Status == CommandStatus.StackConflict, "Merge cannot change metadata");

        string root = Path.Combine(Path.GetTempPath(), "thorskist-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "resize.db");
            var clock = new SystemReplicationClock();
            Task<JournalWorldRuntime> Open(int? capacity = null) => JournalWorldRuntime.OpenAsync(path, id.World, null,
                _ => Array.Empty<LegacyVaultImport>(), _ => new long[] { 1 }, clock, 8, capacity, _ => 50);
            var world = await Open();
            var overflow = new StackRecord(Guid.NewGuid(), -1, new ItemRecord("hash:missing", 7, 110, new byte[] { 255 }));
            var high = new StackRecord(Guid.NewGuid(), 3, Item(3));
            await world.CreateAsync(new VaultState(id, 4, new[] { stack, high, overflow }), new byte[] { 1 });
            await world.StopAsync();
            // Simulate a real v5 database, including the absent checkpoint column.
            using (var db = SqliteConnections.Open(path))
            {
                using var q = db.CreateCommand();
                q.CommandText = "ALTER TABLE vaults DROP COLUMN checkpoint_capacity; PRAGMA user_version=5;";
                q.ExecuteNonQuery();
            }
            world = await Open(2);
            var shrunk = await world.ReadAsync(id);
            Check(world.Vaults.Single().Capacity == 2 && shrunk.Current.Capacity == 2 && shrunk.Current.Stacks.Count == 3,
                "Configured shrink migrates v5 and preserves every stack");
            Check(shrunk.Current.Stacks.Count(s => s.Slot == -1) == 1
                && shrunk.Current.Stacks.Sum(s => s.Item.Quantity) == 58
                && shrunk.Current.Stacks.Single(s => s.Id == overflow.Id).Item.CopyPayload().SequenceEqual(new byte[] { 255 }),
                "Shrink retains opaque metadata and promotes available slots without loss");
            Check(shrunk.Checkpoint.State.Capacity == 2 && shrunk.Head == shrunk.Checkpoint.Sequence,
                "Startup finishes resize checkpoint before admitting peers");
            long head = shrunk.Head;
            await world.StopAsync();
            world = await Open(2);
            Check((await world.ReadAsync(id)).Head == head, "Unchanged capacity does not append another resize");
            await world.StopAsync();
            world = await Open(8);
            var grown = await world.ReadAsync(id);
            Check(grown.Current.Capacity == 8 && grown.Current.Stacks.All(s => s.Slot >= 0)
                && grown.Current.Stacks.Select(s => s.Id).OrderBy(x => x).SequenceEqual(new[] { stack.Id, high.Id, overflow.Id }.OrderBy(x => x)),
                "Configured growth restores overflow to normal slots without replacing identities");
            await world.StopAsync();

            // A failure at each write boundary must recover either the old or
            // complete resized state, even before the new checkpoint is written.
            foreach (var boundary in new[] { StorageBoundary.AfterCurrentWrite, StorageBoundary.AfterJournalWrite,
                StorageBoundary.BeforeCommit, StorageBoundary.AfterCommit })
            {
                string faultPath = Path.Combine(root, boundary + ".db");
                using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(faultPath, id), initial)) { }
                using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(faultPath, id,
                    point => { if (point == boundary) throw new IOException("injected resize failure"); }), initial))
                {
                    try { await writer.ResizeAsync(0); throw new Exception("Fault not reached"); }
                    catch (IOException) { checks++; }
                }
                using (var storage = new SqliteJournalStorage(faultPath, id))
                {
                    var recovered = (await storage.ReadStateAsync(default))!;
                    bool committed = boundary == StorageBoundary.AfterCommit;
                    Check(recovered.Current.Capacity == (committed ? 0 : 1) && recovered.Checkpoint.State.Capacity == 1
                        && recovered.Current.Stacks.Single().Item.Quantity == 48
                        && recovered.Current.Stacks.Single().Slot == (committed ? -1 : 0), "Resize is atomic at " + boundary);
                    if (committed)
                    {
                        var entry = recovered.Journal.Single();
                        var wire = JournalWire.DecodeEntries(JournalWire.EncodeEntries(address, new[] { entry })).Entries.Single();
                        Check(JournalMachineState.SameEntry(entry, wire), "Resize wire round trip retains capacity and checksum");
                    }
                }
                using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(faultPath, id), initial))
                {
                    var resized = await writer.ResizeAsync(0);
                    await writer.CheckpointAsync(resized.Head);
                }
                using (var storage = new SqliteJournalStorage(faultPath, id))
                    Check((await storage.ReadStateAsync(default))!.Checkpoint.State.Capacity == 0, "Resize checkpoint reopens after " + boundary);
            }
            // Active participants must never observe a unilateral startup resize.
            using var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(Path.Combine(root, "active.db"), id), initial, clock, 8);
            await server.LoginAsync("alice", Guid.NewGuid());
            try { await server.ResizeAsync(8); throw new Exception("Active resize admitted"); }
            catch (InvalidOperationException) { checks++; }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {checks} deposit bounds and durable capacity regression assertions");
        return checks;
    }
}
