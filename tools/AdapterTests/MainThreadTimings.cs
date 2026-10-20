using System;
using System.Collections.Generic;

namespace ThorsKist.Diagnostics
{
    // Unity-independent aggregation. Scope nesting is main-thread-only; nested
    // method totals are diagnostic breakdowns, never summed as frame blocking.
    internal sealed class MainThreadTimings
    {
        internal sealed class Metric
        {
            internal readonly string Name;
            internal readonly bool Kist;
            internal readonly long[] Samples;
            internal long Calls, Inclusive, Exclusive, Maximum;
            internal Metric(string name, bool kist, int samples) { Name = name; Kist = kist; Samples = new long[samples]; }
        }
        internal struct Frame
        {
            internal int Id, Gc0, Gc1, Gc2, Stacks, Roots;
            internal long Start, Wall, Kist, Overhead;
            internal bool UiOpen, Complete;
        }
        internal struct Scope
        {
            internal int Depth, Metric;
            internal long Start;
            internal bool Active;
        }
        internal readonly Metric[] Metrics;
        internal readonly Frame[] Frames;
        internal readonly long[][] MethodFrames;
        internal int FrameCount { get; private set; }
        internal int InvalidNesting { get; private set; }
        internal bool Full { get; private set; }
        private readonly long[] children = new long[128];
        private readonly int[] metricStack = new int[128];
        private int depth, kistDepth;
        private long kistStart;
        internal MainThreadTimings(string[] names, bool[] kist, int maxFrames = 30000, int maxSamples = 65536)
        {
            if (names.Length != kist.Length || maxFrames <= 0 || maxSamples <= 0) throw new ArgumentException("Invalid timing bounds");
            Metrics = new Metric[names.Length]; Frames = new Frame[maxFrames]; MethodFrames = new long[names.Length][];
            for (int i = 0; i < names.Length; i++) { Metrics[i] = new Metric(names[i], kist[i], maxSamples); MethodFrames[i] = new long[maxFrames]; }
        }
        internal void Advance(int id, long now, bool uiOpen, int stacks, int gc0, int gc1, int gc2)
        {
            if (Full || (FrameCount != 0 && Frames[FrameCount - 1].Id == id)) return;
            if (depth != 0) { InvalidNesting++; depth = kistDepth = 0; }
            if (FrameCount != 0)
            {
                int old = FrameCount - 1;
                Frames[old].Wall = Math.Max(0, now - Frames[old].Start); Frames[old].Complete = true;
                Frames[old].Gc0 = gc0 - Frames[old].Gc0; Frames[old].Gc1 = gc1 - Frames[old].Gc1; Frames[old].Gc2 = gc2 - Frames[old].Gc2;
            }
            if (FrameCount == Frames.Length) { Full = true; return; }
            Frames[FrameCount++] = new Frame { Id = id, Start = now, UiOpen = uiOpen, Stacks = stacks, Gc0 = gc0, Gc1 = gc1, Gc2 = gc2 };
        }
        internal Scope Begin(int metric, long now)
        {
            if (Full || FrameCount == 0) return default;
            if (depth == children.Length) { InvalidNesting++; return default; }
            int slot = depth++; children[slot] = 0; metricStack[slot] = metric;
            if (Metrics[metric].Kist && kistDepth++ == 0) { kistStart = now; Frames[FrameCount - 1].Roots++; }
            return new Scope { Active = true, Depth = slot, Metric = metric, Start = now };
        }
        internal void End(Scope scope, long now)
        {
            if (!scope.Active) return;
            if (depth != scope.Depth + 1 || metricStack[scope.Depth] != scope.Metric)
            { InvalidNesting++; depth = kistDepth = 0; return; }
            long elapsed = Math.Max(0, now - scope.Start), exclusive = Math.Max(0, elapsed - children[scope.Depth]);
            depth--;
            if (depth > 0) children[depth - 1] += elapsed;
            var metric = Metrics[scope.Metric];
            metric.Samples[(int)(metric.Calls % metric.Samples.Length)] = elapsed;
            metric.Calls++; metric.Inclusive += elapsed; metric.Exclusive += exclusive; metric.Maximum = Math.Max(metric.Maximum, elapsed);
            MethodFrames[scope.Metric][FrameCount - 1] += elapsed;
            if (metric.Kist && --kistDepth == 0) Frames[FrameCount - 1].Kist += Math.Max(0, now - kistStart);
        }
        internal void Overhead(long ticks)
        { if (FrameCount != 0 && !Full) Frames[FrameCount - 1].Overhead += Math.Max(0, ticks); }
        internal static long Percentile(long[] values, int count, double percentile)
        {
            if (count == 0) return 0;
            var sorted = new long[count]; Array.Copy(values, sorted, count); Array.Sort(sorted);
            return sorted[Math.Max(0, Math.Min(count - 1, (int)Math.Ceiling(count * percentile) - 1))];
        }
    }
}
