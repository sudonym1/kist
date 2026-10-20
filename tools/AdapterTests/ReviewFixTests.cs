using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;
using UnityEngine;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private IEnumerator ReviewFixes()
        {
            WithdrawalStacking();
            var clock = new SystemReplicationClock();
            string path = Path.Combine(TestSaveIsolation.Root, "review-fixes-" + Guid.NewGuid().ToString("N") + ".sqlite");
            var id = new VaultIdentity("review", "deposits", Guid.NewGuid());
            var named = JournalItemCodec.Capture(Wood(45), 45);
            var alias = new ItemRecord("hash:" + "Wood".GetStableHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture),
                45, named.FormatVersion, named.CopyPayload());
            var first = Guid.NewGuid(); var second = Guid.NewGuid();
            var opening = Task.Run(() => JournalWorldRuntime.OpenAsync(path, id.World, null, _ => Array.Empty<LegacyVaultImport>(),
                _ => new long[] { 1 }, clock, 64, stackLimit: _ => 50));
            while (!opening.IsCompleted) yield return null;
            cutoverWorld = opening.GetAwaiter().GetResult();
            var create = cutoverWorld.CreateAsync(new VaultState(id, 2, new[] {
                new StackRecord(first, 0, alias),
                new StackRecord(second, 1, JournalItemCodec.Capture(Wood(47), 47)) }), new byte[] { 1 });
            while (!create.IsCompleted) yield return null;
            create.GetAwaiter().GetResult();
            var inventory = new Inventory("review deposit fixture", null, 8, 4);
            cutoverRequests = new JournalSendQueue(); cutoverReplies = new JournalSendQueue();
            cutoverPeer = new JournalPeerIdentity(new object(), "review-player", 1, id);
            cutoverBinding = new JournalPeerBinding(cutoverPeer);
            cutoverSession = new JournalGameplaySession(inventory, cutoverRequests, clock, new JournalTransferBudget(), () => inventory);
            JournalGameplaySession.Current = cutoverSession;
            cutoverSession.Login(id);
            yield return CutoverDrive(() => cutoverSession.AccessEnabled);
            foreach (string field in new[] { "custom", "variant", "crafter", "durability", "quality", "world", "picked", "cheated" })
            {
                var different = Wood(1); ChangeStackMetadata(different, field);
                inventory.GetAllItems().Add(different);
                Check(!cutoverSession.Deposit(inventory, different, 1) && different.m_stack == 1
                    && !JournalReservationGuard.Busy(inventory) && cutoverSession.Stock.Rows.Sum(r => r.Stack.Item.Quantity) == 92,
                    "Deposit cannot use partial stacks with different metadata, matching withdrawal: " + field);
                inventory.GetAllItems().Remove(different);
            }
            var source = Wood(8); inventory.GetAllItems().Add(source);
            Check(cutoverSession.Deposit(inventory, source, 8), "Deposit admitted against two partial stacks in a full vault");
            yield return CutoverDrive(() => !inventory.ContainsItem(source));
            Check(cutoverSession.Stock.Rows.Count() == 2 && cutoverSession.Stock.Rows.All(r => r.Stack.Item.Quantity == 50)
                && !JournalReservationGuard.Busy(inventory), "One deposit fills both partial stacks, including hash alias, without new slots");

            source = Wood(1); inventory.GetAllItems().Add(source);
            Check(!cutoverSession.Deposit(inventory, source, 1), "Full vault fails deposit capacity preflight");
            Check(source.m_stack == 1 && inventory.ContainsItem(source) && cutoverSession.Stock.Rows.All(r => r.Stack.Item.Quantity == 50),
                "Refused deposit preserves local items without reserving them");
            Check(cutoverSession.Take(first, 4) && cutoverSession.Take(second, 4), "Create room in two existing vault stacks");
            yield return CutoverDrive(() => cutoverSession.Stock.Rows.All(r => r.Stack.Item.Quantity == 46));
            inventory.GetAllItems().Clear();
            source = Wood(9); inventory.GetAllItems().Add(source);
            Check(!cutoverSession.Deposit(inventory, source, 9) && source.m_stack == 9
                && !JournalReservationGuard.Busy(inventory) && cutoverSession.Stock.Rows.All(r => r.Stack.Item.Quantity == 46),
                "Insufficient total room leaves partial vault stacks and source untouched, like withdrawal");
            inventory.GetAllItems().Clear();
            var a = Wood(4); var b = Wood(4); b.m_gridPos = new Vector2i(1, 0);
            inventory.GetAllItems().Add(a); inventory.GetAllItems().Add(b);
            Check(cutoverSession.Deposit(inventory, a, 4) && cutoverSession.Deposit(inventory, b, 4),
                "Concurrent local deposits reserve distinct remaining room");
            yield return CutoverDrive(() => !inventory.ContainsItem(a) && !inventory.ContainsItem(b));
            Check(cutoverSession.Stock.Rows.All(r => r.Stack.Item.Quantity == 50), "Pending deposit accounting prevents local overfill");

            // Free a slot, then prove incompatible metadata creates a separate row.
            Check(cutoverSession.Take(second, 50), "Free a vault slot");
            yield return CutoverDrive(() => !cutoverSession.Stock.Find(second, out _));
            inventory.GetAllItems().Clear();
            var distinct = Wood(10); distinct.m_customData["review"] = "keep separate"; inventory.GetAllItems().Add(distinct);
            Check(cutoverSession.Deposit(inventory, distinct, 10), "Deposit distinct metadata");
            yield return CutoverDrive(() => !inventory.ContainsItem(distinct));
            Check(cutoverSession.Stock.Rows.Count() == 2 && cutoverSession.Stock.Rows.Any(r => r.Item.m_customData.ContainsKey("review")),
                "Metadata remains intact in a separate stack");
            var logout = cutoverSession.Logout();
            yield return CutoverDrive(() => logout.IsCompleted && cutoverSession.Client.ProcessingStopped);
            logout.GetAwaiter().GetResult();
            cutoverSession.Dispose(); JournalGameplaySession.Current = null;
            var stop = cutoverWorld.StopAsync(); while (!stop.IsCompleted) yield return null; stop.GetAwaiter().GetResult();
            opening = JournalWorldRuntime.OpenAsync(path, id.World, null, _ => Array.Empty<LegacyVaultImport>(),
                _ => new long[] { 1 }, clock, 64, capacity: 4);
            while (!opening.IsCompleted) yield return null; cutoverWorld = opening.GetAwaiter().GetResult();
            var read = cutoverWorld.ReadAsync(id); while (!read.IsCompleted) yield return null;
            Check(read.Result.Current.Capacity == 4 && read.Result.Current.Stacks.Sum(s => s.Item.Quantity) == 60,
                "Native gameplay database reopens with configured capacity and exact stock");
            stop = cutoverWorld.StopAsync(); while (!stop.IsCompleted) yield return null; stop.GetAwaiter().GetResult();
            CutoverLog("REVIEW FIXES PASS " + checks + " native assertions");
            Application.Quit();
        }
    }
}
