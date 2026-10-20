using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ThorsKist.Sync;

namespace ThorsKist
{
    internal static partial class MainThreadProfiler
    {
        // Allocate the event buffer on a worker before F4, never during a
        // transfer or frame probe. One bounded capture, no event streaming IO.
        private static Task<JournalDiagnostics.Capture> preparedJournalTrace = Task.Run(() => JournalDiagnostics.PrepareCapture());
        private static bool StartJournalTrace(int mainThread)
        {
            if (preparedJournalTrace.Status != TaskStatus.RanToCompletion) return false;
            JournalDiagnostics.StartCapture(preparedJournalTrace.Result, mainThread); return true;
        }
        private static string Csv(object value)
        { string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        internal static void ExportJournalTrace(JournalDiagnostics.Capture trace, string path, Action<string> logger)
        {
            try
            {
                Directory.CreateDirectory(path);
                var events = trace.Snapshot().OrderBy(e => e.Start).ThenBy(e => e.Id).ToArray();
                var csv = new StringBuilder("span,stage,operation,request,sequence,kind,expected_thread,start_thread,end_thread,start_on_main,end_on_main,thread_violation,begin_frame,count,start_ms,duration_ms\n");
                foreach (var e in events)
                    csv.Append(e.Id).Append(',').Append(Csv(e.Stage)).Append(',').Append(Csv(e.Correlation.Operation)).Append(',')
                        .Append(e.Correlation.Request.ToString("N")).Append(',').Append(e.Correlation.Sequence).Append(',').Append(e.Kind).Append(',').Append(e.Expected).Append(',')
                        .Append(e.StartThread).Append(',').Append(e.EndThread).Append(',').Append(e.StartThread == trace.MainThread ? 1 : 0).Append(',')
                        .Append(e.EndThread == trace.MainThread ? 1 : 0).Append(',').Append(trace.ThreadViolation(e) ? 1 : 0).Append(',').Append(e.Frame).Append(',').Append(e.Count).Append(',')
                        .Append(Ms(e.Start - trace.StartTicks)).Append(',').Append(Ms(e.End - e.Start)).AppendLine();
                File.WriteAllText(Path.Combine(path, "journal-events.csv"), csv.ToString());
                var stages = new StringBuilder("stage,operation,kind,expected_thread,calls,total_ms,mean_ms,p50_ms,p95_ms,p99_ms,max_ms,main_work_over_1ms,thread_violations,start_threads,end_threads\n");
                foreach (var group in events.GroupBy(e => (e.Stage, e.Correlation.Operation, e.Kind, e.Expected)).OrderBy(g => g.Key.Stage).ThenBy(g => g.Key.Operation))
                {
                    var values = group.Select(e => e.End - e.Start).OrderBy(t => t).ToArray();
                    long Percentile(double p) => values[Math.Max(0, (int)Math.Ceiling(values.Length * p) - 1)];
                    stages.Append(Csv(group.Key.Stage)).Append(',').Append(Csv(group.Key.Operation)).Append(',').Append(group.Key.Kind).Append(',').Append(group.Key.Expected).Append(',')
                        .Append(values.Length).Append(',').Append(Ms(values.Sum())).Append(',').Append(Ms(values.Sum() / values.Length)).Append(',')
                        .Append(Ms(Percentile(.5))).Append(',').Append(Ms(Percentile(.95))).Append(',').Append(Ms(Percentile(.99))).Append(',').Append(Ms(values.Last())).Append(',')
                        .Append(group.Count(e => e.Kind == JournalTimingKind.Work && e.StartThread == trace.MainThread && Milliseconds(e.End - e.Start) > 1)).Append(',')
                        .Append(group.Count(trace.ThreadViolation)).Append(',').Append(Csv(string.Join(";", group.Select(e => e.StartThread).Distinct().OrderBy(t => t)))).Append(',')
                        .Append(Csv(string.Join(";", group.Select(e => e.EndThread).Distinct().OrderBy(t => t)))).AppendLine();
                }
                File.WriteAllText(Path.Combine(path, "journal-stages.csv"), stages.ToString());
                int violations = events.Count(trace.ThreadViolation);
                var main = events.Where(e => e.Kind == JournalTimingKind.Work && e.StartThread == trace.MainThread).ToArray();
                string summary = "Journal transfer diagnostics; Kist drawing is outside this trace.\n"
                    + "started_utc=" + trace.StartedUtc.ToString("O") + " main_thread=" + trace.MainThread + " stopwatch_frequency=" + Stopwatch.Frequency + "\n"
                    + "duration_ms=" + Ms(trace.StopTicks - trace.StartTicks) + " events=" + events.Length + " capacity=" + trace.Capacity + " dropped=" + trace.Dropped
                    + " started_spans=" + trace.StartedSpans + " incomplete_or_unpublished=" + Math.Max(0, trace.StartedSpans - events.Length - trace.Dropped) + "\n"
                    + "thread_violations=" + violations + " main_transfer_work_over_1ms=" + main.Count(e => Milliseconds(e.End - e.Start) > 1)
                    + " main_transfer_work_max_ms=" + Ms(main.Length == 0 ? 0 : main.Max(e => e.End - e.Start)) + "\n"
                    + "Work is synchronous elapsed time on its recorded thread; Wait/Elapsed can cross awaits and are not main-thread blocking time.\n"
                    + "Overlapping/nested stages must not be added. Counts represent rows/items/bytes according to stage. Frame is the current main frame at span start.\n"
                    + "Request IDs and sequence markers join submissions, server replies, verification, effects and acknowledgments. No item payloads or account identifiers are recorded.\n"
                    + "Events finishing after capture stops are excluded; started/completed differences disclose in-flight spans. A full buffer drops new completed events.\n";
                File.WriteAllText(Path.Combine(path, "journal-summary.txt"), summary);
                logger("M7 JOURNAL TIMING SAVED " + path + "; events=" + events.Length + " dropped=" + trace.Dropped + " thread_violations=" + violations);
            }
            catch (Exception error) { logger("M7 JOURNAL TIMING EXPORT FAIL " + error); }
        }
    }
}
