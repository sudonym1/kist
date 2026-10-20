using System;
using ThorsKist.Diagnostics;

static class TimingTests
{
    static int checks;
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    public static int Run()
    {
        checks = 0;
        var t = new MainThreadTimings(new[] { "outer", "inner", "layout" }, new[] { true, true, false }, 2, 3);
        Check(!t.Begin(0, 0).Active, "No scope before a frame");
        t.Advance(10, 100, true, 590, 1, 2, 3);
        var outer = t.Begin(0, 110);
        var inner = t.Begin(1, 120); t.End(inner, 140);
        t.End(outer, 160);
        var layout = t.Begin(2, 170);
        inner = t.Begin(1, 180); t.End(inner, 190);
        t.End(layout, 210);
        t.Overhead(7); t.Overhead(-1);
        t.Advance(10, 220, false, 0, 8, 8, 8);
        Check(t.FrameCount == 1 && !t.Frames[0].Complete, "Same-frame calls do not advance");
        t.Advance(11, 250, false, 587, 3, 3, 3);
        var f = t.Frames[0];
        Check(f.Kist == 60 && f.Roots == 2, "Nested Kist calls counted once; Kist inside control remains attributed");
        Check(f.Wall == 150 && f.Complete && f.Gc0 == 2 && f.Gc1 == 1 && f.Gc2 == 0, "Frame wall and collection deltas");
        Check(f.Stacks == 590 && f.UiOpen && f.Overhead == 7, "Frame context and overhead retained");
        Check(t.Metrics[0].Inclusive == 50 && t.Metrics[0].Exclusive == 30, "Parent exclusive excludes child");
        Check(t.Metrics[1].Inclusive == 30 && t.Metrics[1].Exclusive == 30 && t.Metrics[1].Calls == 2, "Independent child totals");
        Check(t.Metrics[2].Inclusive == 40 && t.Metrics[2].Exclusive == 30, "Non-Kist control timing");
        Check(t.MethodFrames[0][0] == 50 && t.MethodFrames[1][0] == 30, "Frame method breakdown");
        for (int i = 1; i <= 5; i++) { var s = t.Begin(0, 250); t.End(s, 250 + i); }
        Check(t.Metrics[0].Calls == 6 && t.Metrics[0].Inclusive == 65 && t.Metrics[0].Maximum == 50, "Totals retain calls beyond sample capacity");
        Check(MainThreadTimings.Percentile(t.Metrics[0].Samples, 3, .50) == 4 && MainThreadTimings.Percentile(t.Metrics[0].Samples, 3, .99) == 5, "Ring retains latest samples");
        t.Advance(12, 300, false, 0, 3, 3, 3);
        Check(t.Full && t.FrameCount == 2 && t.Frames[1].Complete && !t.Begin(0, 301).Active, "Frame cap completes final frame and stops recording");
        var bad = new MainThreadTimings(new[] { "kist" }, new[] { true }, 4, 2);
        bad.Advance(1, 0, false, 0, 0, 0, 0);
        var a = bad.Begin(0, 1); var b = bad.Begin(0, 2); bad.End(a, 3);
        Check(bad.InvalidNesting == 1, "Reject out-of-order scope exits");
        bad.End(b, 4);
        a = bad.Begin(0, 5); bad.End(a, 7);
        Check(bad.Frames[0].Kist == 2, "Collector recovers after invalid nesting");
        bad.Begin(0, 8); bad.Advance(2, 10, false, 0, 0, 0, 0);
        Check(bad.InvalidNesting == 3, "Cross-frame active scope is flagged");
        Check(MainThreadTimings.Percentile(Array.Empty<long>(), 0, .99) == 0, "Empty percentile");
        Check(MainThreadTimings.Percentile(new long[] { 9, 1, 3, 2 }, 4, .50) == 2, "Nearest-rank percentile");
        Console.WriteLine($"PASS {checks} main-thread timing assertions"); return checks;
    }
}
