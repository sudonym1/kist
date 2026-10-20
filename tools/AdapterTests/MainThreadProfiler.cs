using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using ThorsKist.Diagnostics;
using UnityEngine;

namespace ThorsKist
{
    [HarmonyPatch]
    internal static class MainThreadProfiler
    {
        private static readonly Dictionary<MethodBase, int> Ids = new Dictionary<MethodBase, int>();
        private static readonly List<string> Names = new List<string>();
        private static readonly List<bool> IsKist = new List<bool>();
        private static MainThreadTimings capture;
        private static int owner, warmup, lastFrame = -1;
        private static long started;
        private static string output;
        private static Action<string> report;
        private static bool active;
        private static Task exporting;
        internal static int TargetCount => Ids.Count;
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            if (Ids.Count != 0) return Ids.OrderBy(p => p.Value).Select(p => p.Key).ToArray();
            var targets = new[] {
                (typeof(Main), "Update"),
                (typeof(KistServer), "Tick"), (typeof(KistServer), "EnsureLoaded"), (typeof(KistServer), "Save"),
                (typeof(KistServer), "OnMessage"), (typeof(KistServer), "SendState"),
                (typeof(KistClient), "Tick"), (typeof(KistClient), "OnMessage"), (typeof(KistClient), "ReadState"),
                (typeof(KistClient), "Deposit"), (typeof(KistClient), "Take"),
                (typeof(KistUI), "Tick"), (typeof(KistUI), "Open"), (typeof(KistUI), "Build"),
                (typeof(KistUI), "BuildKistRows"), (typeof(KistUI), "BuildHirdRows"),
                (typeof(KistStock), "Count"), (typeof(KistSort), "Arrange"),
                (typeof(JournalReservationGuard), "Poll"), (typeof(JournalReservationGuard), "Blocks"),
                (typeof(JournalGameplaySession), "Tick"), (typeof(JournalDirectRpc), "Pump"),
                (typeof(JournalGameThread), "Drain"), (typeof(JournalCharacterInventory), "Apply"),
                (typeof(JournalStockProjection), "Drain"), (typeof(JournalStockProjection), "Changed"),
                (typeof(JournalItemCodec), "Capture"), (typeof(JournalItemCodec), "Restore") };
            foreach (var target in targets)
            {
                var method = AccessTools.Method(target.Item1, target.Item2) ?? throw new MissingMethodException(target.Item1.Name, target.Item2);
                if (Ids.ContainsKey(method)) continue;
                Ids.Add(method, Names.Count); Names.Add(target.Item1.Name + "." + target.Item2); IsKist.Add(true);
            }
            // All-game UI layout is a comparison metric, not attributed to Kist.
            var layout = AccessTools.Method(typeof(UnityEngine.UI.CanvasUpdateRegistry), "PerformUpdate");
            if (layout != null) { Ids.Add(layout, Names.Count); Names.Add("UnityUI.AllCanvasLayout"); IsKist.Add(false); }
            return Ids.OrderBy(p => p.Value).Select(p => p.Key).ToArray();
        }
        internal static void Tick(Action<string> logger)
        {
            report = logger;
            if (Input.GetKeyDown(KeyCode.F4))
            {
                if (active) Stop();
                else if (exporting == null || exporting.IsCompleted) Start();
            }
            if (!active) return;
            EnsureFrame();
            if (capture.Full || (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency >= 90) Stop();
        }
        private static void Start()
        {
            owner = Thread.CurrentThread.ManagedThreadId;
            capture = new MainThreadTimings(Names.ToArray(), IsKist.ToArray());
            output = Path.Combine(Paths.BepInExRootPath, "timings", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            warmup = Time.frameCount + 3; lastFrame = -1; started = Stopwatch.GetTimestamp(); active = true;
            report("M7 TIMING START " + output + "; F4 stops capture; automatic limit 90 seconds/30000 frames. First 3 frames excluded.");
            KistClient.Message("Kist timing recording. F4 stops (90 second maximum).");
        }
        private static void EnsureFrame()
        {
            int frame = Time.frameCount;
            if (frame < warmup || frame == lastFrame) return;
            lastFrame = frame;
            int stacks = JournalGameplaySession.Current != null ? JournalGameplaySession.Current.Stock.Count : KistClient.Kist?.NrOfItems() ?? 0;
            capture.Advance(frame, Stopwatch.GetTimestamp(), KistUI.IsOpen, stacks, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        }
        [HarmonyPrefix]
        private static void Prefix(MethodBase __originalMethod, out MainThreadTimings.Scope __state)
        {
            __state = default;
            if (!active || Thread.CurrentThread.ManagedThreadId != owner || Time.frameCount < warmup) return;
            long begin = Stopwatch.GetTimestamp(); EnsureFrame();
            __state = capture.Begin(Ids[__originalMethod], Stopwatch.GetTimestamp());
            capture.Overhead(Stopwatch.GetTimestamp() - begin);
        }
        [HarmonyFinalizer]
        private static void Finalizer(MainThreadTimings.Scope __state)
        {
            if (!__state.Active) return;
            long end = Stopwatch.GetTimestamp(); capture.End(__state, end);
            capture.Overhead(Stopwatch.GetTimestamp() - end);
        }
        internal static void Stop()
        {
            if (!active) return; active = false;
            var data = capture; var path = output; var logger = report;
            exporting = Task.Run(() => Export(data, path, logger));
            KistClient.Message("Kist timing stopped; CSV reports are being written off the game thread.");
        }
        private static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F6", CultureInfo.InvariantCulture);
        private static void Export(MainThreadTimings data, string path, Action<string> logger)
        {
            try
            {
                Directory.CreateDirectory(path);
                var frames = new StringBuilder("frame,complete,frame_wall_ms,kist_union_ms,profiler_bookkeeping_ms,kist_roots,ui_open,stacks,gc0,gc1,gc2");
                foreach (var metric in data.Metrics) frames.Append(',').Append(metric.Name).Append("_inclusive_ms"); frames.AppendLine();
                var union = new List<long>(); var wall = new List<long>(); long overhead = 0;
                for (int i = 0; i < data.FrameCount; i++)
                {
                    var f = data.Frames[i];
                    frames.Append(f.Id).Append(',').Append(f.Complete ? 1 : 0).Append(',').Append(Ms(f.Wall)).Append(',').Append(Ms(f.Kist)).Append(',').Append(Ms(f.Overhead))
                        .Append(',').Append(f.Roots).Append(',').Append(f.UiOpen ? 1 : 0).Append(',').Append(f.Stacks).Append(',').Append(f.Complete ? f.Gc0 : 0).Append(',').Append(f.Complete ? f.Gc1 : 0).Append(',').Append(f.Complete ? f.Gc2 : 0);
                    for (int m = 0; m < data.Metrics.Length; m++) frames.Append(',').Append(Ms(data.MethodFrames[m][i])); frames.AppendLine();
                    if (f.Complete) { union.Add(f.Kist); wall.Add(f.Wall); overhead += f.Overhead; }
                }
                File.WriteAllText(Path.Combine(path, "frames.csv"), frames.ToString());
                var methods = new StringBuilder("method,counts_as_kist,calls,total_inclusive_ms,total_exclusive_ms,mean_inclusive_ms,p50_ms,p95_ms,p99_ms,max_ms,percentile_sample_count\n");
                foreach (var m in data.Metrics)
                {
                    int n = (int)Math.Min(m.Calls, m.Samples.Length);
                    methods.Append(m.Name).Append(',').Append(m.Kist ? 1 : 0).Append(',').Append(m.Calls).Append(',').Append(Ms(m.Inclusive)).Append(',').Append(Ms(m.Exclusive)).Append(',').Append(Ms(m.Calls == 0 ? 0 : m.Inclusive / m.Calls))
                        .Append(',').Append(Ms(MainThreadTimings.Percentile(m.Samples, n, .50))).Append(',').Append(Ms(MainThreadTimings.Percentile(m.Samples, n, .95)))
                        .Append(',').Append(Ms(MainThreadTimings.Percentile(m.Samples, n, .99))).Append(',').Append(Ms(m.Maximum)).Append(',').Append(n).AppendLine();
                }
                File.WriteAllText(Path.Combine(path, "methods.csv"), methods.ToString());
                var u = union.ToArray(); var w = wall.ToArray();
                string summary = "Instrumented synchronous main-thread elapsed time; background workers excluded.\n"
                    + "Kist union counts nested instrumented scopes once. Method inclusive totals must not be added.\n"
                    + "Frame wall interval includes VSync/GPU/OS/other mods. It is not Kist blocking time.\n"
                    + "UnityUI.AllCanvasLayout covers all game UI and is not attributed to Kist.\n"
                    + "Bookkeeping is reported separately; measured spans include a small entry-probe cost. Do not subtract all bookkeeping from union.\n"
                    + "Method percentiles use the last 65536 calls; full totals/max include all calls. Last incomplete frame excluded from frame percentiles.\n"
                    + "Deferred render/layout and uninstrumented Harmony hooks are not fully attributed; this is a measured scope, not total game CPU.\n"
                    + "Frames=" + u.Length + " invalid_nesting=" + data.InvalidNesting + "\n"
                    + "Kist union p50_ms=" + Ms(MainThreadTimings.Percentile(u, u.Length, .50)) + " p95_ms=" + Ms(MainThreadTimings.Percentile(u, u.Length, .95))
                    + " p99_ms=" + Ms(MainThreadTimings.Percentile(u, u.Length, .99)) + " max_ms=" + Ms(u.Length == 0 ? 0 : u.Max()) + "\n"
                    + "Frame wall p95_ms=" + Ms(MainThreadTimings.Percentile(w, w.Length, .95)) + " max_ms=" + Ms(w.Length == 0 ? 0 : w.Max()) + "\n"
                    + "Profiler bookkeeping total_ms=" + Ms(overhead) + "\n";
                File.WriteAllText(Path.Combine(path, "summary.txt"), summary);
                logger("M7 TIMING SAVED " + path + "; " + summary.Replace('\n', ' '));
            }
            catch (Exception error) { logger("M7 TIMING EXPORT FAIL " + error); }
        }
    }
}
