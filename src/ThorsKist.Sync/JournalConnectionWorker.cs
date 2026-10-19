using System;
using System.Threading;
using System.Threading.Tasks;

namespace ThorsKist.Sync
{
    // One per live connection. Queue admission is synchronous and bounded;
    // decoding/dispatch never runs on the game thread. Closing cancels work
    // without blocking Unity waiting for a database or network continuation.
    public sealed class JournalConnectionWorker : IDisposable
    {
        private readonly object sync = new object();
        private readonly Func<JournalFrame, CancellationToken, Task> handle;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly int maximumBytes, maximumFrames;
        private Task tail = Task.CompletedTask;
        private int pendingBytes, pendingFrames;
        private bool closed;
        private bool disposed;
        private Exception? failure;
        public Exception? Failure { get { lock (sync) return failure; } }
        public Task Completion { get { lock (sync) return tail; } }
        public JournalConnectionWorker(Func<JournalFrame, CancellationToken, Task> handle, int maximumBytes = 256 * 1024, int maximumFrames = 16)
        {
            if (maximumBytes < JournalWire.MaximumFrameBytes || maximumFrames <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            this.handle = handle ?? throw new ArgumentNullException(nameof(handle)); this.maximumBytes = maximumBytes; this.maximumFrames = maximumFrames;
        }
        public bool TryReceive(byte[] bytes)
        {
            if (bytes == null || bytes.Length < JournalWire.FrameHeaderBytes || bytes.Length > JournalWire.MaximumFrameBytes) return false;
            lock (sync)
            {
                if (closed || failure != null || pendingFrames >= maximumFrames || bytes.Length > maximumBytes - pendingBytes) return false;
                var immutable = (byte[])bytes.Clone(); var previous = tail; pendingFrames++; pendingBytes += immutable.Length;
                tail = Task.Run(async () =>
                {
                    try
                    {
                        await previous.ConfigureAwait(false);
                        cancellation.Token.ThrowIfCancellationRequested();
                        await handle(JournalWire.DecodeFrame(immutable), cancellation.Token).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        // Observe exceptions here; faults close this connection's
                        // ingress, rather than leave unobserved fire-and-forget tasks.
                        lock (sync) { if (!closed && failure == null) failure = error; closed = true; }
                        cancellation.Cancel();
                    }
                    finally { lock (sync) { pendingFrames--; pendingBytes -= immutable.Length; } }
                });
                return true;
            }
        }
        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; closed = true; }
            cancellation.Cancel();
            // Dispose only after queued tasks stop inspecting the source.
            _ = Completion.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
        }
    }
}
