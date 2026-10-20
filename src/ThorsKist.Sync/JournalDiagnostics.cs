using System;
using System.Diagnostics;
using System.Threading;

namespace ThorsKist.Sync
{
    public enum JournalTimingKind { Work, Wait, Elapsed, Marker }
    public enum JournalTimingThread { Any, Main, Worker }
    // Optional bounded diagnostics. No file IO, logging or callbacks on sampled
    // threads. Disabled probes return default structs; export runs on a worker.
    public static class JournalDiagnostics
    {
        public readonly struct Correlation
        {
            public Guid Request { get; }
            public long Sequence { get; }
            public string Operation { get; }
            public Correlation(Guid request, long sequence, string operation)
            { Request = request; Sequence = sequence; Operation = operation; }
        }
        private static readonly AsyncLocal<Correlation?> context = new AsyncLocal<Correlation?>();
        private static Capture? current;
        public static string Operation(JournalMessage message) => message switch
        {
            JournalMessage.Command => "command", JournalMessage.Acknowledge => "ack", JournalMessage.Login => "login",
            JournalMessage.Logout => "logout", JournalMessage.Suspend => "suspend", JournalMessage.Entries => "entries", _ => "transfer"
        };
        public static bool Enabled => Volatile.Read(ref current) != null;
        public static Correlation Current => context.Value ?? new Correlation(Guid.Empty, 0, "none");
        public readonly struct ContextScope : IDisposable
        {
            private readonly Correlation? previous;
            private readonly bool active;
            internal ContextScope(Correlation next) { previous = context.Value; active = true; context.Value = next; }
            public void Dispose() { if (active) context.Value = previous; }
        }
        public static ContextScope Use(Correlation next) => Enabled ? new ContextScope(next) : default;
        public static ContextScope Correlate(Guid request, long sequence, string operation) => Use(new Correlation(request, sequence, operation));
        public readonly struct Sample
        {
            public string Stage { get; }
            public JournalTimingKind Kind { get; }
            public JournalTimingThread Expected { get; }
            public Correlation Correlation { get; }
            public long Id { get; }
            public long Start { get; }
            public long End { get; }
            public int StartThread { get; }
            public int EndThread { get; }
            public int Frame { get; }
            public int Count { get; }
            internal Sample(string stage, JournalTimingKind kind, JournalTimingThread expected, Correlation correlation,
                long id, long start, long end, int startThread, int endThread, int frame, int count)
            { Stage = stage; Kind = kind; Expected = expected; Correlation = correlation; Id = id;
              Start = start; End = end; StartThread = startThread; EndThread = endThread; Frame = frame; Count = count; }
        }
        public readonly struct Scope : IDisposable
        {
            private readonly Capture? capture;
            private readonly string stage;
            private readonly JournalTimingKind kind;
            private readonly JournalTimingThread expected;
            private readonly Correlation correlation;
            private readonly long id, start;
            private readonly int thread, frame, count;
            internal Scope(Capture capture, string stage, JournalTimingKind kind, JournalTimingThread expected, int count)
            {
                this.capture = capture; this.stage = stage; this.kind = kind; this.expected = expected;
                correlation = Current; id = Interlocked.Increment(ref capture.Started); start = Stopwatch.GetTimestamp();
                thread = Thread.CurrentThread.ManagedThreadId; frame = Volatile.Read(ref capture.Frame); this.count = count;
            }
            public void Dispose()
            {
                if (capture == null) return;
                capture.Record(new Sample(stage, kind, expected, correlation, id, start, Stopwatch.GetTimestamp(),
                    thread, Thread.CurrentThread.ManagedThreadId, frame, count));
            }
        }
        public sealed class Capture
        {
            private readonly Sample[] samples;
            private readonly int[] ready;
            private int next = -1, stopped;
            internal long Started;
            internal int Frame;
            public long StartTicks { get; private set; }
            public DateTimeOffset StartedUtc { get; private set; }
            public long StopTicks { get; private set; }
            public int MainThread { get; private set; }
            public int Capacity => samples.Length;
            public long StartedSpans => Interlocked.Read(ref Started);
            public int Dropped => Math.Max(0, Volatile.Read(ref next) + 1 - samples.Length);
            internal Capture(int mainThread, int capacity)
            { MainThread = mainThread; samples = new Sample[capacity]; ready = new int[capacity]; }
            internal void Activate(int mainThread)
            {
                if (StartTicks != 0) throw new InvalidOperationException("Capture buffers cannot be reused");
                MainThread = mainThread; StartedUtc = DateTimeOffset.UtcNow; StartTicks = Stopwatch.GetTimestamp();
            }
            public bool ThreadViolation(Sample sample) => sample.Kind == JournalTimingKind.Work &&
                ((sample.StartThread != sample.EndThread)
                || (sample.Expected == JournalTimingThread.Worker && (sample.StartThread == MainThread || sample.EndThread == MainThread))
                || (sample.Expected == JournalTimingThread.Main && (sample.StartThread != MainThread || sample.EndThread != MainThread)));
            internal void Record(Sample sample)
            {
                if (Volatile.Read(ref stopped) != 0) return;
                int slot = Interlocked.Increment(ref next);
                if (slot >= samples.Length) return;
                samples[slot] = sample;
                // Publish only a completely written event. Export never waits
                // for a writer and never reads a partially initialized record.
                Volatile.Write(ref ready[slot], 1);
            }
            internal void Stop() { StopTicks = Stopwatch.GetTimestamp(); Volatile.Write(ref stopped, 1); }
            public Sample[] Snapshot()
            {
                var count = Math.Min(samples.Length, Volatile.Read(ref next) + 1);
                var result = new System.Collections.Generic.List<Sample>(Math.Max(0, count));
                for (int i = 0; i < count; i++) if (Volatile.Read(ref ready[i]) != 0) result.Add(samples[i]);
                return result.ToArray();
            }
        }
        public static Capture PrepareCapture(int capacity = 131072)
        {
            if (capacity <= 0 || capacity > 1048576) throw new ArgumentOutOfRangeException(nameof(capacity));
            return new Capture(0, capacity);
        }
        public static Capture StartCapture(int mainThread, int capacity = 131072) => StartCapture(PrepareCapture(capacity), mainThread);
        public static Capture StartCapture(Capture capture, int mainThread)
        {
            if (mainThread <= 0 || capture == null) throw new ArgumentOutOfRangeException(nameof(mainThread));
            capture.Activate(mainThread);
            if (Interlocked.CompareExchange(ref current, capture, null) != null) throw new InvalidOperationException("Journal trace already active");
            return capture;
        }
        public static Capture? StopCapture()
        { var capture = Interlocked.Exchange(ref current, null); capture?.Stop(); return capture; }
        public static void SetFrame(int frame)
        { var capture = Volatile.Read(ref current); if (capture != null) Volatile.Write(ref capture.Frame, frame); }
        public static Scope Measure(string stage, JournalTimingKind kind = JournalTimingKind.Work,
            JournalTimingThread expected = JournalTimingThread.Worker, int count = 0)
        { var capture = Volatile.Read(ref current); return capture == null ? default : new Scope(capture, stage, kind, expected, count); }
        public static void Mark(string stage, Guid request, long sequence, int count = 0)
        { using (Correlate(request, sequence, "item")) using (Measure(stage, JournalTimingKind.Marker, JournalTimingThread.Any, count)) { } }
    }
}
