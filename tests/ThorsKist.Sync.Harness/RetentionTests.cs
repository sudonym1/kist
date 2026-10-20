using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

static class RetentionTests
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    public static async Task<int> Run()
    {
        checks = 0;
        var directory = Path.Combine(Path.GetTempPath(), "thorskist-retention-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try { await Closed(Path.Combine(directory, "closed.db")); await Large(Path.Combine(directory, "large.db")); }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"PASS {checks} M7 session retention/schema migration assertions"); return checks;
    }
    static void Sql(string path, string sql, byte[]? body = null, string? hash = null)
    {
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
        using var q = db.CreateCommand(); q.CommandText = sql;
        if (body != null) q.Parameters.AddWithValue("$body", body);
        if (hash != null) q.Parameters.AddWithValue("$hash", hash);
        q.ExecuteNonQuery();
    }
    static byte[] LegacyProtocol(ReplicationState state)
    {
        var type = typeof(SqliteJournalStorage).Assembly.GetType("ThorsKist.Sync.Sqlite.StorageEncoding")!;
        var write = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == "Replication" && m.GetParameters().Length == 2);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); write.Invoke(null, new object[] { writer, state }); writer.Flush(); return stream.ToArray();
    }
    static async Task Closed(string path)
    {
        var v = new VaultIdentity("retention", "closed", Guid.NewGuid());
        var initial = new VaultState(v, 1, new[] { new StackRecord(Guid.NewGuid(), 0, new ItemRecord("Wood", 10, 110, new byte[256 * 1024])) });
        var request = Guid.NewGuid(); SessionAddress address; ReplicationState protocol;
        using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, v), initial, new SystemReplicationClock(), 2))
        {
            var login = await server.LoginAsync("account", request); address = login.Session!.Address;
            await server.AcknowledgeAsync(address, login.Session.LoginMarker);
            await server.ExcludeAsync(address, false);
            var state = await server.ReadAsync(); var closed = state.Replication.Sessions.Single(); protocol = state.Replication;
            Check(closed.LoginImageReleased && closed.Baseline.State.Stacks.Count == 0 && closed.Catchup.Count == 0, "Closed session releases login payload");
            Check(closed.Address.Session == address.Session && closed.LoginRequest == request, "Closed session retains immutable retry identity");
            Check(LegacyProtocol(protocol).Length < 2048, "Closed session tombstone size is independent of vault payload");
            var oldClosed = new ReplicationSession(closed.Address, closed.LoginRequest, login.Session.Baseline, login.Session.Catchup,
                closed.LoginMarker, SessionPhase.Closed, closed.Acknowledged, closed.QueuedThrough, closed.LogoutRequest,
                closed.LogoutMarker, closed.BarrierDue, closed.SuspendedUntil, Array.Empty<DeliveryDeadline>());
            protocol = new ReplicationState(protocol.Revision, protocol.NextHeartbeat, new[] { oldClosed }, protocol.Settlements);
            Check(LegacyProtocol(protocol).Length > 256 * 1024, "Upgrade fixture contains a historical unreleased login image");
        }
        // Recreate a pre-normalization schema to exercise the transactional v4 upgrade.
        var body = LegacyProtocol(protocol); var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        Sql(path, "UPDATE replication SET body=$body,checksum=$hash; DROP TABLE replication_sessions; DROP TABLE replication_settlements; PRAGMA user_version=4;", body, hash);
        using (var storage = new SqliteJournalStorage(path, v))
        {
            var state = (await storage.ReadStateAsync(default))!;
            Check(state.Replication.Sessions.Single().LoginImageReleased && state.Replication.Sessions.Single().Address.Session == address.Session, "v4 migration preserves closed retry tombstone");
        }
        using (var server = await ReplicationServer.OpenAsync(new SqliteJournalStorage(path, v), initial, new SystemReplicationClock(), 2))
        {
            var retry = await server.LoginAsync("account", request);
            Check(retry.Session!.Phase == SessionPhase.Closed && retry.Session.Address.Session == address.Session, "Reopen old login retry stays closed");
            var next = await server.LoginAsync("account", Guid.NewGuid());
            Check(next.Session!.Address.Session != address.Session && !next.Session.LoginImageReleased, "Fresh login retains its complete immutable image");
        }
        Sql(path, "DELETE FROM replication_sessions WHERE session_id='" + address.Session.ToString("N") + "';");
        using var broken = new SqliteJournalStorage(path, v);
        try { await broken.ReadStateAsync(default); throw new Exception("Missing closed-session row accepted"); }
        catch (InvalidDataException) { checks++; }
    }
    static async Task Large(string path)
    {
        var v = new VaultIdentity("retention", "large", Guid.NewGuid());
        var initial = new VaultState(v, 4, Enumerable.Range(0, 4).Select(i => new StackRecord(Guid.NewGuid(), i, new ItemRecord("opaque", 1, 110, new byte[256 * 1024]))));
        var baseline = new JournalSnapshot(0, initial, JournalChecksum.Compute(initial));
        var entries = new List<JournalEntry>(); var sessions = new List<ReplicationSession>();
        var due = DateTimeOffset.UtcNow.AddSeconds(30);
        for (int i = 1; i <= 65; i++)
        {
            var a = new SessionAddress(v.World, v.Vault, v.Epoch, "account" + i, Guid.NewGuid()); var request = Guid.NewGuid();
            entries.Add(new JournalEntry(v, i, Guid.NewGuid(), request, JournalEntryKind.Login, a, null, null, null, baseline.Checksum));
            sessions.Add(new ReplicationSession(a, request, baseline, entries, i, SessionPhase.LoggingIn, 0, i, Guid.Empty, 0, due, null,
                entries.Select(e => new DeliveryDeadline(e.Sequence, due))));
        }
        var state = JournalMachineState.Recover(baseline, initial, entries.Count, entries, Array.Empty<CommandResult>(), initial.Stacks.Select(s => s.Id),
            new ReplicationState(1, DateTimeOffset.MinValue, sessions, Array.Empty<EffectSettlement>()));
        using (var storage = new SqliteJournalStorage(path, v)) await storage.CommitAsync(new StorageCommit(0, state), default);
        using (var storage = new SqliteJournalStorage(path, v))
        {
            var read = (await storage.ReadStateAsync(default))!;
            Check(read.Replication.Sessions.Count == 65 && read.Replication.Sessions.All(s => s.Baseline.State.Stacks.Count == 4), "More than 64 MiB of immutable login images persists across independent bounded rows");
            Check(read.Replication.Sessions.Select(s => s.LoginRequest).ToHashSet().SetEquals(sessions.Select(s => s.LoginRequest)), "Large session history preserves every retry key");
        }
    }
}
