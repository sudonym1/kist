using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

static class StorageTests
{
    static int checks, kills;
    static readonly Guid Epoch = Guid.Parse("12345678-1234-1234-1234-123456789abc");
    static readonly VaultIdentity Vault = new("world", "hird", Epoch);
    static readonly SessionAddress Address = new("world", "hird", Epoch, "alice", Id(10));
    static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    static VaultState Empty(VaultIdentity? identity = null) => new(identity ?? Vault, 2, Array.Empty<StackRecord>());
    static ItemCommand Command(int request, int quantity, ItemCommandKind kind) => new(Address, Id(request), Id(1), kind, new ItemRecord("unknown-prefab", quantity, 109, new byte[] { 0, 128, 255 }));
    static readonly ItemCommand Deposit = Command(20, 10, ItemCommandKind.Deposit);
    static readonly ItemCommand Take = Command(21, 3, ItemCommandKind.Withdraw);
    static readonly ItemCommand More = Command(26, 3, ItemCommandKind.Deposit);
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("M3: " + message); }
    static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { checks++; return; } throw new Exception("M3: expected " + typeof(T).Name); }
    static async Task<JournalMachineState> Read(string path)
    {
        using var storage = new SqliteJournalStorage(path, Vault);
        return await storage.ReadStateAsync(default) ?? throw new Exception("Missing state");
    }
    static async Task Seed(string path, bool withTake = false)
    {
        using var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault), Empty());
        await writer.ExecuteAsync(Deposit);
        if (withTake) await writer.ExecuteAsync(Take);
    }
    static void Equivalent(JournalMachineState a, JournalMachineState b, string message)
    {
        Check(a.Head == b.Head && a.Checkpoint.Sequence == b.Checkpoint.Sequence
            && a.Checkpoint.Checksum == b.Checkpoint.Checksum && JournalChecksum.Compute(a.Current) == JournalChecksum.Compute(b.Current)
            && a.Results.Count == b.Results.Count && a.Journal.Count == b.Journal.Count
            && a.UsedStackIds.OrderBy(i => i).SequenceEqual(b.UsedStackIds.OrderBy(i => i))
            && a.Journal.Zip(b.Journal).All(pair => JournalMachineState.SameEntry(pair.First, pair.Second))
            && a.Results.Zip(b.Results).All(pair => pair.First.Status == pair.Second.Status
                && pair.First.Command.RequestId == pair.Second.Command.RequestId && pair.First.Command.StackId == pair.Second.Command.StackId
                && JournalChecksum.SameItem(pair.First.Command.Item, pair.Second.Command.Item)
                && JournalMachineState.SameEntry(pair.First.Entry, pair.Second.Entry)), message);
    }
    public static async Task<int> Run()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("THORSKIST_SYNC_ARTIFACTS") ?? Path.GetTempPath(), "thorskist-m3-" + Guid.NewGuid());
        Directory.CreateDirectory(root); bool passed = false;
        try
        {
            await RoundTrip(Path.Combine(root, "roundtrip.db"));
            await Failures(root);
            await Publication(root);
            await Corruption(root);
            await Migration(root);
            foreach (string operation in new[] { "initialize", "deposit", "withdraw", "checkpoint" })
                foreach (var point in new[] { StorageBoundary.AfterCurrentWrite, StorageBoundary.AfterJournalWrite, StorageBoundary.BeforeCommit, StorageBoundary.AfterCommit })
                    await Crash(root, operation, point);
            foreach (var point in new[] { StorageBoundary.AfterBackup, StorageBoundary.AfterCurrentWrite, StorageBoundary.AfterJournalWrite, StorageBoundary.BeforeMigrationCommit, StorageBoundary.AfterMigrationCommit })
                await Crash(root, "migration", point);
            Console.WriteLine($"PASS {checks} M3 SQLite assertions; {kills} forced child-process terminations (initialize/deposit/withdraw/checkpoint/migration)");
            passed = true;
            return checks;
        }
        finally
        {
            if (passed) Directory.Delete(root, true);
            else Console.Error.WriteLine("M3 failure databases and fixtures retained at " + root);
        }
    }
    static async Task RoundTrip(string path)
    {
        JournalMachineState before;
        using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault), Empty()))
        {
            var first = await writer.ExecuteAsync(Deposit);
            var retry = await writer.ExecuteAsync(Deposit);
            Check(retry.IsRetry && retry.Result!.Entry.OperationId == first.Result!.Entry.OperationId, "durable retry preserves operation");
            await writer.ExecuteAsync(Take);
            await writer.ExecuteAsync(Command(22, 100, ItemCommandKind.Withdraw));
            await writer.AppendNoOpAsync();
            before = await writer.ReadAsync();
        }
        Equivalent(before, await Read(path), "all records round trip");
        using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault), Empty()))
        {
            var retry = await writer.ExecuteAsync(Take);
            Check(retry.IsRetry && retry.Result!.Entry.Effect!.EffectId == before.Results[1].Entry.Effect!.EffectId, "effect identity survives reopen");
            Check((await writer.ExecuteAsync(Command(21, 4, ItemCommandKind.Withdraw))).Status == CommandStatus.RequestConflict, "altered retry rejected after reopen");
            await writer.CheckpointAsync(2); before = await writer.ReadAsync();
        }
        Equivalent(before, await Read(path), "partial checkpoint round trip");
        using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault), Empty()))
        {
            await writer.CheckpointAsync(4); var retry = await writer.ExecuteAsync(Deposit);
            Check(retry.IsRetry && (await writer.ReadAsync()).Current.Stacks.Single().Item.Quantity == 7, "pruning never forgets requests");
            await writer.ExecuteAsync(Command(23, 7, ItemCommandKind.Withdraw));
            await writer.CheckpointAsync(5);
            Check((await writer.ExecuteAsync(Command(24, 1, ItemCommandKind.Deposit))).Status == CommandStatus.StackConflict, "deleted stack ID cannot be reused after pruning");
        }
        using var other = new SqliteJournalStorage(path, new VaultIdentity("world", "other", Epoch));
        await other.CommitAsync(new StorageCommit(0, JournalMachineState.Create(Empty(new VaultIdentity("world", "other", Epoch)))), default);
        Check((await Read(path)).Head == 6 && (await other.ReadStateAsync(default))!.Head == 0, "vaults isolated in shared database");
        using var staleEpoch = new SqliteJournalStorage(path, new VaultIdentity("world", "hird", Id(999)));
        await Throws<InvalidDataException>(async () => await staleEpoch.ReadStateAsync(default));
        using var db = SqliteConnections.Open(path);
        using var q = db.CreateCommand(); q.CommandText = "PRAGMA integrity_check;";
        Check((string)q.ExecuteScalar()! == "ok", "production database integrity");
        q.CommandText = "PRAGMA journal_mode;"; Check((string)q.ExecuteScalar()! == "wal", "production WAL");
        q.CommandText = "PRAGMA synchronous;"; Check(Convert.ToInt32(q.ExecuteScalar()) == 2, "production FULL");
    }
    static async Task Failures(string root)
    {
        string path = Path.Combine(root, "fail.db"); await Seed(path); var original = await Read(path);
        foreach (var point in new[] { StorageBoundary.AfterCurrentWrite, StorageBoundary.AfterJournalWrite, StorageBoundary.BeforeCommit })
        {
            using var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault, p => { if (p == point) throw new IOException("injected write failure"); }), Empty());
            await Throws<IOException>(() => writer.ExecuteAsync(Take));
            await Throws<InvalidOperationException>(() => writer.ReadAsync());
            Equivalent(original, await Read(path), "failed write leaves no partial state at " + point);
        }
        using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault, p => { if (p == StorageBoundary.AfterCommit) throw new IOException("lost completion"); }), Empty()))
        { await Throws<IOException>(() => writer.ExecuteAsync(Take)); await Throws<InvalidOperationException>(() => writer.ExecuteAsync(Take)); }
        using (var recovered = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault), Empty()))
        {
            Check((await recovered.ExecuteAsync(Take)).IsRetry, "uncertain commit query returns committed result");
            Check((await recovered.ReadAsync()).Current.Stacks.Single().Item.Quantity == 7, "uncertain commit debits once");
        }
        using var storage = new SqliteJournalStorage(path, Vault);
        await Throws<InvalidOperationException>(() => storage.CommitAsync(new StorageCommit(1, JournalMachine.AppendNoOp(original)), default));
        var current = await Read(path);
        var checkpointed = JournalMachine.Checkpoint(current, current.Head);
        await storage.CommitAsync(new StorageCommit(current.Head, checkpointed), default);
        await Throws<InvalidOperationException>(() => storage.CommitAsync(new StorageCommit(current.Head, current), default));
        Equivalent(checkpointed, await Read(path), "same-head stale checkpoint cannot resurrect pruned history");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => storage.CommitAsync(new StorageCommit(current.Head, checkpointed), canceled.Token));
        // Concurrent writer requests contend for the last items in serialized order.
        string concurrent = Path.Combine(root, "concurrent.db"); await Seed(concurrent);
        using var serialized = await JournalWriter.OpenAsync(new SqliteJournalStorage(concurrent, Vault), Empty());
        var outcomes = await Task.WhenAll(Enumerable.Range(100, 20).Select(i => serialized.ExecuteAsync(Command(i, 1, ItemCommandKind.Withdraw))));
        Check(outcomes.Count(t => t.Status == CommandStatus.Accepted) == 10 && (await Read(concurrent)).Current.Stacks.Count == 0, "single worker order prevents concurrent overspend");
        // Separate connections with the same expected head cannot both commit.
        string contested = Path.Combine(root, "contested.db"); await Seed(contested);
        var old = await Read(contested); var left = JournalMachine.Execute(old, Take).State;
        var right = JournalMachine.Execute(old, Command(25, 1, ItemCommandKind.Withdraw)).State;
        using var a = new SqliteJournalStorage(contested, Vault); using var b = new SqliteJournalStorage(contested, Vault);
        async Task<bool> Try(SqliteJournalStorage s, JournalMachineState next)
        { try { await s.CommitAsync(new StorageCommit(old.Head, next), default); return true; } catch (InvalidOperationException) { return false; } }
        var successes = await Task.WhenAll(Try(a, left), Try(b, right));
        Check(successes.Count(x => x) == 1 && (await Read(contested)).Head == 2, "cross-connection stale heads rejected atomically");
    }
    static async Task Corruption(string root)
    {
        var corruptions = new[] {
            "UPDATE vaults SET current_checksum='broken';",
            "UPDATE vaults SET history_checksum='broken';",
            "UPDATE vaults SET checkpoint_checksum='broken';",
            "UPDATE vaults SET head=head+1;",
            "UPDATE vaults SET checksum_schema=999;",
            "DELETE FROM current_stacks;",
            "DELETE FROM journal WHERE seq=1;",
            "DELETE FROM request_results;",
            "DELETE FROM used_stack_ids;",
            "UPDATE journal SET body=X'0100000000';",
            "UPDATE request_results SET request_id='00000000000000000000000000000000';",
            "UPDATE current_stacks SET stack_id='00000000000000000000000000000000';",
            "PRAGMA user_version=999;"
        };
        for (int i = 0; i < corruptions.Length; i++)
        {
            string path = Path.Combine(root, "corrupt" + i + ".db"); await Seed(path);
            using (var db = SqliteConnections.Open(path)) { using var q = db.CreateCommand(); q.CommandText = corruptions[i]; q.ExecuteNonQuery(); }
            // Some malformed envelopes fail at the binary reader/constructor.
            await Throws<Exception>(async () => await Read(path));
        }
        string archived = Path.Combine(root, "archived-corrupt.db"); await Seed(archived, true);
        using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(archived, Vault), Empty())) await writer.CheckpointAsync(2);
        using (var db = SqliteConnections.Open(archived)) { using var q = db.CreateCommand(); q.CommandText = "DELETE FROM request_results WHERE seq=1;"; q.ExecuteNonQuery(); }
        await Throws<InvalidDataException>(async () => await Read(archived));
        string deletedId = Path.Combine(root, "deleted-id-corrupt.db"); await Seed(deletedId);
        using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(deletedId, Vault), Empty()))
        { await writer.ExecuteAsync(Command(23, 10, ItemCommandKind.Withdraw)); await writer.CheckpointAsync(2); }
        using (var db = SqliteConnections.Open(deletedId)) { using var q = db.CreateCommand(); q.CommandText = "DELETE FROM used_stack_ids;"; q.ExecuteNonQuery(); }
        await Throws<InvalidDataException>(async () => await Read(deletedId));
    }
    static async Task Publication(string root)
    {
        string path = Path.Combine(root, "publication.db"); await Seed(path); var old = await Read(path);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource();
        using var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault, point =>
        {
            if (point != StorageBoundary.BeforeCommit) return;
            entered.SetResult(SynchronizationContext.Current == null);
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Commit gate timed out");
        }), Empty());
        var pending = writer.ExecuteAsync(Take, cancellation.Token);
        try
        {
            Check(await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)), "database work runs outside caller synchronization context");
            Check(!pending.IsCompleted, "result cannot be published before commit");
            Equivalent(old, await Read(path).WaitAsync(TimeSpan.FromSeconds(5)), "WAL reader sees previous complete state during write");
            cancellation.Cancel(); // Too late to report a committed transaction as canceled.
        }
        finally { release.Set(); }
        var completed = await pending;
        Check(completed.Result!.Entry.Sequence == 2 && (await writer.ReadAsync()).Head == 2, "install and result follow durable commit despite late cancellation");
        Equivalent(completed.State, await Read(path), "published state is durable");
    }
    static readonly byte[] Source = { 1, 0, 0, 0, 2, 0, 0, 0, 255, 0, 128, 4 };
    // Migration fixture adapter, deliberately not a claim of vanilla decoding.
    // It preserves metadata and raw unknown records; M5 supplies real decoding.
    static IReadOnlyList<LegacyVaultImport> Decode(byte[] bytes)
    {
        if (!bytes.SequenceEqual(Source)) throw new InvalidDataException("Bad legacy fixture");
        return new[] {
            new LegacyVaultImport(new VaultState(Vault, 1, new[] { new StackRecord(Id(1), 0, Deposit.Item), new StackRecord(Id(2), -1, Deposit.Item) }), new byte[] { 10, 255 }),
            new LegacyVaultImport(Empty(new VaultIdentity("world", "second-hird", Epoch)), new byte[] { 42 })
        };
    }
    static async Task Migration(string root)
    {
        string path = Path.Combine(root, "migration.db"), source = Path.Combine(root, "legacy.dat"); File.WriteAllBytes(source, Source);
        await Throws<InvalidDataException>(() => LegacySaveMigration.ImportAsync(path, source, _ => throw new InvalidDataException("decode failed")));
        using (var storage = new SqliteJournalStorage(path, Vault)) Check(await storage.ReadStateAsync(default) == null, "decoder failure leaves no vault");
        await LegacySaveMigration.ImportAsync(path, source, Decode);
        var state = await Read(path);
        var catalog = await SqliteJournalStorage.ListVaultsAsync(path);
        Check(catalog.Count == 2 && catalog.All(v => v.Epoch == Epoch), "bootstrap discovers imported vault epochs");
        using (var storage = new SqliteJournalStorage(path, Vault))
            Check((await storage.ReadLegacyMetadataAsync())!.SequenceEqual(new byte[] { 10, 255 }), "metadata recovery API preserves hird header");
        Check(state.Current.Stacks.Count == 2 && state.Current.Stacks.Single(s => s.Slot == -1).Item.CopyPayload().SequenceEqual(Deposit.Item.CopyPayload()), "unknown payload and overflow survive import");
        Check(File.ReadAllBytes(source).SequenceEqual(Source) && Directory.GetFiles(root, "legacy.dat.journal-backup-*").Length == 1, "original and one backup preserved");
        await LegacySaveMigration.ImportAsync(path, source, _ => throw new Exception("retry must not decode"));
        Equivalent(state, await Read(path), "migration marker prevents repeated import");
        using (var db = SqliteConnections.Open(path))
        {
            using var q = db.CreateCommand(); q.CommandText = "SELECT count(*) FROM vaults;"; Check(Convert.ToInt32(q.ExecuteScalar()) == 2, "all vaults imported atomically");
            q.CommandText = "SELECT source FROM migration;"; Check(((byte[])q.ExecuteScalar()!).SequenceEqual(Source), "raw legacy source retained in database");
            q.CommandText = "SELECT body FROM legacy_metadata ORDER BY vault_id;"; Check(((byte[])q.ExecuteScalar()!).SequenceEqual(new byte[] { 10, 255 }), "hird metadata retained exactly");
        }
        File.WriteAllBytes(source, new byte[] { 9 });
        await Throws<InvalidDataException>(() => LegacySaveMigration.ImportAsync(path, source, Decode));
        Equivalent(state, await Read(path), "changed source cannot replace imported history");
        string nonempty = Path.Combine(root, "nonempty.db"); await Seed(nonempty);
        File.WriteAllBytes(source, Source);
        await Throws<InvalidOperationException>(() => LegacySaveMigration.ImportAsync(nonempty, source, Decode));
        string badBackupSource = Path.Combine(root, "badbackup.dat"); File.WriteAllBytes(badBackupSource, Source);
        string backup = Directory.GetFiles(root, "legacy.dat.journal-backup-*").Single(p => File.ReadAllBytes(p).SequenceEqual(Source));
        File.WriteAllBytes(badBackupSource + backup.Substring(source.Length), new byte[] { 0 });
        await Throws<InvalidDataException>(() => LegacySaveMigration.ImportAsync(Path.Combine(root, "badbackup.db"), badBackupSource, Decode));
    }
    static async Task Crash(string root, string operation, StorageBoundary boundary)
    {
        string directory = Path.Combine(root, operation + "-" + boundary); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state.db");
        if (operation == "deposit" || operation == "withdraw" || operation == "checkpoint") await Seed(path, operation == "checkpoint");
        if (operation == "migration") File.WriteAllBytes(Path.Combine(directory, "legacy.dat"), Source);
        JournalMachineState? before = operation == "initialize" || operation == "migration" ? null : await Read(path);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { "--m3-child", operation, boundary.ToString(), path }) start.ArgumentList.Add(arg);
        using (var child = Process.Start(start) ?? throw new Exception("Child did not start"))
        {
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                string? signal = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                File.WriteAllText(Path.Combine(directory, "crash.txt"), operation + "/" + boundary + ": " + signal);
                if (signal != "M3-BOUNDARY " + boundary)
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    throw new Exception("Unexpected child signal: " + signal + " " + await errors);
                }
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); kills++;
                Check(child.ExitCode != 0, "child forcibly terminated at " + boundary);
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        }
        bool committed = boundary == StorageBoundary.AfterCommit || boundary == StorageBoundary.AfterMigrationCommit;
        if (operation == "migration")
        {
            using (var storage = new SqliteJournalStorage(path, Vault)) Check((await storage.ReadStateAsync(default) != null) == committed, "migration crash atomic visibility");
            Check(File.ReadAllBytes(Path.Combine(directory, "legacy.dat")).SequenceEqual(Source), "migration crash preserves source");
            await LegacySaveMigration.ImportAsync(path, Path.Combine(directory, "legacy.dat"), Decode);
            Check((await Read(path)).Current.Stacks.Count == 2, "migration recovers once after crash");
            using var db = SqliteConnections.Open(path); using var q = db.CreateCommand(); q.CommandText = "SELECT count(*) FROM vaults;";
            Check(Convert.ToInt32(q.ExecuteScalar()) == 2, "no partial or duplicate vault import after crash");
        }
        else if (operation == "initialize")
        {
            using var storage = new SqliteJournalStorage(path, Vault);
            Check((await storage.ReadStateAsync(default) != null) == committed, "initialization atomic visibility");
        }
        else
        {
            var after = await Read(path);
            if (!committed) Equivalent(before!, after, operation + " rolls back at " + boundary);
            else if (operation == "checkpoint")
                Equivalent(JournalMachine.Checkpoint(before!, before!.Head), after, "checkpoint crash preserves current and request identities");
            else Check(after.Head == 2 && after.Current.Stacks.Single().Item.Quantity == (operation == "deposit" ? 13 : 7) && after.Results.Count == 2, "commit before publication survives crash");
            using var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault), Empty());
            var target = operation == "deposit" ? More : Take;
            var retry = await writer.ExecuteAsync(target);
            Check(retry.IsRetry == (operation == "checkpoint" || committed), "uncertain result query after " + operation + "/" + boundary);
            Check((await writer.ReadAsync()).Head == 2 && (await writer.ReadAsync()).Current.Stacks.Single().Item.Quantity == (operation == "deposit" ? 13 : 7), "server credit/debit not repeated after crash");
            var again = await writer.ExecuteAsync(target);
            Check(again.IsRetry && again.Result!.Entry.OperationId == retry.Result!.Entry.OperationId
                && again.Result.Entry.Effect!.EffectId == retry.Result.Entry.Effect!.EffectId, "stable operation/effect after crash retry");
        }
        Console.WriteLine("M3 crash recovered: " + operation + "/" + boundary);
    }
    public static async Task Child(string[] args)
    {
        string operation = args[1]; var target = Enum.Parse<StorageBoundary>(args[2]); string path = args[3];
        void Boundary(StorageBoundary point)
        {
            if (point != target) return;
            Console.WriteLine("M3-BOUNDARY " + point); Console.Out.Flush();
            using var never = new ManualResetEventSlim(false); never.Wait();
        }
        if (operation == "migration")
            await LegacySaveMigration.ImportAsync(path, Path.Combine(Path.GetDirectoryName(path)!, "legacy.dat"), Decode, boundary: Boundary);
        else
        {
            using var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, Vault, Boundary), Empty());
            if (operation == "withdraw") await writer.ExecuteAsync(Take);
            if (operation == "deposit") await writer.ExecuteAsync(More);
            if (operation == "checkpoint") await writer.CheckpointAsync(2);
        }
        throw new Exception("Child failed to reach boundary");
    }
}
