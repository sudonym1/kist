using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharedInventory;

// Compiled only with EnableTestHarness=true. The scheduler is main-thread-only;
// telemetry can also be recorded by the database worker. No Unity dependency.
internal sealed class TestSettings
{
    public volatile bool Active = false;
    public int Seed = 12345;
    public int RequestDelayMs = 0, ReplyDelayMs = 0, JitterMs = 0, BytesPerSecond = 0;
    public int ProcessingDelayMs = 0, SaveDelayMs = 0, SaveFailures = 0;
    public long TargetPeer = 0; // zero means every peer
    public bool AllowReordering = false;
    public int MaxScheduled = 100000;
}

internal sealed class LabMetrics
{
    private readonly object gate = new object();
    private readonly Dictionary<string, long> counts = new Dictionary<string, long>();
    private readonly Dictionary<string, Queue<double>> samples = new Dictionary<string, Queue<double>>();
    private readonly Queue<object> trace = new Queue<object>();
    public void Add(string name, long amount = 1)
    {
        lock (gate) counts[name] = (counts.TryGetValue(name, out var value) ? value : 0) + amount;
    }
    public void Observe(string name, double value)
    {
        lock (gate)
        {
            if (!samples.TryGetValue(name, out var values)) samples.Add(name, values = new Queue<double>());
            if (values.Count == 8192) values.Dequeue();
            values.Enqueue(value);
        }
    }
    public void Event(object value)
    {
        lock (gate)
        {
            if (trace.Count == 10000) trace.Dequeue();
            trace.Enqueue(value);
        }
    }
    public object Snapshot(bool includeTrace = true)
    {
        lock (gate) return new
        {
            Counters = new Dictionary<string, long>(counts),
            Samples = samples.ToDictionary(p => p.Key, p =>
            {
                var values = p.Value.OrderBy(v => v).ToArray();
                double Percentile(double fraction) => values[Math.Max(0, (int)Math.Ceiling(values.Length * fraction) - 1)];
                return new { Count = values.Length, P50 = Percentile(.5), P95 = Percentile(.95), P99 = Percentile(.99), Max = values[values.Length - 1] };
            }),
            Trace = includeTrace ? trace.ToArray() : Array.Empty<object>(), SampleLimit = 8192, TraceLimit = 10000
        };
    }
}

internal sealed class TestHarness
{
    private sealed class Packet
    {
        public double Due, Created;
        public long Sequence;
        public Action Deliver = null!;
        public string Method = "";
        public long Peer;
        public int Bytes;
    }
    public readonly TestSettings Settings;
    public readonly LabMetrics Metrics = new LabMetrics();
    private readonly Func<double> clock;
    private readonly Random random;
    private readonly SortedSet<Packet> scheduled = new SortedSet<Packet>(Comparer<Packet>.Create((a, b) =>
    {
        var comparison = a.Due.CompareTo(b.Due);
        return comparison != 0 ? comparison : a.Sequence.CompareTo(b.Sequence);
    }));
    private readonly List<Packet> held = new List<Packet>();
    private readonly Dictionary<string, double> lanes = new Dictionary<string, double>();
    private readonly Dictionary<string, double> bandwidth = new Dictionary<string, double>();
    private readonly Dictionary<long, (double Time, string Method)> pending = new Dictionary<long, (double, string)>();
    private const int PendingLimit = 10000;
    private readonly double started;
    private long sequence;
    private double outageUntil;
    public string HoldNext = "", DropNext = ""; // exact method or Request / Reply
    // Offline runners can reject disconnected logical peers and check an
    // independent model at the exact point a request enters the ordered worker.
    public Func<long, string, byte[], bool>? AdmitServerRequest = null;
    public int Queued => scheduled.Count;
    public int Held => held.Count;
    public int Pending => pending.Count;
    public int PendingRefreshes => pending.Values.Count(p => p.Method == "List");
    public int PendingMutations => Pending - PendingRefreshes;
    public double Now => clock();
    public double OldestMs => scheduled.Concat(held).Select(p => (Now - p.Created) * 1000).DefaultIfEmpty(0).Max();
    public double OldestPendingMs => pending.Values.Select(p => (Now - p.Time) * 1000).DefaultIfEmpty(0).Max();

    public TestHarness(TestSettings settings, Func<double>? clock = null)
    {
        Settings = settings;
        random = new Random(settings.Seed);
        this.clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        started = Now;
    }

    public void Start(long id, string method)
    {
        if (pending.Count >= PendingLimit && !pending.ContainsKey(id))
        {
            var oldest = pending.OrderBy(p => p.Value.Time).First();
            pending.Remove(oldest.Key);
            Metrics.Add("correlationEvicted");
            Metrics.Event(new { Kind = "correlationEvicted", Id = oldest.Key, Time = Now - started });
        }
        pending[id] = (Now, method);
        Metrics.Add("requests." + method.ToLowerInvariant());
        Metrics.Event(new { Kind = "requestStarted", Id = id, Method = method, Time = Now - started });
    }
    public void Complete(long id, string error)
    {
        if (!pending.TryGetValue(id, out var start)) return;
        pending.Remove(id);
        if (error.Length == 0) Metrics.Observe("roundTripMs", (Now - start.Time) * 1000);
        Metrics.Add(error.Length == 0 ? "completed" : "rejected");
        Metrics.Event(new { Kind = "requestCompleted", Id = id, Error = error, Time = Now - started });
    }
    public void ExpireRefresh(long id, string reason)
    {
        if (pending.TryGetValue(id, out var request) && request.Method == "List") pending.Remove(id);
        Metrics.Add("refreshExpired");
        if (reason == "timeout") Metrics.Add("refreshTimeouts");
        Metrics.Event(new { Kind = "refreshExpired", Id = id, Outcome = reason, Time = Now - started });
    }
    public void RefreshRetry(long id)
    {
        Metrics.Add("refreshRetries");
        Metrics.Event(new { Kind = "refreshRetry", Id = id, Time = Now - started });
    }
    public void IgnoreRefresh(long id)
    {
        Metrics.Add("ignoredRefreshReplies");
        Metrics.Event(new { Kind = "ignoredRefreshReply", Id = id, Time = Now - started });
    }
    private static bool Matches(string filter, string method) => filter == method ||
        filter == (method.EndsWith("Reply", StringComparison.Ordinal) ? "Reply" : "Request");

    public void Schedule(long peer, string method, int bytes, Action deliver)
    {
        var now = Now;
        var reply = method.EndsWith("Reply", StringComparison.Ordinal);
        var affected = Settings.Active && (Settings.TargetPeer == 0 || Settings.TargetPeer == peer);
        Metrics.Add(reply ? "replyBytes" : "requestBytes", bytes);
        if (affected && Matches(DropNext, method))
        {
            DropNext = "";
            Metrics.Add("discarded");
            Metrics.Event(new { Time = now - started, Peer = peer, Method = method, Outcome = "discarded" });
            return;
        }
        if (scheduled.Count + held.Count >= Settings.MaxScheduled)
        {
            Metrics.Add("schedulerOverflow");
            return;
        }
        var lane = peer + ":" + reply;
        var due = now;
        if (affected)
        {
            var delay = reply ? Settings.ReplyDelayMs : Settings.RequestDelayMs;
            due += Math.Max(0, delay + (random.NextDouble() * 2 - 1) * Settings.JitterMs) / 1000;
            if (Settings.BytesPerSecond > 0)
            {
                bandwidth.TryGetValue(lane, out var previous);
                var finish = Math.Max(now, previous) + (double)bytes / Settings.BytesPerSecond;
                bandwidth[lane] = finish;
                due = Math.Max(due, finish);
            }
            due = Math.Max(due, outageUntil);
        }
        if (!Settings.AllowReordering && lanes.TryGetValue(lane, out var last)) due = Math.Max(due, last);
        lanes[lane] = due;
        var packet = new Packet { Due = due, Created = now, Peer = peer, Method = method,
            Bytes = bytes, Sequence = ++sequence, Deliver = deliver };
        if (affected && Matches(HoldNext, method)) { HoldNext = ""; held.Add(packet); }
        else scheduled.Add(packet);
        Metrics.Event(new { Time = now - started, Peer = peer, Method = method, Bytes = bytes, Due = due - started, packet.Sequence });
    }

    public void Outage(double seconds) => outageUntil = Math.Max(outageUntil, Now + Math.Max(0, seconds));
    public void Release()
    {
        foreach (var packet in held) { packet.Due = Now; scheduled.Add(packet); }
        held.Clear();
    }
    public void DisableFaults()
    {
        Settings.Active = false;
        Settings.ProcessingDelayMs = Settings.SaveDelayMs = Settings.SaveFailures = 0;
        HoldNext = DropNext = "";
        outageUntil = 0;
        var packets = scheduled.Concat(held).OrderBy(p => p.Sequence).ToArray();
        scheduled.Clear(); held.Clear(); lanes.Clear(); bandwidth.Clear();
        foreach (var packet in packets) { packet.Due = Now; scheduled.Add(packet); }
    }
    public void Pump(int maximum = 32)
    {
        while (maximum-- > 0 && scheduled.Count != 0)
        {
            var packet = scheduled.Min!;
            if (packet.Due > Now) break;
            if (Settings.Active && (Settings.TargetPeer == 0 || Settings.TargetPeer == packet.Peer) && outageUntil > Now)
            {
                scheduled.Remove(packet);
                packet.Due = outageUntil;
                scheduled.Add(packet);
                continue;
            }
            scheduled.Remove(packet);
            Metrics.Observe("deliveryDelayMs", (Now - packet.Created) * 1000);
            packet.Deliver();
            Metrics.Add("delivered");
        }
    }
    public object Snapshot(bool includeTrace = true) => new { Settings = JObject.FromObject(Settings), ElapsedSeconds = Now - started, Queued, Held, Pending, PendingRefreshes, PendingMutations, PendingLimit,
        OldestMs, OldestPendingMs, Metrics = Metrics.Snapshot(includeTrace) };
    public void Export(string path, object? context = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonConvert.SerializeObject(new { Run = Snapshot(), Context = context }, Formatting.Indented));
    }
    public void Cancel()
    {
        Metrics.Add("cancelledOnDisconnect", scheduled.Count + held.Count);
        scheduled.Clear(); held.Clear(); lanes.Clear(); bandwidth.Clear();
    }
}
