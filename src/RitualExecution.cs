using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

internal sealed partial class Ritual
{
    private RitualStorage? storage;
    private RitualOutcomes? outcomes;
    private readonly RitualDeathGuard deathGuard = new RitualDeathGuard();
    private string clientSession = "", clientAttempt = "";
    private double clientProbedAt;
    private readonly HashSet<string> strikes = new HashSet<string>();
    private readonly Queue<string> strikeOrder = new Queue<string>();
    private readonly List<DeathReport> deathReports = new List<DeathReport>();
    private static readonly System.Reflection.FieldInfo lastHit = AccessTools.Field(typeof(Character), "m_lastHit");
    internal RitualOutcomes? Outcomes => outcomes;

    private void Trace(string attempt, string phase, string details) =>
        log.LogInfo("Einherjar ritual trace: world=" + world + " localPeer=" + ZNet.GetUID() +
            " serverPeer=" + ServerId + " localSession=" + session + " attempt=" + attempt + " phase=" + phase + " " + details);

    private sealed class DeathReport
    {
        public string Id = "", Life = "", OwnerLife = "";
        public long Peer;
        public Vector3 Position;
    }

    private void InitializeOutcomes(Func<double> minutes)
    {
        router.Register<ZPackage>(Prefix + "Death", ReceiveDeath);
        router.Register<ZPackage>(Prefix + "DeathComplete", ReceiveDeathComplete);
        router.Register<ZPackage>(Prefix + "Strike", ReceiveStrike);
        if (!server) return;
        var random = new System.Random();
        long draws = 0;
        Trace("", "rng-initialized", "generator=System.Random build=" + typeof(Ritual).Assembly.ManifestModule.ModuleVersionId);
        storage = new RitualStorage(Path.Combine(Paths.ConfigPath, "SharedInventory", "ritual-" + world + ".json"), world, (state, error) =>
        {
            if (stopped) return;
            if (error != null) { log.LogError("Ritual state could not be loaded: " + error); return; }
            outcomes = new RitualOutcomes(state!, () => Now, () => DateTime.UtcNow, minutes, count =>
                {
                    var index = random.Next(count);
                    Trace(AttemptId, "rng-draw", "drawNumber=" + (++draws) + " exclusiveUpperBound=" + count + " index=" + index);
                    return index;
                },
                (snapshot, complete) => storage!.Save(snapshot, complete), DispatchDeaths,
                victim =>
                {
                    SpawnEssence(new Vector3(victim.X, victim.Y, victim.Z));
                    Broadcast(RitualOutcomes.SacrificeMessage(victim.Name), false);
                }, message => Trace(AttemptId, "outcome", message));
        });
    }

    private void DispatchDeaths(RitualRecord record)
    {
        Broadcast(record.Punishment ? RitualOutcomes.PunishmentMessage : record.Victims.Single().Name + " has been chosen by Oden.", false);
        // No retry or substitute victim. Every committed punishment target is
        // handled independently; missing owners expire without blocking others.
        foreach (var victim in record.Victims)
        {
            var character = Character(victim.Peer);
            var skip = character == null ? "missing-character" : character.m_uid.ToString() != victim.Life ? "different-life" :
                character.GetBool(ZDOVars.s_dead) ? "already-dead" : "";
            confirmations.TryGetValue(victim.Peer, out var confirmation);
            if (skip.Length == 0 && (confirmation == null || confirmation.OwnerLife != victim.OwnerLife)) skip = "missing-life-confirmation";
            if (skip.Length != 0)
            { Trace(record.Id, "death-dispatch-skipped", "victimPeer=" + victim.Peer + " reason=" + skip); continue; }
            var p = Package(); p.Write(session); p.Write(record.Id); p.Write(victim.Life); p.Write(victim.OwnerLife);
            p.Write(confirmation!.ClientTime + RitualOutcomes.DeathTimeout);
            p.Write(record.Punishment); // Same validated death operation; no new endpoint or timer data.
            Trace(record.Id, "death-dispatch", "targetPeer=" + victim.Peer + " life=" + victim.Life);
            try { Send(victim.Peer, "Death", p); }
            catch (Exception error) { log.LogError("Ritual death dispatch failed for " + record.Id + "/" + victim.Life + ": " + error); }
        }
    }

    private void ReceiveDeath(long sender, ZPackage p)
    {
        if (!FromServer(sender) || !Accept(p)) return;
        var token = p.ReadString(); var id = p.ReadString(); var life = p.ReadString(); var ownerLife = p.ReadString(); var expires = p.ReadDouble();
        var punishment = p.GetPos() < p.Size() && p.ReadBool(); // Older packets contain no guidance flag.
        var player = Player.m_localPlayer;
        if (player == null || player.IsDead() || player.InIntro() || player.IsTeleporting() ||
            player.GetComponent<ZNetView>()?.IsOwner() != true || player.GetZDOID().ToString() != life ||
            !deathGuard.Claim(life, ownerLife, token, id, Now, expires)) return;
        Trace(id, "death-accepted", "serverSession=" + token + " victimPeer=" + ZNet.GetUID() + " life=" + life);
        var position = player.transform.position;
        // Player.OnDeath reads this unconditionally for death statistics. Fresh
        // characters have no previous hit; avoid stale enemy/player attribution.
        lastHit.SetValue(player, new HitData { m_hitType = HitData.HitType.Undefined, m_point = position });
        PlayStrike(id, life, ownerLife, position);
        // OnDeath itself bypasses damage mitigation/god mode and runs ordinary
        // tombstone, death effects, skill settings and respawn exactly once.
        // No ordinary-death observer can publish an essence.
        player.OnDeath();
        Trace(id, "death-result", "victimPeer=" + ZNet.GetUID() + " life=" + life + " dead=" + player.IsDead());
        if (!player.IsDead()) return;
        Plugin.Instance?.Tutorials?.RitualDeath(player, punishment);
        var reply = Package(); reply.Write(token); reply.Write(id); reply.Write(life); reply.Write(ownerLife); reply.Write(position);
        Send(sender, "DeathComplete", reply);
    }

    private void ReceiveDeathComplete(long sender, ZPackage p)
    {
        if (!server || !Accept(p) || p.ReadString() != session || gathering.State != RitualGathering.Stage.Committed) return;
        var report = new DeathReport { Peer = sender, Id = p.ReadString(), Life = p.ReadString(), OwnerLife = p.ReadString(), Position = p.ReadVector3() };
        if (report.Id != gathering.Id || !gathering.Members.Any(m => m.Peer == sender && m.Life == report.Life && m.OwnerLife == report.OwnerLife) ||
            !Finite(report.Position) || deathReports.Any(r => r.Peer == sender && r.Id == report.Id)) return;
        deathReports.Add(report);
        Trace(report.Id, "death-report-received", "victimPeer=" + sender + " life=" + report.Life);
    }

    private void ConfirmDeaths()
    {
        // A routed completion can precede ZDO replication. Retain it only for
        // this committed attempt; wait for server-known death of that same life.
        foreach (var report in deathReports.ToArray())
        {
            var character = Character(report.Peer);
            if (report.Id != gathering.Id || character == null || character.m_uid.ToString() != report.Life)
            { deathReports.Remove(report); continue; }
            if (!character.GetBool(ZDOVars.s_dead) || (character.GetPosition() - report.Position).sqrMagnitude > 100) continue;
            if (outcomes?.Confirm(report.Id, report.Peer, report.Life, report.OwnerLife, report.Position.x, report.Position.y, report.Position.z) == true)
            {
                foreach (var member in gathering.Members.Where(m => m.Peer != report.Peer))
                {
                    var p = Package(); p.Write(session); p.Write(report.Id); p.Write(report.Life); p.Write(report.OwnerLife); p.Write(report.Position);
                    Send(member.Peer, "Strike", p);
                }
                deathReports.Remove(report);
            }
        }
    }

    private void ReceiveStrike(long sender, ZPackage p)
    {
        if (!FromServer(sender) || !Accept(p)) return;
        var token = p.ReadString(); var id = p.ReadString(); var life = p.ReadString(); var ownerLife = p.ReadString(); var position = p.ReadVector3();
        if (token != clientSession || id != clientAttempt || Now - clientProbedAt > RitualOutcomes.DeathTimeout * 2 || !Finite(position)) return;
        PlayStrike(id, life, ownerLife, position);
    }
    private void PlayStrike(string id, string life, string ownerLife, Vector3 position)
    {
        var key = id + "/" + life + "/" + ownerLife;
        if (!strikes.Add(key)) return;
        strikeOrder.Enqueue(key);
        while (strikeOrder.Count > 128) strikes.Remove(strikeOrder.Dequeue());
        RitualLightning.Play(position);
    }
    private static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z) &&
        !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z);

    internal static void SpawnEssence(Vector3 position)
    {
        var prefab = ZNetScene.instance.GetPrefab(ContentIds.Essence);
        if (prefab == null) throw new InvalidOperationException("Einherjar Essence prefab is unavailable.");
        ItemDrop.DropItem(prefab.GetComponent<ItemDrop>().m_itemData, 1, position + Vector3.up * .5f, Quaternion.identity);
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    private static class InvalidateDeadLife
    {
        private static void Postfix(Player __instance)
        {
            if (__instance == Player.m_localPlayer) Plugin.Instance?.Ritual?.deathGuard.Invalidate();
        }
    }
}
