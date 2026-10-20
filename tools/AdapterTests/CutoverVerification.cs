using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BepInEx;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;
using UnityEngine;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private void CutoverLog(string text)
        {
            Logger.LogInfo(text);
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(Paths.BepInExRootPath), "cutover-evidence.log"), DateTime.UtcNow.ToString("O") + " " + text + Environment.NewLine);
        }
        private bool cutoverConfigCaptured, cutoverRange, cutoverSaveOnSet;
        private void RestoreCutoverConfig()
        {
            if (!cutoverConfigCaptured) return;
            Main.UnlimitedRange.Value = cutoverRange;
            Main.Instance.Config.SaveOnConfigSet = cutoverSaveOnSet;
            cutoverConfigCaptured = false;
        }
        private JournalWorldRuntime cutoverWorld;
        private JournalGameplaySession cutoverSession;
        private JournalPeerIdentity cutoverPeer;
        private JournalPeerBinding cutoverBinding;
        private JournalSendQueue cutoverRequests, cutoverReplies;
        private Task cutoverIngress = Task.CompletedTask, cutoverDelivery = Task.CompletedTask;
        private static string Digest(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
        private IEnumerator CutoverDrive(Func<bool> done)
        {
            var timeout = Stopwatch.StartNew();
            while (!done())
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("Native gameplay cutover stalled");
                if (cutoverWorld.Failure != null) throw new Exception("Storage authority failed", cutoverWorld.Failure);
                if (!cutoverSession.LoggingOut && cutoverSession.Client.ProcessingEnded) throw new InvalidOperationException("World owner would close this journal session before completion");
                if (cutoverSession.Failure != null || cutoverSession.Client.Failure != null) throw new Exception("Gameplay session failed", cutoverSession.Failure ?? cutoverSession.Client.Failure);
                if (cutoverIngress.IsFaulted) throw cutoverIngress.Exception.GetBaseException();
                if (cutoverDelivery.IsFaulted) throw cutoverDelivery.Exception.GetBaseException();
                if (cutoverIngress.IsCompleted)
                {
                    var bytes = cutoverRequests.Dequeue();
                    if (bytes != null) cutoverIngress = Task.Run(() => cutoverWorld.ReceiveAsync(cutoverPeer, cutoverBinding, cutoverReplies, JournalWire.DecodeFrame(bytes)));
                }
                if (cutoverDelivery.IsCompleted)
                {
                    var bytes = cutoverReplies.Dequeue();
                    if (bytes != null) cutoverDelivery = Task.Run(() => cutoverSession.Client.ReceiveAsync(JournalWire.DecodeFrame(bytes)));
                }
                cutoverSession.Tick();
                yield return null;
            }
        }
        private IEnumerator CutoverVerification()
        {
            bool traceEnabled = Environment.GetCommandLineArgs().Contains("-thorskist-journal-trace-verify");
            if (traceEnabled)
            {
                var prepare = Task.Run(() => JournalDiagnostics.PrepareCapture());
                while (!prepare.IsCompleted) yield return null;
                JournalDiagnostics.StartCapture(prepare.GetAwaiter().GetResult(), System.Threading.Thread.CurrentThread.ManagedThreadId);
            }
            string source = Path.Combine(TestSaveIsolation.Root, "ThorsKist", "kist_2186005207.dat");
            string database = Path.ChangeExtension(source, ".sqlite");
            byte[] legacy = File.ReadAllBytes(source); string hash = Digest(legacy);
            var names = ObjectDB.instance.m_items.Where(p => p != null).ToDictionary(p => p.name.GetStableHashCode(), p => p.name);
            int version = ItemWire.InventoryVersion, capacity = 8 * Main.KistRows.Value;
            var imports = JournalLegacyDecoder.DecodeCaptured(legacy, "2186005207", capacity, version, names);
            var clock = new SystemReplicationClock();
            Func<Task<JournalWorldRuntime>> open = () => JournalWorldRuntime.OpenAsync(database, "2186005207", source,
                bytes => JournalLegacyDecoder.DecodeCaptured(bytes, "2186005207", capacity, version, names).Select(v => new LegacyVaultImport(v.State, v.CopyHeader())).ToArray(),
                bytes => Hird.ReadHeader(new ZPackage(bytes)).Members.Select(m => m.PlayerId).ToArray(), clock, 64);
            var opening = Task.Run(open); while (!opening.IsCompleted) yield return null;
            cutoverWorld = opening.GetAwaiter().GetResult();
            Check(cutoverWorld.Vaults.Count == imports.Count, "Current test vault catalog retained");
            var vault = cutoverWorld.Vaults[0]; var hird = Hird.ReadHeader(new ZPackage(vault.CopyHeader()));
            var reading = cutoverWorld.ReadAsync(vault.Identity); while (!reading.IsCompleted) yield return null;
            var initial = reading.GetAwaiter().GetResult();
            CutoverLog("M7 CUTOVER AUTHORITY SQLite/journal " + database + "; legacy SHA256=" + hash + "; initial head=" + initial.Head + "; stacks=" + initial.Current.Stacks.Count);
            Check(vault.CopyHeader().SequenceEqual(imports[0].CopyHeader()), "Hird metadata preserved exactly");
            Check(initial.Current.Stacks.Count == imports[0].State.Stacks.Count, "Every current test stack retained including opaque/overflow");
            Func<VaultState, string[]> records = state => state.Stacks.Select(s => s.Slot + ":" + s.Item.Prefab + ":" + s.Item.Quantity + ":" + s.Item.FormatVersion + ":" + Convert.ToBase64String(s.Item.CopyPayload())).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            // The migration fixture matches the immutable import exactly. A
            // later transfer trace uses the current SQLite baseline: ordinary
            // withdrawal/redeposit legitimately replaces stack IDs and slots.
            if (!traceEnabled) Check(records(initial.Current).SequenceEqual(records(imports[0].State)), "Every item quantity, metadata byte and overflow position retained");
            else CutoverLog("M7 JOURNAL TRACE uses current SQLite baseline after manual gameplay; exact restoration checked after scratch transfer and reopen");
            var before = JournalChecksum.Encode(initial.Current);
            var inventory = new Inventory("cutover-verification-character", null, 8, 4);
            var wood = Wood(3); Check(inventory.AddItem(wood), "Native disposable character inventory seeded");
            cutoverRange = Main.UnlimitedRange.Value; cutoverSaveOnSet = Main.Instance.Config.SaveOnConfigSet;
            cutoverConfigCaptured = true; Main.Instance.Config.SaveOnConfigSet = false;
            Main.UnlimitedRange.Value = true;
            KistClient.Hird = hird; KistClient.MinHirdSize = 1;
            cutoverRequests = new JournalSendQueue(); cutoverReplies = new JournalSendQueue();
            cutoverPeer = new JournalPeerIdentity(new object(), "native-cutover-verification", hird.OwnerId, vault.Identity);
            cutoverBinding = new JournalPeerBinding(cutoverPeer);
            cutoverSession = new JournalGameplaySession(inventory, cutoverRequests, clock, new JournalTransferBudget(), () => inventory);
            JournalGameplaySession.Current = cutoverSession;
            Check(cutoverSession.Client.LoginPending && !cutoverSession.Client.ProcessingEnded, "World owner retains newly created session while login is pending");
            cutoverSession.Login(vault.Identity);
            yield return CutoverDrive(() => cutoverSession.AccessEnabled);
            Check(!cutoverSession.Client.LoginPending && !cutoverSession.Client.ProcessingEnded && cutoverSession.Stock.Count == initial.Current.Stacks.Count,
                "World owner reaches active login with every SQLite stock row displayed");
            CutoverLog("M7 CUTOVER LOGIN ACTIVE displayed=" + cutoverSession.Stock.Count + "; sequence=" + cutoverSession.Client.Sequence);
            long login = cutoverSession.Client.Sequence;
            Check(KistClient.Deposit(inventory, wood, 3), "Ordinary KistClient.Deposit accepted by live gameplay adapter");
            Check(inventory.ContainsItem(wood), "Deposit remains present until verified journal effect");
            yield return CutoverDrive(() => !inventory.ContainsItem(wood) && cutoverSession.Client.Sequence > login);
            var deposited = cutoverWorld.ReadAsync(vault.Identity); while (!deposited.IsCompleted) yield return null;
            var afterDeposit = deposited.GetAwaiter().GetResult();
            var added = afterDeposit.Current.Stacks.Single(s => !initial.Current.Stacks.Any(b => b.Id == s.Id));
            Check(added.Item.Quantity == 3 && added.Item.Prefab == "Wood", "Ordinary deposit persisted in SQLite");
            CutoverLog("M7 CUTOVER DEPOSIT journal sequence=" + afterDeposit.Head + "; exact new stack=" + added.Id + "; quantity=3");
            var row = cutoverSession.Stock.Rows.Single(r => r.Stack.Id == added.Id);
            KistClient.TakeAction(row.Item, 3)();
            yield return CutoverDrive(() => inventory.CountItems("$item_wood") == 3);
            var withdrawal = cutoverWorld.ReadAsync(vault.Identity); while (!withdrawal.IsCompleted) yield return null;
            var afterWithdrawal = withdrawal.GetAwaiter().GetResult();
            Check(JournalChecksum.Encode(afterWithdrawal.Current).SequenceEqual(before), "Ordinary withdrawal restores exact original vault state");
            CutoverLog("M7 CUTOVER WITHDRAW journal sequence=" + afterWithdrawal.Head + "; native character received exact quantity=3");
            var logout = cutoverSession.Logout();
            yield return CutoverDrive(() => logout.IsCompleted && cutoverSession.Client.ProcessingStopped);
            Check(logout.GetAwaiter().GetResult().Status == SessionStatus.Ok, "Gameplay logout acknowledged");
            cutoverBinding.Close(); var disconnect = cutoverWorld.DisconnectAsync(cutoverBinding); while (!disconnect.IsCompleted) yield return null; disconnect.GetAwaiter().GetResult();
            var close = cutoverSession.Client.CloseAsync(); while (!close.IsCompleted) yield return null; close.GetAwaiter().GetResult();
            cutoverSession.Dispose(); JournalGameplaySession.Current = null; KistClient.Hird = null;
            var stop = cutoverWorld.StopAsync(); while (!stop.IsCompleted) yield return null; stop.GetAwaiter().GetResult();
            opening = Task.Run(() => JournalWorldRuntime.OpenAsync(database, "2186005207", source,
                bytes => throw new InvalidOperationException("A SQLite reopen must never decode legacy bytes"),
                bytes => Hird.ReadHeader(new ZPackage(bytes)).Members.Select(m => m.PlayerId).ToArray(), clock, 64));
            while (!opening.IsCompleted) yield return null; cutoverWorld = opening.GetAwaiter().GetResult();
            reading = cutoverWorld.ReadAsync(vault.Identity); while (!reading.IsCompleted) yield return null;
            var reopened = reading.GetAwaiter().GetResult();
            Check(reopened.Head > afterWithdrawal.Head && JournalChecksum.Encode(reopened.Current).SequenceEqual(before), "Restart restores SQLite state and journal history");
            Check(Digest(File.ReadAllBytes(source)) == hash, "Original current test dat remains byte-for-byte unchanged");
            Check(File.Exists(source + ".journal-backup-" + hash), "Verified once-only migration backup retained");
            stop = cutoverWorld.StopAsync(); while (!stop.IsCompleted) yield return null; stop.GetAwaiter().GetResult();
            CutoverLog("M7 CUTOVER PASS ordinary gameplay API -> journal -> SQLite -> reopen; dat unchanged; native checks=" + checks);
            RestoreCutoverConfig();
            if (traceEnabled)
            {
                var trace = JournalDiagnostics.StopCapture();
                string path = Path.Combine(Path.GetDirectoryName(Paths.BepInExRootPath), "journal-trace-verification");
                var export = Task.Run(() => MainThreadProfiler.ExportJournalTrace(trace, path, CutoverLog));
                while (!export.IsCompleted) yield return null;
                export.GetAwaiter().GetResult();
                var samples = trace.Snapshot();
                Check(samples.Any(s => s.Stage == "sqlite.transaction.commit_flush"), "Native trace captures real durable commits");
                Check(samples.Any(s => s.Stage == "unity.inventory_effect.work" && s.StartThread == trace.MainThread), "Native inventory effects run on Unity thread");
                Check(samples.Any(s => s.Stage == "client.ack.round_trip"), "Native trace captures acknowledgment latency");
                Check(!samples.Any(trace.ThreadViolation), "All sampled native worker and Unity stages respect their thread expectations");
                Check(File.Exists(Path.Combine(path, "journal-events.csv")) && File.Exists(Path.Combine(path, "journal-stages.csv")), "Native trace export writes events and stage summaries");
                CutoverLog("M7 JOURNAL TRACE PASS checks=" + checks);
            }
            Application.Quit();
        }
        // Iterator exceptions are caught here so headless execution always
        // leaves an explicit failure and exits without touching original saves.
        private IEnumerator RunCutoverVerification()
        {
            var stack = new Stack<IEnumerator>(); stack.Push(CutoverVerification());
            while (stack.Count != 0)
            {
                object current = null;
                try
                {
                    if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                    current = stack.Peek().Current;
                }
                catch (Exception error) { JournalDiagnostics.StopCapture(); RestoreCutoverConfig(); CutoverLog("M7 CUTOVER FAIL " + error); Application.Quit(); yield break; }
                if (current is IEnumerator nested) stack.Push(nested); else yield return current;
            }
        }
    }
}

