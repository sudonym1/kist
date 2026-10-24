using System;
using System.Collections.Generic;
using System.Linq;

namespace SharedInventory;

// Transport-independent server state. The caller supplies authoritative peer/life
// identities and current availability; map visibility never participates.
internal sealed class RitualMember
{
    public long Peer;
    public string Life = "", Name = "", Unavailable = "";
    public RitualMember Copy() => new RitualMember { Peer = Peer, Life = Life, Name = Name, Unavailable = Unavailable };
}

internal sealed class RitualGathering
{
    public const double Duration = 5;
    public const string Cancelled = "Ritual Cancelled";
    public const string MissingPrefix = "All connected players must be at the original stone circle. Missing: ";
    public enum Stage { Idle, Countdown, Committed }
    public Stage State { get; private set; }
    public string Id { get; private set; } = "";
    public double StartedAt { get; private set; }
    public IReadOnlyList<RitualMember> Members { get; private set; } = Array.Empty<RitualMember>();
    public bool Active => State != Stage.Idle;

    public static string Missing(IReadOnlyList<RitualMember> roster)
    {
        var missing = roster.Where(p => p.Unavailable.Length != 0 || p.Life.Length == 0)
            .Select(p => p.Name + " (" + (p.Unavailable.Length != 0 ? p.Unavailable : "loading") + ")").ToArray();
        return missing.Length == 0 ? "" : MissingPrefix + string.Join(", ", missing) + ".";
    }

    public bool Start(double now, IReadOnlyList<RitualMember> roster, out string error, string? id = null)
    {
        error = Active ? "A ritual is already in progress." : Missing(roster);
        if (roster.Count == 0 && error.Length == 0) error = "No connected players are available.";
        if (error.Length != 0) return false;
        Members = roster.OrderBy(p => p.Peer).Select(p => p.Copy()).ToArray();
        Id = id ?? Guid.NewGuid().ToString("N");
        StartedAt = now;
        State = Stage.Countdown;
        return true;
    }

    // Exactly one transition reaches Committed, including at the five-second
    // boundary. Consumers in phase 5 own completion after that handoff.
    public bool Advance(double now, IReadOnlyList<RitualMember> roster, out string cancellation)
    {
        cancellation = "";
        if (State != Stage.Countdown) return false;
        if (roster.Count != Members.Count || Members.Any(p => !roster.Any(q => q.Peer == p.Peer && q.Life == p.Life)))
            cancellation = Cancelled;
        else
        {
            var missing = Missing(roster);
            if (missing.Length != 0) cancellation = Cancelled;
        }
        if (cancellation.Length != 0) { State = Stage.Idle; return false; }
        if (now < StartedAt + Duration) return false;
        State = Stage.Committed;
        return true;
    }

    public void Reset()
    {
        State = Stage.Idle;
        Members = Array.Empty<RitualMember>();
    }

    public static int Number(double now, double start)
    {
        var elapsed = now - start;
        return elapsed < 0 || elapsed >= Duration ? 0 : 5 - (int)Math.Floor(elapsed);
    }
}

// Client view of the server's monotonic schedule. Revisions reject delayed
// starts after cancellation; probe echoes estimate clock offset without sharing
// a machine clock or any hidden gameplay deadline.
internal sealed class RitualCountdown
{
    private string session = "", attempt = "";
    private long revision;
    private double offset = double.NaN, start = double.NaN;
    private int lastNumber = 5;
    public bool Apply(double now, string token, long serial, string id, double serverTime,
        double echo, bool countdown, double serverStart)
    {
        if (session.Length != 0 && session != token || serial <= revision) return false;
        session = token; revision = serial;
        if (echo > 0) offset = serverTime - (now + echo) / 2;
        else if (double.IsNaN(offset)) offset = serverTime - now;
        if (countdown && id.Length != 0)
        {
            if (id != attempt) lastNumber = 5;
            attempt = id; start = serverStart - offset;
        }
        else if (id.Length != 0 && attempt == id) { start = double.NaN; attempt = ""; }
        return true;
    }
    public int Number(double now)
    {
        if (double.IsNaN(start)) return 0;
        if (now < start) return 0;
        if (lastNumber == 0) return 0;
        var number = RitualGathering.Number(now, start);
        lastNumber = Math.Min(lastNumber, number);
        return lastNumber;
    }
    public void Clear() { start = double.NaN; attempt = ""; }
}
