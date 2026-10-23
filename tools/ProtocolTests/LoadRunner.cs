using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharedInventory;

internal sealed class LoadScenario
{
    public string Name = "baseline";
    public int Seed = 12345, Peers = 10, Entries = 385, BatchSize = 4, Amount = 1;
    public long InitialStock = 10000;
    public double DurationSeconds = 10, RequestsPerSecond = 100, RampSeconds = 0, DrainSeconds = 15;
    public int BurstRequests = 0, ListWeight = 40, DepositWeight = 20, WithdrawalWeight = 20, BatchWeight = 20;
    public bool MenuPolling = false, AllowUnresolved = false;
    public double HotspotFraction = .8, MaxP95Ms = 0, MaxBaselineRatio = 0;
    public int MaxWorkerQueue = 0;
    public TestSettings Network = new TestSettings();
    public List<LoadStep> Steps = new List<LoadStep>();
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Network == null || Steps == null ||
            Peers < 1 || Peers > 10000 || Entries < 1 || Entries > 100000 || BatchSize < 1 || BatchSize > Entries ||
            Amount < 1 || InitialStock < 0 || InitialStock > long.MaxValue / 2 ||
            !double.IsFinite(DurationSeconds) || DurationSeconds <= 0 || DurationSeconds > 3600 ||
            !double.IsFinite(RequestsPerSecond) || RequestsPerSecond < 0 || RequestsPerSecond > 100000 ||
            !double.IsFinite(RampSeconds) || RampSeconds < 0 || !double.IsFinite(DrainSeconds) || DrainSeconds <= 0 || DrainSeconds > 600 ||
            BurstRequests < 0 || BurstRequests > 100000 || !double.IsFinite(HotspotFraction) || HotspotFraction < 0 || HotspotFraction > 1 ||
            !double.IsFinite(MaxP95Ms) || MaxP95Ms < 0 || !double.IsFinite(MaxBaselineRatio) || MaxBaselineRatio < 0 || MaxWorkerQueue < 0 ||
            ListWeight < 0 || DepositWeight < 0 || WithdrawalWeight < 0 || BatchWeight < 0 ||
            (long)ListWeight + DepositWeight + WithdrawalWeight + BatchWeight is <= 0 or > int.MaxValue ||
            Network.RequestDelayMs < 0 || Network.ReplyDelayMs < 0 || Network.JitterMs < 0 || Network.BytesPerSecond < 0 ||
            Network.ProcessingDelayMs < 0 || Network.SaveDelayMs < 0 || Network.SaveFailures < 0 || Network.MaxScheduled < 1 ||
            Network.RequestDelayMs > 60000 || Network.ReplyDelayMs > 60000 || Network.ProcessingDelayMs > 60000 || Network.SaveDelayMs > 60000 ||
            (Network.TargetPeer != 0 && (Network.TargetPeer < 2 || Network.TargetPeer >= Peers + 2)))
            throw new ArgumentException("Invalid or out-of-range load scenario setting.");
        var actions = new[] { "hold", "drop", "release", "outage", "disable", "disconnect", "reconnect", "worker-delay", "save-delay", "save-failure", "list", "deposit", "withdrawal", "batch" };
        foreach (var step in Steps)
            if (!double.IsFinite(step.AtSeconds) || step.AtSeconds < 0 || step.AtSeconds > DurationSeconds ||
                !actions.Contains(step.Action) || step.Value < 0 || step.Value > 60000 || step.Peer < 2 || step.Peer >= Peers + 2 ||
                ((step.Action == "hold" || step.Action == "drop") && !new[] { "Request", "Reply", "List", "Deposit", "Withdrawal", "ListReply", "DepositReply", "WithdrawalReply" }.Contains(step.Method)))
                throw new ArgumentException("Invalid scenario step: " + step.Action);
    }
}

internal sealed class LoadStep
{
    public double AtSeconds = 0;
    public string Action = "", Method = "Reply";
    public long Peer = 2;
    public int Value = 0;
}

// Logical peers replace only routing. The actual server handlers, ZPackage
// codecs, ordered worker, and database persistence execute unchanged.
internal static class LoadRunner
{
    private sealed class Request
    {
        public long Id, Peer;
        public string Method = "", Item = "";
        public long Amount;
        public int Quality;
        public Dictionary<string, long>? Batch;
    }
    private sealed class Expected
    {
        public bool Error;
        public long Amount, Total;
        public int Quality;
        public Dictionary<string, long>? Items;
        public Dictionary<string, int>? Qualities;
    }
    public static int Run(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Usage: --load scenario.json [--output report.json] [--baseline report.json]");
        var scenario = JsonConvert.DeserializeObject<LoadScenario>(File.ReadAllText(args[1]),
            new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }) ?? throw new ArgumentException("Missing scenario");
        scenario.Validate();
        var configuration = JObject.FromObject(scenario);
        var safeName = new string(scenario.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var output = Path.GetFullPath(Path.Combine(".work", "load-reports", safeName + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json"));
        string? baseline = null;
        for (var i = 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Missing option value");
            if (args[i] == "--output") output = Path.GetFullPath(args[i + 1]);
            else if (args[i] == "--baseline") baseline = args[i + 1];
            else throw new ArgumentException("Unknown option " + args[i]);
        }
        if (baseline != null && string.Equals(Path.GetFullPath(baseline), output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Baseline and output must be different files.");
        // Never accept a database path from configuration. Only a fresh temp root.
        var root = Path.Combine(Path.GetTempPath(), "sharedinventory-load-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "SharedInventory"));
        var oldConfig = Paths.ConfigPath;
        Paths.ConfigPath = root;
        RpcApi? api = null;
        var lab = new TestHarness(scenario.Network);
        try
        {
            var random = new Random(scenario.Seed);
            var initial = Enumerable.Range(0, scenario.Entries).ToDictionary(i => "Lab" + i.ToString("D6"), _ => scenario.InitialStock);
            var expectedCounts = initial.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
            var expectedQuality = expectedCounts.ToDictionary(p => p.Key, _ => 1);
            var database = Path.Combine(root, "SharedInventory", "1.json");
            File.WriteAllText(database, JsonConvert.SerializeObject(initial));
            var requests = new Dictionary<long, Request>();
            var expectedReplies = new Dictionary<long, Expected>();
            var disconnected = new HashSet<long>();
            var violations = new List<string>();
            long nextId = 0, issued = 0, completed = 0, rejected = 0, rejectedBatches = 0, unpaidUnits = 0;
            var log = new ManualLogSource();
            var router = new ZRoutedRpc();
            ListReply? final = null;
            api = new RpcApi(router, log, reply => final = reply, _ => { }, _ => { }, _ => { }, lab);

            Expected Model(Request request)
            {
                long Total(string item) => expectedCounts.TryGetValue(item, out var value) ? value : 0;
                int Quality(string item) => expectedQuality.TryGetValue(item, out var value) ? value : 1;
                void Set(string item, long total, int quality)
                {
                    if (total == 0) { expectedCounts.Remove(item); expectedQuality.Remove(item); }
                    else { expectedCounts[item] = total; expectedQuality[item] = quality; }
                }
                if (request.Method == "List") return new Expected { Items = new Dictionary<string, long>(expectedCounts), Qualities = new Dictionary<string, int>(expectedQuality) };
                if (request.Batch != null)
                {
                    var error = request.Batch.Any(p => Total(p.Key) < p.Value);
                    if (error) { rejectedBatches++; unpaidUnits += request.Batch.Values.Sum(); return new Expected { Error = true }; }
                    foreach (var pair in request.Batch) Set(pair.Key, Total(pair.Key) - pair.Value, Quality(pair.Key));
                    return new Expected { Items = request.Batch.ToDictionary(p => p.Key, p => Total(p.Key)),
                        Qualities = request.Batch.ToDictionary(p => p.Key, p => Quality(p.Key)) };
                }
                var quality = Quality(request.Item);
                var amount = request.Method == "Deposit" ? request.Amount : Math.Min(request.Amount, Total(request.Item));
                var total = request.Method == "Deposit" ? checked(Total(request.Item) + amount) : Total(request.Item) - amount;
                if (request.Method == "Deposit") quality = Math.Max(quality, request.Quality);
                Set(request.Item, total, quality);
                return new Expected { Amount = amount, Total = total, Quality = quality };
            }

            lab.AdmitServerRequest = (peer, method, bytes) =>
            {
                if (peer == 1) return true; // final validation uses the host client
                if (disconnected.Contains(peer)) { lab.Metrics.Add("disconnectedRequests"); return false; }
                var package = new ZPackage(bytes);
                if (method == "Withdrawal") package.ReadBool();
                var id = package.ReadLong();
                expectedReplies.Add(id, Model(requests[id]));
                return true;
            };

            void Issue(long peer, string? operation = null)
            {
                if (disconnected.Contains(peer)) return;
                var choice = random.Next(scenario.ListWeight + scenario.DepositWeight + scenario.WithdrawalWeight + scenario.BatchWeight);
                var method = operation ?? (choice < scenario.ListWeight ? "list" :
                    choice < scenario.ListWeight + scenario.DepositWeight ? "deposit" :
                    choice < scenario.ListWeight + scenario.DepositWeight + scenario.WithdrawalWeight ? "withdrawal" : "batch");
                var index = random.NextDouble() < scenario.HotspotFraction ? 0 : random.Next(scenario.Entries);
                var request = new Request { Id = ++nextId, Peer = peer, Amount = scenario.Amount, Quality = random.Next(1, 5),
                    Item = "Lab" + index.ToString("D6"), Method = method == "list" ? "List" : method == "deposit" ? "Deposit" : "Withdrawal" };
                if (method == "batch") request.Batch = Enumerable.Range(0, scenario.BatchSize)
                    .ToDictionary(j => "Lab" + ((index + j) % scenario.Entries).ToString("D6"), _ => (long)scenario.Amount);
                var package = new ZPackage();
                if (request.Method == "Withdrawal") package.Write(request.Batch != null);
                package.Write(request.Id);
                if (request.Batch != null)
                {
                    package.Write(request.Batch.Count);
                    foreach (var pair in request.Batch) { package.Write(pair.Key); package.Write(pair.Value); }
                }
                else if (request.Method != "List")
                {
                    package.Write(request.Item); package.Write(request.Amount);
                    if (request.Method == "Deposit") package.Write(request.Quality);
                }
                requests.Add(request.Id, request);
                issued++;
                lab.Start(request.Id, method);
                lab.Metrics.Event(new { Kind = "issued", request.Id, request.Peer, request.Method, request.Item, request.Amount, request.Quality, request.Batch });
                router.Inject(peer, "SharedInventory.v3." + request.Method, package);
            }

            router.ReplyReceived = (peer, name, package) =>
            {
                if (disconnected.Contains(peer)) { lab.Metrics.Add("disconnectedReplies"); return; }
                var batch = false;
                if (name.EndsWith("WithdrawalReply", StringComparison.Ordinal))
                {
                    batch = package.ReadBool();
                    package = new ZPackage(package.ReadByteArray());
                }
                var id = package.ReadLong();
                var error = package.ReadString();
                if (!requests.TryGetValue(id, out var request) || !expectedReplies.TryGetValue(id, out var expected))
                { violations.Add("Unexpected reply " + id); return; }
                requests.Remove(id);
                expectedReplies.Remove(id);
                completed++;
                if (error.Length != 0) rejected++;
                lab.Complete(id, error);
                if (expected.Error != (error.Length != 0)) { violations.Add("Wrong success/error for " + id); return; }
                if (expected.Error) return;
                if (request.Method == "List" || batch)
                {
                    var count = package.ReadInt();
                    var items = new Dictionary<string, long>();
                    var qualities = new Dictionary<string, int>();
                    for (var i = 0; i < count; ++i) { var item = package.ReadString(); items.Add(item, package.ReadLong()); qualities.Add(item, package.ReadInt()); }
                    if (!Same(expected.Items!, items) || !Same(expected.Qualities!, qualities)) violations.Add("Snapshot mismatch for " + id);
                }
                else
                {
                    var item = package.ReadString(); var amount = package.ReadLong(); var total = package.ReadLong(); var quality = package.ReadInt();
                    if (item != request.Item || amount != expected.Amount || total != expected.Total || quality != expected.Quality)
                        violations.Add("Transfer mismatch for " + id);
                }
            };

            var steps = scenario.Steps.OrderBy(s => s.AtSeconds).ToArray();
            var stepIndex = 0;
            var watch = Stopwatch.StartNew();
            double nextPoll = 0, lastTime = 0, credit = 0;
            var burst = false;
            var maxWorker = 0;
            var lastProgress = 0.0;
            while (watch.Elapsed.TotalSeconds < scenario.DurationSeconds)
            {
                var elapsed = watch.Elapsed.TotalSeconds;
                while (stepIndex < steps.Length && steps[stepIndex].AtSeconds <= elapsed)
                {
                    var step = steps[stepIndex++];
                    lab.Metrics.Event(new { Kind = "step", step.Action, step.AtSeconds, step.Peer, step.Value });
                    switch (step.Action)
                    {
                        case "hold": lab.HoldNext = step.Method; scenario.Network.Active = true; break;
                        case "drop": lab.DropNext = step.Method; scenario.Network.Active = true; break;
                        case "release": lab.Release(); break;
                        case "outage": scenario.Network.Active = true; lab.Outage(step.Value); break;
                        case "disable": lab.DisableFaults(); break;
                        case "disconnect": disconnected.Add(step.Peer); break;
                        case "reconnect": disconnected.Remove(step.Peer); break;
                        case "worker-delay": scenario.Network.Active = true; scenario.Network.ProcessingDelayMs = step.Value; break;
                        case "save-delay": scenario.Network.Active = true; scenario.Network.SaveDelayMs = step.Value; break;
                        case "save-failure": scenario.Network.Active = true; scenario.Network.SaveFailures = step.Value; break;
                        default: Issue(step.Peer, step.Action); break;
                    }
                }
                if (!burst) { for (var i = 0; i < scenario.BurstRequests; ++i) Issue(2 + i % scenario.Peers); burst = true; }
                if (scenario.MenuPolling && elapsed >= nextPoll)
                {
                    for (var peer = 2; peer < scenario.Peers + 2; ++peer)
                        if (!requests.Values.Any(r => r.Peer == peer && r.Method == "List")) Issue(peer, "list");
                    nextPoll += 2;
                }
                var ramp = scenario.RampSeconds == 0 ? 1 : Math.Min(1, elapsed / scenario.RampSeconds);
                credit += (elapsed - lastTime) * scenario.RequestsPerSecond * ramp;
                lastTime = elapsed;
                var limit = 1000;
                while (credit >= 1 && limit-- > 0) { Issue(random.Next(2, scenario.Peers + 2)); credit--; }
                api.Pump();
                maxWorker = Math.Max(maxWorker, api.WorkerQueueDepth);
                if (elapsed - lastProgress >= 5)
                { System.Console.WriteLine($"{elapsed:F1}s: issued {issued}, replies {completed}, pending {requests.Count}, worker {api.WorkerQueueDepth}"); lastProgress = elapsed; }
                Thread.Sleep(1);
            }
            var drain = Stopwatch.StartNew();
            // Waiting for replies alone would never finish an intentional reply-loss test.
            while (drain.Elapsed.TotalSeconds < scenario.DrainSeconds &&
                (lab.Queued != 0 || api.WorkerQueueDepth != 0 || api.MainQueueDepth != 0 || requests.Count > 0))
            { api.Pump(); maxWorker = Math.Max(maxWorker, api.WorkerQueueDepth); Thread.Sleep(1); }
            drain.Stop(); watch.Stop();
            var beforeCleanup = lab.Snapshot();
            var workloadMetrics = JObject.FromObject(lab.Metrics.Snapshot(false));
            var p95 = workloadMetrics["Samples"]?["roundTripMs"]?["P95"]?.Value<double>() ?? 0;
            var unresolved = requests.Values.Select(r => new { r.Id, r.Peer, r.Method, r.Item,
                ServerReceived = expectedReplies.ContainsKey(r.Id) }).ToArray();
            // Discard test transport backlog, then use an ordered List as a barrier
            // behind every request already received by the server.
            lab.Cancel(); lab.DisableFaults();
            api.List(++nextId);
            var barrier = Stopwatch.StartNew();
            while (final == null && barrier.Elapsed.TotalSeconds < Math.Max(30, scenario.DrainSeconds)) { api.Pump(); Thread.Sleep(1); }
            if (final == null || final.Error.Length != 0) violations.Add("Final server snapshot unavailable");
            else if (!Same(expectedCounts, final.Items) || !Same(expectedQuality, final.Qualities)) violations.Add("Final inventory differs from independent reference model");
            api.Stop(); RpcApi.FinishShutdown(); api = null;
            var persisted = new DatabaseFile(database).Database;
            if (!Same(expectedCounts, persisted.List()) || expectedQuality.Any(p => persisted.Quality(p.Key) != p.Value)) violations.Add("Persisted inventory differs from reference model");
            if (!scenario.AllowUnresolved && unresolved.Length != 0) violations.Add("Unresolved operations: " + unresolved.Length);
            if (scenario.MaxWorkerQueue > 0 && maxWorker > scenario.MaxWorkerQueue) violations.Add("Worker queue threshold exceeded");
            if (log.Errors.Any(error => !(error is InvalidOperationException rejectedError && rejectedError.Message == "Shared materials are no longer available.") &&
                !(error is IOException injectedError && injectedError.Message == "Injected test save failure"))) violations.Add("Unexpected worker or codec error");
            if (scenario.MaxP95Ms > 0 && p95 > scenario.MaxP95Ms) violations.Add("p95 latency threshold exceeded");
            double? baselineP95 = baseline == null ? null : JObject.Parse(File.ReadAllText(baseline))["Context"]?["P95Ms"]?.Value<double>();
            if (baseline != null && baselineP95 == null) violations.Add("Baseline has no p95 latency");
            if (scenario.MaxBaselineRatio > 0 && baselineP95 > 0 && p95 / baselineP95.Value > scenario.MaxBaselineRatio) violations.Add("Baseline latency ratio exceeded");
            lab.Export(output, new { Scenario = configuration, InitialCounts = initial, FinalCounts = final?.Items,
                Issued = issued, Completed = completed, Rejected = rejected, RejectedBatches = rejectedBatches,
                UnpaidSharedUnits = unpaidUnits, Unresolved = unresolved, MaxWorkerQueue = maxWorker,
                P95Ms = p95, BaselineP95Ms = baselineP95, Throughput = completed / watch.Elapsed.TotalSeconds,
                DrainMs = drain.Elapsed.TotalMilliseconds, BeforeCleanup = beforeCleanup,
                WorkerErrors = log.Errors.Select(e => e.ToString()).ToArray(), Violations = violations, Passed = violations.Count == 0,
                Scope = "Logical peers and real server codecs/worker/database; no Unity or real network transport." });
            System.Console.WriteLine($"{(violations.Count == 0 ? "PASS" : "FAIL")} {scenario.Name}: {issued} requests, {completed} replies, {unresolved.Length} unresolved, p95 {p95:F1} ms. Report: {output}");
            foreach (var violation in violations) System.Console.WriteLine("FAIL " + violation);
            return violations.Count == 0 ? 0 : 1;
        }
        finally
        {
            api?.Stop(); RpcApi.FinishShutdown(); Paths.ConfigPath = oldConfig;
            Directory.Delete(root, true);
        }
    }
    private static bool Same<T>(IReadOnlyDictionary<string, T> expected, IReadOnlyDictionary<string, T> actual) =>
        expected.Count == actual.Count && expected.All(p => actual.TryGetValue(p.Key, out var value) && EqualityComparer<T>.Default.Equals(p.Value, value));
}
