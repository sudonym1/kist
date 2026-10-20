using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

static class AccessTests
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    public static async Task<int> Run()
    {
        checks = 0;
        var vault = new VaultIdentity("m7", "access", Guid.NewGuid()); var connection = new object();
        var peer = new JournalPeerIdentity(connection, "account", 123, vault);
        var binding = new JournalPeerBinding(peer); var address = new SessionAddress(vault.World, vault.Vault, vault.Epoch, peer.Account, Guid.NewGuid());
        Check(binding.Bind(peer, address), "Bind authenticated access test session");
        var item = new ItemRecord("Wood", 1, 110, Array.Empty<byte>());
        foreach (var action in new[] {
            (ItemCommandKind.Deposit, ItemCommandPurpose.Inventory, JournalAccess.Deposit),
            (ItemCommandKind.Withdraw, ItemCommandPurpose.Inventory, JournalAccess.Take),
            (ItemCommandKind.Withdraw, ItemCommandPurpose.Craft, JournalAccess.Craft),
            (ItemCommandKind.Withdraw, ItemCommandPurpose.Station, JournalAccess.Station) })
        {
            var command = new ItemCommand(address, Guid.NewGuid(), Guid.NewGuid(), action.Item1, item, action.Item2);
            var frame = new JournalFrame(JournalMessage.Command, command.RequestId, JournalWire.EncodeCommand(command));
            Check(JournalWire.DecodeCommand(frame.CopyBody()).Purpose == command.Purpose, "Wire preserves action-specific purpose");
            foreach (var permission in new[] { JournalAccess.None, JournalAccess.Deposit, JournalAccess.Take, JournalAccess.Craft, JournalAccess.Station, JournalAccess.All })
            {
                var captured = new JournalPeerIdentity(connection, peer.Account, peer.Character, vault, permission);
                if ((permission & action.Item3) != 0)
                    Check(JournalRequest.Decode(frame, captured, binding).Command!.Purpose == action.Item2, "Correct action range authorizes command");
                else
                {
                    try { JournalRequest.Decode(frame, captured, binding); throw new Exception("Wrong action range admitted command"); }
                    catch (InvalidDataException) { checks++; }
                }
            }
        }
        var away = new JournalPeerIdentity(connection, peer.Account, peer.Character, vault, JournalAccess.None);
        foreach (var kind in new[] { JournalMessage.Acknowledge, JournalMessage.Logout, JournalMessage.Suspend })
        {
            var frame = new JournalFrame(kind, Guid.NewGuid(), JournalWire.EncodePosition(address, 0));
            Check(JournalRequest.Decode(frame, away, binding).Message == kind, "Settlement controls continue outside every mutation range");
        }
        await WorkerContexts(peer, vault);
        await PersistPurpose(vault, address, item);
        Console.WriteLine($"PASS {checks} M7 action access/persistence assertions"); return checks;
    }
    static async Task WorkerContexts(JournalPeerIdentity peer, VaultIdentity vault)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<JournalAccess>();
        using var worker = new JournalConnectionWorker(async (frame, context, token) =>
        { if (seen.Count == 0) { entered.TrySetResult(true); await gate.Task; } seen.Add(((JournalPeerIdentity)context!).Access); });
        var bytes = JournalWire.EncodeFrame(new JournalFrame(JournalMessage.Login, Guid.NewGuid(), JournalWire.EncodeLogin(vault)));
        var depositing = new JournalPeerIdentity(peer.Connection, peer.Account, peer.Character, vault, JournalAccess.Deposit);
        var taking = new JournalPeerIdentity(peer.Connection, peer.Account, peer.Character, vault, JournalAccess.Take);
        Check(worker.TryReceive(bytes, depositing), "Worker accepts first immutable access snapshot"); await entered.Task;
        Check(worker.TryReceive(bytes, taking), "Worker accepts subsequent changed-range snapshot");
        gate.TrySetResult(true); await worker.Completion;
        Check(seen.SequenceEqual(new[] { JournalAccess.Deposit, JournalAccess.Take }), "Queued frames retain their own captured authorization facts");
    }
    static async Task PersistPurpose(VaultIdentity vault, SessionAddress address, ItemRecord item)
    {
        var directory = Path.Combine(Path.GetTempPath(), "thorskist-m7-purpose-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "purpose.db"); var stack = Guid.NewGuid();
            var initial = new VaultState(vault, 1, new[] { new StackRecord(stack, 0, new ItemRecord("Wood", 3, 110, Array.Empty<byte>())) });
            var request = Guid.NewGuid(); Guid operation;
            using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, vault), initial))
            {
                var craft = new ItemCommand(address, request, stack, ItemCommandKind.Withdraw, item, ItemCommandPurpose.Craft);
                var result = await writer.ExecuteAsync(craft); operation = result.Result!.Entry.OperationId;
                Check((await writer.ExecuteAsync(new ItemCommand(address, request, stack, ItemCommandKind.Withdraw, item))).Status == CommandStatus.RequestConflict,
                    "Same request cannot change craft into ordinary take");
                await writer.CheckpointAsync(result.State.Head);
            }
            using (var writer = await JournalWriter.OpenAsync(new SqliteJournalStorage(path, vault), initial))
            {
                var state = await writer.ReadAsync(); Check(state.Results.Single().Command.Purpose == ItemCommandPurpose.Craft, "SQLite/recovery preserves purpose after checkpoint pruning");
                var retry = await writer.ExecuteAsync(new ItemCommand(address, request, stack, ItemCommandKind.Withdraw, item, ItemCommandPurpose.Craft));
                Check(retry.IsRetry && retry.Result!.Entry.OperationId == operation && retry.State.Current.Stacks.Single().Item.Quantity == 2,
                    "Material-use retry after reopen cannot debit again");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
