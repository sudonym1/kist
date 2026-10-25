using System;
using System.Collections.Generic;
using System.Linq;

namespace SharedInventory;

// Unity-free outcome transaction. All callbacks run on the main thread; writes
// receive detached snapshots. A reserved reward is never replayed after restart.
internal sealed class RitualState
{
    public int Version = 1;
    public long World;
    public long DeadlineUtcTicks;
    public List<RitualRecord> Attempts = new List<RitualRecord>();
    public RitualState Copy() => new RitualState { Version = Version, World = World, DeadlineUtcTicks = DeadlineUtcTicks,
        Attempts = Attempts.Select(a => a.Copy()).ToList() };
}

internal sealed class RitualRecord
{
    public string Id = "", Status = "committed", Reward = "none";
    public bool Punishment;
    public long CommittedUtcTicks;
    public List<RitualVictim> Victims = new List<RitualVictim>();
    public RitualRecord Copy() => new RitualRecord { Id = Id, Status = Status, Reward = Reward, Punishment = Punishment,
        CommittedUtcTicks = CommittedUtcTicks, Victims = Victims.Select(v => v.Copy()).ToList() };
}

internal sealed class RitualVictim
{
    public long Peer;
    public string Life = "", OwnerLife = "", Name = "";
    public bool Confirmed;
    public float X, Y, Z;
    public RitualVictim Copy() => (RitualVictim)MemberwiseClone();
}

internal sealed class RitualOutcomes
{
    public const string PunishmentMessage = "Odin is enraged by your avarice";
    public const double DeathTimeout = 15;
    private readonly Func<double> now;
    private readonly Func<DateTime> utc;
    private readonly Func<double> minutes;
    private readonly Func<int, int> draw;
    private readonly Action<RitualState, Action<Exception?>> save;
    private readonly Action<RitualRecord> dispatch;
    private readonly Action<RitualVictim> reward;
    private readonly Action<string> diagnostic;
    private RitualRecord? current;
    private double deadline, expires;
    private bool writing, stopped;
    public RitualState State { get; }
    public string Error { get; private set; } = "";
    public bool Active => current != null;
    public bool Ready => !stopped && Error.Length == 0 && !Active && !writing;
    internal double Deadline => deadline;

    public RitualOutcomes(RitualState state, Func<double> now, Func<DateTime> utc, Func<double> minutes,
        Func<int, int> draw, Action<RitualState, Action<Exception?>> save,
        Action<RitualRecord> dispatch, Action<RitualVictim> reward, Action<string> diagnostic)
    {
        State = state; this.now = now; this.utc = utc; this.minutes = minutes; this.draw = draw;
        this.save = save; this.dispatch = dispatch; this.reward = reward; this.diagnostic = diagnostic;
        deadline = now() + Math.Max(0, (state.DeadlineUtcTicks - utc().Ticks) / (double)TimeSpan.TicksPerSecond);
        foreach (var attempt in state.Attempts.Where(a => a.Status != "finished" && a.Status != "incomplete"))
            diagnostic("Uncertain ritual retained without replay: " + attempt.Id + " / " + attempt.Status + " / " + attempt.Reward);
    }

    public bool ValidInterval()
    {
        var value = minutes();
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return false;
        try { return utc().AddMinutes(value) > utc(); }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private void StartTimer()
    {
        var seconds = minutes() * 60;
        State.DeadlineUtcTicks = utc().AddSeconds(seconds).Ticks;
        deadline = now() + seconds;
    }

    public bool Commit(string id, IReadOnlyList<RitualMember> roster)
    {
        if (!Ready || !ValidInterval() || string.IsNullOrEmpty(id) || roster.Count == 0 || State.Attempts.Any(a => a.Id == id) ||
            roster.Any(m => m.Peer == 0 || string.IsNullOrEmpty(m.Life) || string.IsNullOrEmpty(m.OwnerLife) || m.Unavailable.Length != 0) ||
            roster.Select(m => m.Peer).Distinct().Count() != roster.Count) return false;
        var punishment = now() < deadline;
        var victims = punishment ? roster : new[] { roster[draw(roster.Count)] };
        current = new RitualRecord { Id = id, Punishment = punishment, CommittedUtcTicks = utc().Ticks,
            Victims = victims.Select(m => new RitualVictim { Peer = m.Peer, Life = m.Life, OwnerLife = m.OwnerLife, Name = m.Name }).ToList() };
        State.Attempts.Add(current);
        if (punishment) StartTimer();
        Persist(() =>
        {
            expires = now() + DeathTimeout;
            // Set waiting before dispatch: a local host can acknowledge synchronously.
            current!.Status = "waiting-deaths";
            dispatch(current.Copy());
        });
        return true;
    }

    public bool Confirm(string id, long peer, string life, string ownerLife, float x, float y, float z)
    {
        if (stopped || writing || current == null || current.Id != id || current.Status != "waiting-deaths" || now() > expires) return false;
        var victim = current.Victims.SingleOrDefault(v => v.Peer == peer && v.Life == life && v.OwnerLife == ownerLife);
        if (victim == null || victim.Confirmed) return false;
        victim.Confirmed = true; victim.X = x; victim.Y = y; victim.Z = z;
        return true;
    }

    public void Update()
    {
        if (stopped || writing || current == null || Error.Length != 0 || current.Status != "waiting-deaths") return;
        if (current.Victims.All(v => v.Confirmed))
        {
            if (current.Punishment) Finish(true);
            else
            {
                if (!ValidInterval()) { Fail(new InvalidOperationException("Invalid ritual interval after sacrifice; manual recovery required.")); return; }
                StartTimer();
                current.Status = "reward-reserved"; current.Reward = "reserved";
                // Persist the timer and publication reservation before creating an item.
                Persist(() =>
                {
                    try { reward(current!.Victims.Single().Copy()); current.Reward = "published"; Finish(true); }
                    catch (Exception error) { Fail(error); }
                });
            }
        }
        else if (now() >= expires) Finish(false);
    }

    private void Finish(bool complete)
    {
        current!.Status = complete ? "finished" : "incomplete";
        var label = current.Id + " / " + current.Status + " / " + current.Reward;
        Persist(() => { diagnostic(label); current = null; });
    }

    public bool Reset(Action<Exception?> completed)
    {
        if (!Ready) return false;
        State.DeadlineUtcTicks = 0; deadline = now();
        Persist(() => completed(null), completed);
        return true;
    }

    private void Persist(Action completed, Action<Exception?>? failed = null)
    {
        writing = true;
        try
        {
            save(State.Copy(), error =>
            {
                if (stopped) return;
                writing = false;
                if (error != null) { Fail(error); failed?.Invoke(error); return; }
                try { completed(); } catch (Exception failure) { Fail(failure); }
            });
        }
        catch (Exception error) { writing = false; Fail(error); failed?.Invoke(error); }
    }
    private void Fail(Exception error) { Error = error.Message; current = null; diagnostic("Ritual state unavailable: " + error); }
    public void Stop() { stopped = true; }
}
