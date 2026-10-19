using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;

namespace ThorsKist
{
    // Session owner drains once per Update and disposes on world/session change.
    // Worker verification can await this queue; Unity never awaits the worker.
    internal sealed class JournalGameThread : IDisposable
    {
        private sealed class Work
        {
            public Func<ReplicaResult> Action;
            public TaskCompletionSource<ReplicaResult> Completion;
        }
        private readonly Queue<Work> queue = new Queue<Work>();
        private readonly int owner = Thread.CurrentThread.ManagedThreadId;
        private readonly int maximumPending;
        private bool disposed;
        public JournalGameThread(int maximumPending = 128)
        {
            if (maximumPending <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPending));
            this.maximumPending = maximumPending;
        }
        public Task<ReplicaResult> DispatchAsync(Func<ReplicaResult> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            lock (queue)
            {
                if (disposed) return Task.FromException<ReplicaResult>(new ObjectDisposedException(nameof(JournalGameThread)));
                if (queue.Count >= maximumPending) return Task.FromException<ReplicaResult>(new InvalidOperationException("Journal game-thread queue is full"));
                var completion = new TaskCompletionSource<ReplicaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                queue.Enqueue(new Work { Action = action, Completion = completion });
                return completion.Task;
            }
        }
        public int Drain(int maximumActions)
        {
            RequireOwner();
            if (maximumActions <= 0) throw new ArgumentOutOfRangeException(nameof(maximumActions));
            int count;
            lock (queue) count = Math.Min(maximumActions, queue.Count);
            // Do not execute newly enqueued continuations in the same frame.
            int executed = 0;
            for (int i = 0; i < count; i++)
            {
                Work work;
                lock (queue)
                {
                    if (disposed || queue.Count == 0) break;
                    work = queue.Dequeue();
                }
                executed++;
                try { work.Completion.TrySetResult(work.Action()); }
                catch (Exception error) { work.Completion.TrySetException(error); }
            }
            return executed;
        }
        public void Dispose()
        {
            RequireOwner();
            lock (queue)
            {
                disposed = true;
                while (queue.Count > 0) queue.Dequeue().Completion.TrySetCanceled();
            }
        }
        private void RequireOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != owner) throw new InvalidOperationException("Inventory dispatch requires the owning game thread");
        }
    }
}
