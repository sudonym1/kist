using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharedInventory;

internal static partial class Program
{
    private sealed class OutcomeLab
    {
        public double Now = 100, Minutes = 30;
        public DateTime Utc = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        public int Choice, Draws, Rewards;
        public bool ThrowReward;
        public RitualState Saved = new RitualState { World = 7 };
        public readonly Queue<(RitualState State, Action<Exception?> Complete)> Writes = new();
        public readonly List<RitualRecord> Dispatches = new();
        public readonly List<string> Diagnostics = new();
        public RitualOutcomes Engine;
        public OutcomeLab(RitualState? state = null)
        {
            Engine = new RitualOutcomes(state ?? Saved.Copy(), () => Now, () => Utc, () => Minutes,
                count => { Draws++; if (Choice >= count) throw new Exception("Draw outside roster"); return Choice; },
                (snapshot, done) => Writes.Enqueue((snapshot, done)), Dispatches.Add,
                _ => { if (ThrowReward) throw new IOException("uncertain spawn"); Rewards++; }, Diagnostics.Add);
        }
        public void Flush(Exception? error = null)
        {
            var write = Writes.Dequeue();
            if (error == null) Saved = write.State;
            write.Complete(error);
        }
        public static RitualMember[] Roster() => GatheringRoster().Select(m => { m.OwnerLife = m.Life + "-nonce"; return m; }).ToArray();
        public void ConfirmAll()
        {
            foreach (var v in Dispatches.Last().Victims) Equal(true, Engine.Confirm(Dispatches.Last().Id, v.Peer, v.Life, v.OwnerLife, 1, 2, 3));
            Engine.Update();
        }
        public void Success(string id = "success")
        {
            Equal(true, Engine.Commit(id, Roster())); Flush(); ConfirmAll(); Flush(); Flush();
        }
    }

    private static void RitualOutcomeTests()
    {
        Test("Sacrifice journals commitment, confirms death, persists deadline and reserves one reward before publication", () =>
        {
            var lab = new OutcomeLab { Choice = 1 };
            Equal(true, lab.Engine.Commit("one", OutcomeLab.Roster()));
            Equal(0, lab.Dispatches.Count); Equal(0, lab.Rewards); Equal(0L, lab.Saved.DeadlineUtcTicks);
            lab.Flush(); Equal(1, lab.Dispatches.Count); Equal(2L, lab.Dispatches[0].Victims.Single().Peer);
            lab.ConfirmAll(); Equal(0, lab.Rewards); Equal(0L, lab.Saved.DeadlineUtcTicks);
            lab.Flush(); Equal(1, lab.Rewards); Equal("reserved", lab.Saved.Attempts.Single().Reward);
            Equal(lab.Utc.AddMinutes(30).Ticks, lab.Saved.DeadlineUtcTicks);
            lab.Flush(); Equal("published", lab.Saved.Attempts.Single().Reward); Equal(false, lab.Engine.Active);
        });
        Test("Every roster slot including the initiator is eligible on the same draw", () =>
        {
            foreach (var slot in new[] { 0, 1 })
            {
                var lab = new OutcomeLab { Choice = slot }; lab.Success();
                Equal(slot + 1L, lab.Dispatches.Single().Victims.Single().Peer); Equal(1, lab.Draws);
            }
        });
        Test("Selection diagnostics record the actual ordered-roster index without consuming extra randomness", () =>
        {
            foreach (var slot in new[] { 0, 1 })
            {
                var lab = new OutcomeLab { Choice = slot };
                var roster = OutcomeLab.Roster(); roster[0].Peer = -393302544; roster[1].Peer = 1439239401;
                Equal(true, lab.Engine.Commit("logged", roster));
                Equal(1, lab.Draws); Equal(0, lab.Dispatches.Count);
                Equal("selection attempt=logged outcome=sacrifice rosterCount=2 selectedIndex=" + slot +
                    " victimPeers=[" + roster[slot].Peer + "] persistence=pending", lab.Diagnostics.Single());
                lab.Flush(); Equal(roster[slot].Peer, lab.Dispatches.Single().Victims.Single().Peer);
                lab.ConfirmAll(); lab.Flush(); lab.Flush();
                Equal(1, lab.Draws); Equal(1, lab.Rewards);
                Equal(true, lab.Diagnostics.Contains("reward-publish attempt=logged victimPeer=" + roster[slot].Peer));
                Equal(false, lab.Engine.Commit("logged", roster)); Equal(1, lab.Draws);
                Equal(1, lab.Diagnostics.Count(m => m.StartsWith("selection attempt=")));
            }
        });
        Test("Punishment logs no random draw and retains every victim", () =>
        {
            var lab = new OutcomeLab(); lab.Success(); lab.Diagnostics.Clear();
            Equal(true, lab.Engine.Commit("logged-anger", OutcomeLab.Roster()));
            Equal("selection attempt=logged-anger outcome=punishment rosterCount=2 selectedIndex=none victimPeers=[1,2] persistence=pending",
                lab.Diagnostics.Single());
            lab.Flush(); lab.ConfirmAll(); lab.Flush();
            Equal(1, lab.Draws); Equal(1, lab.Rewards);
            Equal(2, lab.Diagnostics.Count(m => m.StartsWith("death-confirmed attempt=logged-anger ")));
            Equal(false, lab.Diagnostics.Any(m => m.StartsWith("reward-publish")));
        });
        Test("Solo success has one victim and one reward", () =>
        {
            var lab = new OutcomeLab(); lab.Engine.Commit("solo", OutcomeLab.Roster().Take(1).ToArray()); lab.Flush();
            lab.ConfirmAll(); lab.Flush(); lab.Flush(); Equal(1, lab.Rewards); Equal(1, lab.Dispatches.Single().Victims.Count);
        });
        Test("Concurrent and duplicate commits never redraw or repeat publication", () =>
        {
            var lab = new OutcomeLab(); lab.Engine.Commit("one", OutcomeLab.Roster());
            Equal(false, lab.Engine.Commit("other", OutcomeLab.Roster())); lab.Flush(); lab.ConfirmAll(); lab.Flush(); lab.Flush();
            Equal(false, lab.Engine.Commit("one", OutcomeLab.Roster())); Equal(1, lab.Draws); Equal(1, lab.Rewards);
        });
        Test("Unrelated, wrong-owner, wrong-life, wrong-token and duplicate deaths cannot create essence", () =>
        {
            var lab = new OutcomeLab(); lab.Engine.Commit("one", OutcomeLab.Roster()); lab.Flush();
            var v = lab.Dispatches.Single().Victims.Single();
            Equal(false, lab.Engine.Confirm("other", v.Peer, v.Life, v.OwnerLife, 0, 0, 0));
            Equal(false, lab.Engine.Confirm("one", 99, v.Life, v.OwnerLife, 0, 0, 0));
            Equal(false, lab.Engine.Confirm("one", v.Peer, "respawned", v.OwnerLife, 0, 0, 0));
            Equal(false, lab.Engine.Confirm("one", v.Peer, v.Life, "old-nonce", 0, 0, 0));
            Equal(true, lab.Engine.Confirm("one", v.Peer, v.Life, v.OwnerLife, 0, 0, 0));
            Equal(false, lab.Engine.Confirm("one", v.Peer, v.Life, v.OwnerLife, 0, 0, 0));
            lab.Engine.Update(); lab.Flush(); lab.Flush(); lab.Engine.Update(); Equal(1, lab.Rewards);
        });
        Test("Missing sacrifice confirmation times out without reward, timer or replacement", () =>
        {
            var lab = new OutcomeLab(); lab.Engine.Commit("one", OutcomeLab.Roster()); lab.Flush();
            lab.Now += RitualOutcomes.DeathTimeout; lab.Engine.Update(); lab.Flush();
            Equal(0, lab.Rewards); Equal(0L, lab.Saved.DeadlineUtcTicks); Equal(1, lab.Draws);
            Equal("incomplete", lab.Saved.Attempts.Single().Status);
        });
        Test("Early punishment persists its restarted timer before dispatch, kills the frozen roster and never draws or rewards", () =>
        {
            var lab = new OutcomeLab(); lab.Success(); var previous = lab.Saved.DeadlineUtcTicks;
            lab.Now += 10; lab.Utc = lab.Utc.AddSeconds(10); lab.Engine.Commit("anger", OutcomeLab.Roster());
            Equal(1, lab.Dispatches.Count); lab.Flush();
            Equal(true, lab.Saved.DeadlineUtcTicks > previous); Equal(true, lab.Dispatches.Last().Punishment);
            Equal(2, lab.Dispatches.Last().Victims.Count); lab.ConfirmAll(); lab.Flush();
            Equal(1, lab.Rewards); Equal(1, lab.Draws); Equal("none", lab.Saved.Attempts.Last().Reward);
            Equal("Oden is enraged by your avarice", RitualOutcomes.PunishmentMessage);
        });
        Test("One disconnected punishment victim cannot prevent the other death or change the outcome", () =>
        {
            var lab = new OutcomeLab(); lab.Success(); lab.Engine.Commit("anger", OutcomeLab.Roster()); lab.Flush();
            var v = lab.Dispatches.Last().Victims[0]; lab.Engine.Confirm("anger", v.Peer, v.Life, v.OwnerLife, 0, 0, 0);
            lab.Now += RitualOutcomes.DeathTimeout; lab.Engine.Update(); lab.Flush();
            Equal("incomplete", lab.Saved.Attempts.Last().Status); Equal(1, lab.Saved.Attempts.Last().Victims.Count(v => v.Confirmed));
            Equal(1, lab.Rewards); Equal("none", lab.Saved.Attempts.Last().Reward);
        });
        foreach (var delta in new[] { -.001, 0, .001 })
            Test("Timer commitment boundary at expiry " + delta, () =>
            {
                var lab = new OutcomeLab(); lab.Success(); lab.Now = lab.Engine.Deadline + delta;
                lab.Engine.Commit("boundary", OutcomeLab.Roster()); lab.Flush();
                Equal(delta < 0, lab.Dispatches.Last().Punishment);
            });
        Test("A countdown crossing expiry is decided using commitment time", () =>
        {
            var lab = new OutcomeLab(); lab.Success();
            lab.Now = lab.Engine.Deadline - 2; var gathering = new RitualGathering(); var roster = OutcomeLab.Roster();
            gathering.Start(lab.Now, roster, out _); lab.Now += 5;
            Equal(true, gathering.Advance(lab.Now, roster, out _)); lab.Engine.Commit(gathering.Id, gathering.Members); lab.Flush();
            Equal(false, lab.Dispatches.Last().Punishment);
        });
        Test("Monotonic running intervals ignore manual wall-clock jumps and configuration changes", () =>
        {
            var lab = new OutcomeLab(); lab.Success(); var deadline = lab.Engine.Deadline;
            lab.Utc = lab.Utc.AddDays(3); lab.Minutes = 1; lab.Now = deadline - 1;
            lab.Engine.Commit("early", OutcomeLab.Roster()); lab.Flush();
            Equal(true, lab.Dispatches.Last().Punishment); Equal(lab.Utc.AddMinutes(1).Ticks, lab.Saved.DeadlineUtcTicks);
        });
        Test("Restart counts offline UTC time and never replays uncertain death or reserved reward", () =>
        {
            var lab = new OutcomeLab(); lab.Success(); var state = lab.Saved.Copy();
            state.Attempts[0].Status = "reward-reserved"; state.Attempts[0].Reward = "reserved";
            var now = 10d; var utc = lab.Utc.AddMinutes(31); var writes = 0; var dispatch = 0; var rewards = 0;
            var engine = new RitualOutcomes(state, () => now, () => utc, () => 30, _ => 0,
                (_, _) => writes++, _ => dispatch++, _ => rewards++, _ => { });
            engine.Update(); Equal(0, writes); Equal(0, dispatch); Equal(0, rewards);
            Equal(true, engine.Commit("new", OutcomeLab.Roster())); Equal(1, writes);
            Equal(false, state.Attempts.Last().Punishment); Equal(2, state.Attempts.Count);
        });
        Test("Restart before expiry restores the remaining interval", () =>
        {
            var state = new RitualState { World = 7, DeadlineUtcTicks = new DateTime(2026, 10, 24, 12, 10, 0, DateTimeKind.Utc).Ticks };
            var lab = new OutcomeLab(state); Equal(lab.Now + 600, lab.Engine.Deadline);
            lab.Engine.Commit("early", OutcomeLab.Roster()); lab.Flush(); Equal(true, lab.Dispatches.Single().Punishment);
        });
        Test("Failed commit persistence prevents every death and blocks subsequent initiation", () =>
        {
            var lab = new OutcomeLab(); lab.Engine.Commit("one", OutcomeLab.Roster()); lab.Flush(new IOException("disk full"));
            Equal(0, lab.Dispatches.Count); Equal(false, lab.Engine.Ready); Equal(false, lab.Engine.Commit("two", OutcomeLab.Roster()));
        });
        Test("Failed reward reservation persistence creates no pickup and no retry", () =>
        {
            var lab = new OutcomeLab(); lab.Engine.Commit("one", OutcomeLab.Roster()); lab.Flush(); lab.ConfirmAll();
            lab.Flush(new IOException("disk full")); lab.Engine.Update(); Equal(0, lab.Rewards); Equal(false, lab.Engine.Ready);
        });
        Test("An uncertain spawn remains durably reserved and is never automatically retried", () =>
        {
            var lab = new OutcomeLab { ThrowReward = true }; lab.Engine.Commit("one", OutcomeLab.Roster()); lab.Flush(); lab.ConfirmAll(); lab.Flush();
            Equal("reserved", lab.Saved.Attempts.Single().Reward); Equal(false, lab.Engine.Ready); lab.Engine.Update(); Equal(0, lab.Writes.Count);
        });
        Test("Reset persists server deadline and is rejected while an outcome is active", () =>
        {
            var lab = new OutcomeLab(); lab.Success(); var completed = false;
            Equal(true, lab.Engine.Reset(error => completed = error == null)); Equal(false, completed); lab.Flush();
            Equal(true, completed); Equal(0L, lab.Saved.DeadlineUtcTicks);
            lab.Engine.Commit("another", OutcomeLab.Roster()); Equal(false, lab.Engine.Reset(_ => throw new Exception()));
        });
        foreach (var invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.MaxValue })
            Test("Invalid ritual interval fails safely: " + invalid, () =>
            {
                var lab = new OutcomeLab { Minutes = invalid }; Equal(false, lab.Engine.ValidInterval());
                Equal(false, lab.Engine.Commit("one", OutcomeLab.Roster())); Equal(0, lab.Writes.Count);
            });
        Test("Stopped sessions cannot dispatch deaths or rewards from a pending write", () =>
        {
            var lab = new OutcomeLab(); lab.Engine.Commit("one", OutcomeLab.Roster()); lab.Engine.Stop(); lab.Flush();
            Equal(0, lab.Dispatches.Count); Equal(false, lab.Engine.Ready);
        });
        Test("Owner death guard rejects duplicate commands, expired packets, other sessions and respawned lives", () =>
        {
            var guard = new RitualDeathGuard(); var nonce = guard.Probe("character", "server", "attempt", 100);
            Equal(false, guard.Claim("character", nonce, "old", "attempt", 101, 110));
            Equal(false, guard.Claim("character", nonce, "server", "other", 101, 110));
            Equal(false, guard.Claim("character", nonce, "server", "attempt", 111, 110));
            Equal(false, guard.Claim("character", nonce, "server", "attempt", 101, double.NaN));
            Equal(false, guard.Claim("character", nonce, "server", "attempt", 101, double.PositiveInfinity));
            Equal(true, guard.Claim("character", nonce, "server", "attempt", 101, 110));
            Equal(false, guard.Claim("character", nonce, "server", "attempt", 102, 110));
            var respawned = guard.Probe("character", "server", "new", 103);
            Equal(false, nonce == respawned); Equal(false, guard.Claim("character", nonce, "server", "attempt", 104, 110));
            guard.Invalidate(); Equal(false, guard.Claim("character", respawned, "server", "new", 104, 110));
        });
        Test("Outcome commitment rejects missing life confirmations and duplicate peers without writing", () =>
        {
            var lab = new OutcomeLab(); var roster = OutcomeLab.Roster(); roster[1].OwnerLife = "";
            Equal(false, lab.Engine.Commit("missing", roster));
            roster = OutcomeLab.Roster(); roster[1].Peer = roster[0].Peer;
            Equal(false, lab.Engine.Commit("duplicate", roster)); Equal(0, lab.Writes.Count); Equal(0, lab.Draws);
        });
        Test("Ritual files are world-bound, atomically replaced and fail closed on corrupt or interrupted state", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "einherjar-state-" + Guid.NewGuid()); Directory.CreateDirectory(root);
            var path = Path.Combine(root, "ritual-7.json");
            try
            {
                var state = RitualStateFile.Load(path, 7); Equal(0L, state.DeadlineUtcTicks);
                state.DeadlineUtcTicks = DateTime.UtcNow.Ticks; RitualStateFile.Save(path, state);
                Equal(state.DeadlineUtcTicks, RitualStateFile.Load(path, 7).DeadlineUtcTicks); Equal(true, File.Exists(path + ".bak"));
                var rejected = false; try { RitualStateFile.Load(path, 8); } catch (InvalidDataException) { rejected = true; } Equal(true, rejected);
                File.WriteAllText(path, "{}"); rejected = false;
                try { RitualStateFile.Load(path, 7); } catch (InvalidDataException) { rejected = true; } Equal(true, rejected);
                var incomplete = Path.Combine(root, "interrupted.json"); File.WriteAllText(incomplete + ".tmp", "{}"); rejected = false;
                try { RitualStateFile.Load(incomplete, 7); } catch (InvalidDataException) { rejected = true; } Equal(true, rejected);
                Equal(0L, RitualStateFile.Load(Path.Combine(root, "ritual-8.json"), 8).DeadlineUtcTicks);
            }
            finally { Directory.Delete(root, true); }
        });
    }
}
