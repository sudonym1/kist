using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SharedInventory;

// One ordered worker per endpoint. Server requests from all players use the same
// queue, so updating counts is atomic without putting a lock on the game thread.
internal sealed class Worker
{
    private readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();
    private readonly Action<Exception> log;
    private bool stopped;
    public Task Completion { get; }
#if SHAREDINVENTORY_TESTING
    public TestHarness? Testing;
    public int QueueDepth => jobs.IsAddingCompleted ? 0 : jobs.Count;
#endif

    public Worker(Action save, Action<Exception> log, Task previous)
    {
        this.log = log;
        Completion = Task.Factory.StartNew(() => Run(save, previous),
            System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // Enqueued only by the main thread. Never wait for a result here.
    public void Post(Action job)
    {
        if (stopped) return;
#if SHAREDINVENTORY_TESTING
        var testing = Testing;
        if (testing != null)
        {
            var posted = Stopwatch.GetTimestamp();
            jobs.Add(() =>
            {
                testing.Metrics.Observe("workerWaitMs", (Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency);
                var timer = Stopwatch.StartNew();
                try
                {
                    var delay = System.Threading.Volatile.Read(ref testing.Settings.ProcessingDelayMs);
                    if (testing.Settings.Active && delay > 0) System.Threading.Thread.Sleep(delay);
                    job();
                }
                finally { testing.Metrics.Observe("workerServiceMs", timer.Elapsed.TotalMilliseconds); }
            });
            return;
        }
#endif
        jobs.Add(job);
    }

    private void Run(Action save, Task previous)
    {
        // A rapid reconnect must not load a world before its previous worker saved.
        previous.GetAwaiter().GetResult();
        var timer = Stopwatch.StartNew();
        while (!jobs.IsCompleted)
        {
            if (jobs.TryTake(out var job, 100)) Execute(job);
            if (timer.ElapsedMilliseconds >= 2000)
            {
                Execute(save);
                timer.Restart();
            }
        }
        Execute(save);
        jobs.Dispose();
    }

    private void Execute(Action action)
    {
        try { action(); }
        catch (Exception error) { log(error); }
    }

    public Task Stop()
    {
        if (!stopped)
        {
            stopped = true;
            jobs.CompleteAdding();
        }
        return Completion;
    }
}
