using System;
using System.Linq;
using SharedInventory;

internal static partial class Program
{
    private static RitualMember[] GatheringRoster() => new[] {
        new RitualMember { Peer = 1, Life = "host-life", Name = "Host" },
        new RitualMember { Peer = 2, Life = "remote-life", Name = "Remote" }
    };
    private static void RitualTests()
    {
        Test("Missing connected players are named before a countdown can start", () =>
        {
            var roster = GatheringRoster(); roster[1].Unavailable = "outside the circle";
            var ritual = new RitualGathering();
            Equal(false, ritual.Start(10, roster, out var error));
            Equal(RitualGathering.MissingPrefix + "Remote (outside the circle).", error);
            Equal(false, ritual.Active);
        });
        Test("Dead and loading players remain required, including the host", () =>
        {
            var roster = GatheringRoster(); roster[0].Unavailable = "dead"; roster[1].Life = "";
            var ritual = new RitualGathering();
            Equal(false, ritual.Start(0, roster, out var error));
            Equal(true, error.Contains("Host (dead)") && error.Contains("Remote (loading)"));
        });
        Test("Solo gathering is allowed and an empty server cannot begin", () =>
        {
            var ritual = new RitualGathering();
            Equal(false, ritual.Start(0, Array.Empty<RitualMember>(), out _));
            Equal(true, ritual.Start(0, GatheringRoster().Take(1).ToArray(), out _));
            Equal(1, ritual.Members.Count);
        });
        Test("Countdown uses five one-second steps and commits once at exactly five seconds", () =>
        {
            var ritual = new RitualGathering(); var roster = GatheringRoster();
            Equal(true, ritual.Start(10, roster, out _));
            for (var i = 0; i < 5; i++)
            {
                Equal(5 - i, RitualGathering.Number(10 + i, 10));
                Equal(false, ritual.Advance(10 + i + .999, roster, out _));
            }
            Equal(0, RitualGathering.Number(9.999, 10));
            Equal(0, RitualGathering.Number(15, 10));
            Equal(true, ritual.Advance(15, roster, out _));
            Equal(false, ritual.Advance(16, roster, out _));
        });
        Test("Concurrent triggers preserve the single attempt and its frozen roster", () =>
        {
            var ritual = new RitualGathering(); var roster = GatheringRoster();
            ritual.Start(0, roster, out _); var id = ritual.Id;
            Equal(false, ritual.Start(1, GatheringRoster(), out _));
            Equal(id, ritual.Id); Equal(0d, ritual.StartedAt);
            roster[0].Life = "replaced";
            Equal("host-life", ritual.Members[0].Life);
        });
        foreach (var change in new[] { "leave", "death", "join", "disconnect", "character", "unavailable" })
        {
            Test("Countdown cancels on " + change + " including at commitment", () =>
            {
                var ritual = new RitualGathering(); ritual.Start(0, GatheringRoster(), out _);
                var roster = GatheringRoster();
                if (change == "leave") roster[1].Unavailable = "outside the circle";
                if (change == "death") roster[0].Unavailable = "dead";
                if (change == "join") roster = roster.Append(new RitualMember { Peer = 3, Life = "new", Name = "New" }).ToArray();
                if (change == "disconnect") roster = roster.Take(1).ToArray();
                if (change == "character") roster[1].Life = "new-life";
                if (change == "unavailable") roster[1].Unavailable = "confirmation unavailable";
                Equal(false, ritual.Advance(5, roster, out var error));
                Equal("Ritual Cancelled", error); Equal(false, ritual.Active);
                Equal(false, ritual.Advance(6, GatheringRoster(), out _));
            });
        }
        Test("Cancelled attempts can restart with a new correlation ID", () =>
        {
            var ritual = new RitualGathering(); ritual.Start(0, GatheringRoster(), out _); var id = ritual.Id;
            ritual.Advance(1, GatheringRoster().Take(1).ToArray(), out _); ritual.Reset();
            Equal(true, ritual.Start(2, GatheringRoster(), out _)); Equal(false, id == ritual.Id);
        });
        Test("Clients with different clocks follow the same server countdown under symmetric latency", () =>
        {
            foreach (var localBase in new[] { 100d, 10000d })
            {
                var view = new RitualCountdown();
                view.Apply(localBase + .2, "server", 1, "", 1000.1, localBase, false, 0);
                view.Apply(localBase + 1.05, "server", 2, "attempt", 1001.05, 0, true, 1001);
                for (var second = 0; second < 5; second++) Equal(5 - second, view.Number(localBase + 1.05 + second));
                Equal(0, view.Number(localBase + 6.05));
            }
        });
        Test("Delayed duplicate starts cannot restore a cancelled countdown", () =>
        {
            var view = new RitualCountdown();
            view.Apply(100, "session", 2, "attempt", 100, 0, true, 100);
            Equal(5, view.Number(100));
            view.Apply(101, "session", 4, "attempt", 101, 0, false, 100);
            Equal(false, view.Apply(102, "session", 3, "attempt", 100, 0, true, 100));
            Equal(false, view.Apply(102, "session", 4, "attempt", 100, 0, true, 100));
            Equal(0, view.Number(102));
        });
        Test("Countdown revisions and session identity reject old-session packets", () =>
        {
            var view = new RitualCountdown();
            view.Apply(100, "current", 1, "attempt", 100, 0, true, 100);
            Equal(false, view.Apply(101, "old", 99, "other", 100, 0, true, 100));
            Equal(4, view.Number(101));
            view.Clear(); Equal(0, view.Number(102));
        });
        Test("Clock jitter cannot count backwards or revive an expired display", () =>
        {
            var view = new RitualCountdown();
            view.Apply(100, "session", 1, "attempt", 100, 0, true, 100);
            Equal(3, view.Number(102.1));
            view.Apply(102.2, "session", 2, "attempt", 101.5, 102, true, 100);
            Equal(3, view.Number(102.2));
            Equal(0, view.Number(106));
            view.Apply(106.1, "session", 3, "attempt", 104.1, 106, true, 100);
            Equal(0, view.Number(106.1));
            view.Apply(107, "session", 4, "new", 107, 107, true, 107);
            Equal(5, view.Number(107));
        });
    }
}
