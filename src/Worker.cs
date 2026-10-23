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

    public Worker(Action save, Action<Exception> log, Task previous)
    {
        this.log = log;
        Completion = Task.Factory.StartNew(() => Run(save, previous),
            System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // Enqueued only by the main thread. Never wait for a result here.
    public void Post(Action job)
    {
        if (!stopped) jobs.Add(job);
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
