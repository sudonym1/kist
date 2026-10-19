using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

static class Program
{
    static int checks;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--m3-child") { await StorageTests.Child(args); return; }
        var bytes = new byte[] { 1, 2, 3 };
        var item = new ItemRecord("Wood", 3, 109, bytes);
        bytes[0] = 9; var copy = item.CopyPayload(); copy[0] = 8;
        Check(item.CopyPayload()[0] == 1, "Item payload must be immutable");
        var address = new SessionAddress("world", "hird", Guid.NewGuid(), "authenticated-player", Guid.NewGuid());
        var command = new ItemCommand(address, Guid.NewGuid(), Guid.NewGuid(), ItemCommandKind.Deposit, item);
        var clock = new FakeClock(); var handler = new RecordingHandler(); var transport = new FakeTransport(handler);
        await transport.SendAsync(command, default);
        Check(handler.Commands.Count == 0, "Delivery must be explicitly scheduled");
        await transport.Deliver(); await transport.SendAsync(command, default); await transport.Deliver();
        Check(handler.Commands.Count == 2 && ReferenceEquals(handler.Commands[0], handler.Commands[1]), "Scheduler preserves retry identity");
        var wait = clock.Delay(TimeSpan.FromMilliseconds(100), default);
        clock.Advance(TimeSpan.FromMilliseconds(99)); Check(!wait.IsCompleted, "No early backoff");
        clock.Advance(TimeSpan.FromMilliseconds(1)); await wait;
        using (var cancellation = new CancellationTokenSource())
        {
            var canceled = clock.Delay(TimeSpan.FromSeconds(1), cancellation.Token); cancellation.Cancel();
            try { await canceled; throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { checks++; }
        }
        Reservations();
        checks += StateMachineTests.Run();
        checks += await StorageTests.Run();
        Console.WriteLine($"PASS {checks} contract/scheduler/reservation assertions");
        ProbeAndBaseline();
    }
    static void Reservations()
    {
        foreach (string mutation in new[] { "none", "merge", "split", "partial", "metadata", "death", "replacement" })
        {
            var stack = new Stack { Quantity = 10 }; var inventory = new FakeInventory(stack);
            var reservations = new DepositReservations<Stack>(inventory);
            var request = Guid.NewGuid(); var effect = Guid.NewGuid();
            Check(reservations.TryReserve(request, stack, 3), "Reserve");
            Check(stack.Quantity == 10 && reservations.IsBlocked(stack), "Reservation keeps items present and locks source");
            Check(reservations.TryReserve(request, stack, 3), "Same reservation retry");
            Check(!reservations.TryReserve(Guid.NewGuid(), stack, 1), "Block double deposit");
            if (mutation == "merge") stack.Quantity++;
            if (mutation == "split" || mutation == "partial") stack.Quantity--;
            if (mutation == "metadata") stack.Metadata++;
            if (mutation == "death") inventory.Items.Clear();
            if (mutation == "replacement") { inventory.Items.Clear(); inventory.Items.Add(new Stack { Quantity = 10 }); }
            var result = reservations.ApplyVerifiedRemoval(request, effect, 3);
            if (mutation == "none")
            {
                Check(result == InventoryResult.Applied && stack.Quantity == 7, "Exact removal");
                Check(reservations.ApplyVerifiedRemoval(request, effect, 3) == InventoryResult.AlreadyApplied && stack.Quantity == 7, "No repeated effect");
                Check(!reservations.TryReserve(request, stack, 3), "Completed request cannot reserve again");
                Check(reservations.ApplyVerifiedRemoval(request, effect, 2) == InventoryResult.Failed, "Known effect cannot change quantity");
                var otherStack = new Stack { Quantity = 10 }; inventory.Items.Add(otherStack);
                var otherRequest = Guid.NewGuid();
                Check(reservations.TryReserve(otherRequest, otherStack, 3), "Reserve independent source");
                Check(reservations.ApplyVerifiedRemoval(otherRequest, effect, 3) == InventoryResult.Failed
                    && otherStack.Quantity == 10 && reservations.IsBlocked(otherStack), "Known effect cannot complete another request");
            }
            else Check(result == InventoryResult.InventoryChanged && inventory.Removals == 0, "Changed inventory must not be touched: " + mutation);
            reservations.ExcludeSession(); Check(!reservations.IsBlocked(stack), "Exclusion clears without removal/refund");
            if (mutation == "none")
                Check(reservations.ApplyVerifiedRemoval(request, effect, 3) == InventoryResult.AlreadyApplied
                    && stack.Quantity == 7, "Exclusion preserves known effects");
            if (mutation == "replacement") Check(inventory.Items.Single().Quantity == 10, "Never remove replacement");
        }
        var fullStack = new Stack { Quantity = 3 }; var fullInventory = new FakeInventory(fullStack);
        var fullReservations = new DepositReservations<Stack>(fullInventory);
        var fullRequest = Guid.NewGuid(); var fullEffect = Guid.NewGuid();
        Check(fullReservations.TryReserve(fullRequest, fullStack, 3), "Reserve complete stack");
        Check(fullReservations.ApplyVerifiedRemoval(fullRequest, fullEffect, 3) == InventoryResult.Applied
            && !fullInventory.ContainsReference(fullStack), "Complete removal removes source reference");
        Check(fullReservations.ApplyVerifiedRemoval(fullRequest, fullEffect, 3) == InventoryResult.AlreadyApplied
            && fullInventory.Removals == 1, "Complete removal replay is suppressed");
        var source = new Stack { Quantity = 5 }; var inv = new FakeInventory(source);
        var book = new DepositReservations<Stack>(inv); var id = Guid.NewGuid();
        book.TryReserve(id, source, 2);
        Check(book.ApplyVerifiedRemoval(id, Guid.NewGuid(), 3) == InventoryResult.InventoryChanged, "Reject different quantity");
        Check(book.ReleaseRejected(id) && source.Quantity == 5, "Rejection releases without refund");
        id = Guid.NewGuid(); book.TryReserve(id, source, 2); inv.Fail = true;
        Check(book.ApplyVerifiedRemoval(id, Guid.NewGuid(), 2) == InventoryResult.Failed, "Failure cannot acknowledge");
        inv.Fail = false; inv.Partial = true;
        Check(book.ApplyVerifiedRemoval(id, Guid.NewGuid(), 2) == InventoryResult.Failed, "Partial removal cannot acknowledge");
        Check(book.ApplyVerifiedRemoval(id, Guid.NewGuid(), 2) == InventoryResult.InventoryChanged, "Partial failure cannot remove more on retry");
    }
    static void ProbeAndBaseline()
    {
        string directory = Path.Combine(Path.GetTempPath(), "thorskist-m1-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "probe.db");
        try
        {
            using (var db = SqliteConnections.Open(path))
            {
                using var query = db.CreateCommand(); query.CommandText = "SELECT sqlite_version();";
                Console.WriteLine($"SQLite {query.ExecuteScalar()}, {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, {Environment.Version}");
                query.CommandText = "PRAGMA synchronous;"; Check(Convert.ToInt32(query.ExecuteScalar()) == 2, "FULL durability");
                query.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY, payload BLOB NOT NULL);"; query.ExecuteNonQuery();
                using (var transaction = db.BeginTransaction())
                {
                    query.Transaction = transaction; query.CommandText = "INSERT INTO probe VALUES (1, X'01');"; query.ExecuteNonQuery(); transaction.Rollback();
                }
                query.Transaction = null; query.CommandText = "SELECT count(*) FROM probe;"; Check(Convert.ToInt32(query.ExecuteScalar()) == 0, "Rollback");
                var times = new List<double>();
                for (int i = 0; i < 100; i++)
                {
                    var timer = Stopwatch.StartNew(); using var transaction = db.BeginTransaction();
                    using var insert = db.CreateCommand(); insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO probe (payload) VALUES ($payload);"; insert.Parameters.AddWithValue("$payload", new byte[256]);
                    insert.ExecuteNonQuery(); transaction.Commit(); times.Add(timer.Elapsed.TotalMilliseconds);
                }
                times.Sort(); Console.WriteLine($"WAL/FULL 100 single-row commits ms: p50={times[49]:F3} p95={times[94]:F3} p99={times[98]:F3}");
            }
            using (var db = SqliteConnections.Open(path))
            {
                using var query = db.CreateCommand(); query.CommandText = "SELECT count(*) FROM probe;";
                Check(Convert.ToInt32(query.ExecuteScalar()) == 100, "Committed rows survive reopen");
            }
            Console.WriteLine("stacks,recipients,full_bytes,delta_bytes,full_encode_hash_us,delta_encode_hash_us,full_alloc_bytes,delta_alloc_bytes");
            foreach (int stacks in new[] { 0, 32, 512, 2048, 2056 })
            {
                var full = MeasureEncoding(stacks); var delta = MeasureEncoding(stacks == 0 ? 0 : 1);
                foreach (int recipients in new[] { 1, 4, 10 })
                    Console.WriteLine($"{stacks},{recipients},{full.bytes * recipients},{delta.bytes * recipients},{full.us:F2},{delta.us:F2},{full.allocated},{delta.allocated}");
            }
            Console.WriteLine($"PASS {checks} total assertions including file-backed SQLite");
        }
        finally { Directory.Delete(directory, true); }
    }
    // Synthetic wire-cost baseline only, not vanilla serialization or M2 canonical encoding.
    static (int bytes, double us, long allocated) MeasureEncoding(int stacks)
    {
        var payload = new byte[256]; int length = 0;
        using var hash = SHA256.Create();
        long before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
        for (int run = 0; run < 100; run++)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(stacks);
            for (int i = 0; i < stacks; i++) { writer.Write(i); writer.Write(payload.Length); writer.Write(payload); }
            var bytes = stream.ToArray(); hash.ComputeHash(bytes); length = bytes.Length;
        }
        timer.Stop(); return (length, timer.Elapsed.TotalMilliseconds * 10, (GC.GetAllocatedBytesForCurrentThread() - before) / 100);
    }
    sealed class Stack { public int Quantity; public int Metadata; }
    sealed class FakeInventory : IReservationInventory<Stack>
    {
        public readonly List<Stack> Items = new(); public int Removals; public bool Fail, Partial;
        public FakeInventory(Stack stack) { Items.Add(stack); }
        public bool ContainsReference(Stack stack) => Items.Any(s => ReferenceEquals(s, stack));
        public int Quantity(Stack stack) => stack.Quantity;
        public byte[] Capture(Stack stack) => BitConverter.GetBytes(stack.Quantity).Concat(BitConverter.GetBytes(stack.Metadata)).ToArray();
        public bool RemoveExact(Stack stack, int quantity)
        {
            if (Fail) return false;
            Removals++; stack.Quantity -= Partial ? 1 : quantity;
            if (stack.Quantity == 0) Items.Remove(stack);
            return true;
        }
    }
    sealed class RecordingHandler : ICommandHandler
    {
        public readonly List<ItemCommand> Commands = new();
        public Task HandleAsync(ItemCommand command, CancellationToken token) { token.ThrowIfCancellationRequested(); Commands.Add(command); return Task.CompletedTask; }
    }
    sealed class FakeTransport : ICommandTransport
    {
        readonly Queue<ItemCommand> queue = new(); readonly ICommandHandler handler;
        public FakeTransport(ICommandHandler handler) { this.handler = handler; }
        public Task SendAsync(ItemCommand command, CancellationToken token) { token.ThrowIfCancellationRequested(); queue.Enqueue(command); return Task.CompletedTask; }
        public Task Deliver() => handler.HandleAsync(queue.Dequeue(), default);
    }
    sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        readonly List<(DateTimeOffset due, TaskCompletionSource<bool> source, CancellationTokenRegistration registration)> waits = new();
        public Task Delay(TimeSpan delay, CancellationToken token)
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
            if (token.IsCancellationRequested) return Task.FromCanceled(token);
            if (delay == TimeSpan.Zero) return Task.CompletedTask;
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = token.Register(() => source.TrySetCanceled(token));
            waits.Add((UtcNow + delay, source, registration)); return source.Task;
        }
        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
            UtcNow += elapsed;
            foreach (var wait in waits.Where(w => w.due <= UtcNow || w.source.Task.IsCompleted).ToArray())
            { waits.Remove(wait); wait.registration.Dispose(); wait.source.TrySetResult(true); }
        }
    }
}
