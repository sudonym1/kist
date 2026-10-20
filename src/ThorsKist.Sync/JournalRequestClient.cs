using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThorsKist.Sync
{
    // Connection-scoped correlation only; no files or terminal result cache.
    // A timeout is uncertain. The caller retains the original request/payload
    // and reservation until a committed entry or definitive rejection arrives.
    public sealed class JournalRequestClient : IDisposable
    {
        private sealed class Pending
        {
            public readonly JournalFrame Frame;
            public readonly byte[] Bytes;
            public readonly JournalDiagnostics.Scope RoundTrip;
            private int timingEnded;
            public void EndTiming() { if (Interlocked.Exchange(ref timingEnded, 1) == 0) RoundTrip.Dispose(); }
            public readonly TaskCompletionSource<JournalReply> Reply = new TaskCompletionSource<JournalReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Pending(JournalFrame frame, byte[] bytes) { Frame = frame; Bytes = bytes; RoundTrip = JournalDiagnostics.Measure("client.request.round_trip", JournalTimingKind.Elapsed, JournalTimingThread.Any); }
        }
        private readonly object sync = new object();
        private readonly Dictionary<Guid, Pending> pending = new Dictionary<Guid, Pending>();
        private readonly Func<byte[], bool> send;
        private readonly IClock clock;
        private readonly int maximumPending;
        private readonly SemaphoreSlim largeSend = new SemaphoreSlim(1, 1);
        private bool closed;
        public JournalRequestClient(Func<byte[], bool> send, IClock clock, int maximumPending = 64)
        {
            if (maximumPending <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPending));
            this.send = send; this.clock = clock; this.maximumPending = maximumPending;
        }
        public Task<JournalReply> RequestAsync(JournalFrame frame, CancellationToken token = default)
        {
            if (frame.Message < JournalMessage.Login || frame.Message > JournalMessage.Suspend) throw new ArgumentException("Not a request");
            token.ThrowIfCancellationRequested();
            using var correlation = JournalDiagnostics.Correlate(frame.Correlation, JournalDiagnostics.Current.Sequence, JournalDiagnostics.Operation(frame.Message));
            bool large = frame.Message == JournalMessage.Command && frame.CopyBody().Length > JournalWire.MaximumFrameBody;
            var bytes = large ? frame.CopyBody() : JournalWire.EncodeFrame(frame);
            lock (sync)
            {
                if (closed) throw new ObjectDisposedException(nameof(JournalRequestClient));
                if (pending.TryGetValue(frame.Correlation, out var existing))
                {
                    if (existing.Frame.Message != frame.Message || !existing.Bytes.SequenceEqual(bytes)) throw new InvalidOperationException("Pending request ID has different payload");
                    return existing.Reply.Task;
                }
                if (pending.Count >= maximumPending) throw new IOException("Too many pending journal requests");
                var p = new Pending(frame, bytes); pending.Add(frame.Correlation, p);
                try
                {
                    if (large) _ = Task.Run(() => SendLargeAsync(p, token));
                    else if (!send(bytes)) throw new IOException("Journal request queue full");
                }
                catch { pending.Remove(frame.Correlation); p.EndTiming(); throw; }
                _ = ExpireAsync(p, token); return p.Reply.Task;
            }
        }
        private async Task SendLargeAsync(Pending p, CancellationToken token)
        {
            bool entered = false;
            try
            {
                await largeSend.WaitAsync(token).ConfigureAwait(false); entered = true;
                var transfer = new JournalTransfer(p.Frame.Correlation, p.Bytes, JournalMessage.CommandChunk);
                for (int i = 0; i < transfer.FrameCount; i++)
                {
                    var bytes = JournalWire.EncodeFrame(transfer.Frame(i));
                    while (true)
                    {
                        lock (sync)
                        {
                            if (closed || p.Reply.Task.IsCompleted) return;
                            if (send(bytes)) break;
                        }
                        await clock.Delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    if (pending.TryGetValue(p.Frame.Correlation, out var current) && ReferenceEquals(current, p)) pending.Remove(p.Frame.Correlation);
                    p.EndTiming(); p.Reply.TrySetException(error);
                }
            }
            finally { if (entered) largeSend.Release(); }
        }
        private async Task ExpireAsync(Pending p, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                try
                {
                    var delay = clock.Delay(TimeSpan.FromSeconds(30), deadline.Token);
                    if (await Task.WhenAny(p.Reply.Task, delay).ConfigureAwait(false) == delay)
                    {
                        lock (sync)
                        {
                            // Remove by object identity: a late timer cannot expire a retry.
                            if (pending.TryGetValue(p.Frame.Correlation, out var current) && ReferenceEquals(current, p))
                            {
                                pending.Remove(p.Frame.Correlation);
                                p.EndTiming();
                                if (token.IsCancellationRequested) p.Reply.TrySetCanceled();
                                else p.Reply.TrySetException(new TimeoutException("Journal request outcome is uncertain"));
                            }
                        }
                    }
                    deadline.Cancel();
                    try { await delay.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                catch (Exception error)
                {
                    lock (sync)
                    {
                        if (pending.TryGetValue(p.Frame.Correlation, out var current) && ReferenceEquals(current, p)) pending.Remove(p.Frame.Correlation);
                        p.EndTiming(); p.Reply.TrySetException(error);
                    }
                }
            }
        }
        // Call only after the adapter has checked the exact current server RPC.
        // Late replies from expired requests are ignored; wrong-kind replies
        // for a current request are protocol errors, not successful correlations.
        public bool AcceptReply(JournalFrame frame)
        {
            if (frame.Message != JournalMessage.Reply) throw new InvalidDataException("Not a reply");
            var reply = JournalReply.Decode(frame.CopyBody());
            lock (sync)
            {
                if (closed || !pending.TryGetValue(frame.Correlation, out var p)) return false;
                if (p.Frame.Message != reply.Request) throw new InvalidDataException("Reply request kind mismatch");
                pending.Remove(frame.Correlation); p.EndTiming();
                JournalDiagnostics.Mark("client.reply.received", frame.Correlation, reply.Sequence != 0 ? reply.Sequence : reply.Acknowledged);
                p.Reply.TrySetResult(reply); return true;
            }
        }
        public async Task<JournalReply> ExecuteAsync(ItemCommand command, CancellationToken token = default)
        {
            using var correlation = JournalDiagnostics.Correlate(command.RequestId, 0, "command");
            using var elapsed = JournalDiagnostics.Measure("client.command.elapsed", JournalTimingKind.Elapsed, JournalTimingThread.Any, command.Item.Quantity);
            JournalDelivery.ValidateCommand(command);
            var frame = new JournalFrame(JournalMessage.Command, command.RequestId, JournalWire.EncodeCommand(command));
            while (true)
            {
                var reply = await RequestAsync(frame, token).ConfigureAwait(false);
                if (reply.Status != SessionStatus.TransactionDepthExceeded) return reply;
                await clock.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
            }
        }
        public void Dispose()
        {
            lock (sync)
            {
                if (closed) return; closed = true;
                foreach (var p in pending.Values) { p.EndTiming(); p.Reply.TrySetException(new IOException("Journal connection closed; outcome is uncertain")); }
                pending.Clear();
            }
        }
    }
}
