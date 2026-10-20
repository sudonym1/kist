using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

static class WorldRuntimeTests
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static IReadOnlyCollection<long> Members(byte[] header) => header.Select(b => (long)b).ToArray();
    static async Task<byte[]> Next(JournalSendQueue queue)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true) { var bytes = queue.Dequeue(); if (bytes != null) return bytes; if (DateTime.UtcNow > until) throw new TimeoutException("World transfer stalled"); await Task.Delay(1); }
    }
    public static async Task<int> Run()
    {
        checks = 0; var directory = Path.Combine(Path.GetTempPath(), "thorskist-m7-world-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "world.db"); var clock = new SystemReplicationClock();
            var world = await JournalWorldRuntime.OpenAsync(path, "world", null, _ => Array.Empty<LegacyVaultImport>(), Members, clock, 4);
            var id = new VaultIdentity("world", "hird", Guid.NewGuid()); var stack = Guid.NewGuid();
            var item = new ItemRecord("Wood", 4, 110, Array.Empty<byte>());
            try
            {
                Check(world.Vaults.Count == 0, "Empty persistent world opens without a placeholder hird");
                var hird = await world.CreateAsync(new VaultState(id, 2, new[] { new StackRecord(stack, 0, item) }), new byte[] { 1, 2 });
                Check(hird.IsMember(1) && hird.IsMember(2) && world.Vaults.Count == 1, "Created catalog membership is authoritative");
                var deniedPeer = new JournalPeerIdentity(new object(), "outsider", 3, id); var deniedBinding = new JournalPeerBinding(deniedPeer);
                using var deniedQueue = new JournalSendQueue();
                try { await world.ReceiveAsync(deniedPeer, deniedBinding, deniedQueue, new JournalFrame(JournalMessage.Login, Guid.NewGuid(), JournalWire.EncodeLogin(id))); throw new Exception("Nonmember login admitted"); }
                catch (InvalidDataException) { checks++; }
                Check((await world.ReadAsync(id)).Head == 0, "Nonmember traffic cannot allocate a login marker");
                var peer = new JournalPeerIdentity(new object(), "member-two", 2, id); var binding = new JournalPeerBinding(peer);
                using var outgoing = new JournalSendQueue();
                await world.ReceiveAsync(peer, binding, outgoing, new JournalFrame(JournalMessage.Login, Guid.NewGuid(), JournalWire.EncodeLogin(id)));
                var reply = JournalReply.Decode(JournalWire.DecodeFrame(await Next(outgoing)).CopyBody());
                using var receiver = new JournalTransferReceiver(new JournalTransferBudget(), clock);
                var image = receiver.Accept(JournalWire.DecodeFrame(await Next(outgoing)))!; var login = JournalWire.DecodeLoginImage(image);
                var address = login.Session!.Address;
                await world.ReceiveAsync(peer, binding, outgoing, new JournalFrame(JournalMessage.Acknowledge, Guid.NewGuid(), JournalWire.EncodePosition(address, login.Session.LoginMarker)));
                Check(JournalReply.Decode(JournalWire.DecodeFrame(await Next(outgoing)).CopyBody()).Phase == SessionPhase.Active, "World owner routes login to recovered vault lifetime");
                var take = new ItemCommand(address, Guid.NewGuid(), stack, ItemCommandKind.Withdraw, new ItemRecord("Wood", 1, 110, Array.Empty<byte>()));
                await world.ReceiveAsync(peer, binding, outgoing, new JournalFrame(JournalMessage.Command, take.RequestId, JournalWire.EncodeCommand(take)));
                Check((await world.ReadAsync(id)).Current.Stacks.Sum(s => s.Item.Quantity) == 3, "Admitted world withdrawal commits before observer ack");
                Check(await world.UpdateMetadataAsync(id, hird.CopyHeader(), new byte[] { 1 }), "Hird departure commits metadata after exclusion settlement");
                var settled = await world.ReadAsync(id);
                Check(binding.IsClosed && settled.Current.Stacks.Sum(s => s.Item.Quantity) == 4 && settled.Replication.Sessions.Single().Phase == SessionPhase.Closed,
                    "Removed member cannot pin checkpoint or retain unacknowledged withdrawal");
                var reconnect = new JournalPeerIdentity(new object(), peer.Account, peer.Character, id);
                try { await world.ReceiveAsync(reconnect, new JournalPeerBinding(reconnect), deniedQueue, new JournalFrame(JournalMessage.Login, Guid.NewGuid(), JournalWire.EncodeLogin(id))); throw new Exception("Removed member rejoined"); }
                catch (InvalidDataException) { checks++; }
                Check(!await world.UpdateMetadataAsync(id, new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }) && world.Failure == null,
                    "Stale metadata cannot restore a removed member");
                try { await world.RetireEmptyAsync(id); throw new Exception("Nonempty vault retired"); }
                catch (InvalidOperationException) { checks++; }
                Check(world.Failure == null && world.Vaults.Count == 1 && (await world.ReadAsync(id)).Current.Stacks.Sum(s => s.Item.Quantity) == 4,
                    "Failed disband preserves unknown/overflow stock and leaves authority usable");
                var empty = new VaultIdentity("world", "empty-hird", Guid.NewGuid());
                await world.CreateAsync(new VaultState(empty, 2, Array.Empty<StackRecord>()), new byte[] { 1 });
                await world.RetireEmptyAsync(empty);
                Check(world.Vaults.Count == 1 && (await SqliteJournalStorage.ListVaultsAsync(path)).Count == 2,
                    "Disband hides hird while retaining epoch and retry history");
                try { await world.CreateAsync(new VaultState(new VaultIdentity("world", "empty-hird", Guid.NewGuid()), 2, Array.Empty<StackRecord>()), new byte[] { 1 }); throw new Exception("Retired identity reused"); }
                catch (InvalidOperationException) { checks++; }
                Check(world.Failure == null, "Rejected retired-identity reuse does not poison world authority");
            }
            finally { await world.StopAsync(); }
            string stale = Path.Combine(directory, "legacy.dat"); File.WriteAllBytes(stale, new byte[] { 255 });
            world = await JournalWorldRuntime.OpenAsync(path, "world", stale, _ => throw new Exception("Stale legacy save resurrected"), Members, clock, 4);
            try
            {
                Check(world.Vaults.Count == 1 && world.Vaults.Single().Identity.Epoch == id.Epoch
                    && !world.Vaults.Single().IsMember(2), "Reopen retains membership, epoch and retirement without consulting stale save");
                Check((await world.ReadAsync(id)).Current.Stacks.Sum(s => s.Item.Quantity) == 4, "Reopen retains compensated materialized stock");
            }
            finally { await world.StopAsync(); }
            Console.WriteLine($"PASS {checks} M7 persistent world lifetime assertions"); return checks;
        }
        finally { Directory.Delete(directory, true); }
    }
}
