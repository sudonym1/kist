using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;

static class DiagnosticsTests
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        JournalDiagnostics.StopCapture();
        // Disabled probes must not allocate on a hot rendering path.
        using (JournalDiagnostics.Measure("warmup")) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) using (JournalDiagnostics.Measure("disabled")) { }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Disabled timing probes allocate no memory");
        int main = Thread.CurrentThread.ManagedThreadId;
        var trace = JournalDiagnostics.StartCapture(main, 4096);
        Guid request = Guid.NewGuid();
        using (JournalDiagnostics.Correlate(request, 73, "test"))
        {
            JournalDiagnostics.SetFrame(123);
            using (JournalDiagnostics.Measure("main", expected: JournalTimingThread.Main)) { }
            // Hold the initiating thread while workers run to make thread expectations deterministic.
            Task.Run(() => Parallel.For(0, 1000, _ =>
            {
                using (JournalDiagnostics.Measure("worker")) { }
            })).GetAwaiter().GetResult();
            using (JournalDiagnostics.Measure("deliberate-violation")) { }
        }
        var late = JournalDiagnostics.Measure("late");
        JournalDiagnostics.StopCapture();
        var samples = trace.Snapshot();
        Check(samples.Length == 1002, "Concurrent completions publish whole events without losses");
        Check(samples.All(s => s.Correlation.Request == request && s.Correlation.Sequence == 73 && s.Frame == 123), "Request context flows into worker execution");
        Check(samples.Count(trace.ThreadViolation) == 1 && trace.ThreadViolation(samples.Single(s => s.Stage == "deliberate-violation")), "Thread violations distinguish worker and main work");
        var next = JournalDiagnostics.StartCapture(main, 2);
        late.Dispose();
        for (int i = 0; i < 5; i++) using (JournalDiagnostics.Measure("bounded", expected: JournalTimingThread.Any)) { }
        JournalDiagnostics.StopCapture();
        Check(next.Snapshot().Length == 2 && next.Dropped == 3 && next.StartedSpans == 5, "Bounded captures disclose dropped events and isolate late completions");
        Check(trace.Snapshot().Length == 1002, "Stopping capture excludes late completion");
        // Existing real SQLite world tests exercise login, commands, compensation and reopen under capture.
        trace = JournalDiagnostics.StartCapture(main, 32768);
        try { await WorldRuntimeTests.Run(); }
        finally { JournalDiagnostics.StopCapture(); }
        samples = trace.Snapshot();
        Check(samples.Any(s => s.Stage == "sqlite.transaction.commit_flush"), "Real SQLite commits have durable-flush timings");
        Check(samples.Any(s => s.Stage == "sqlite.rows.write") && samples.Any(s => s.Stage == "server.serial_queue.wait"), "World mutations expose row writes and serial queue waits");
        Check(samples.Where(s => s.Stage.StartsWith("sqlite.") && s.Kind == JournalTimingKind.Work).All(s => s.StartThread == s.EndThread), "SQLite synchronous work scopes never cross awaits");
        Check(samples.Any(s => s.Correlation.Operation == "command" && s.Correlation.Request != Guid.Empty), "Real command work retains request correlation");
        Console.WriteLine($"PASS {checks} bounded concurrent journal diagnostics assertions");
        return checks;
    }
}
