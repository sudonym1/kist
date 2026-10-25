using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SharedInventory;

internal enum RitualTestAction { ResetCooldown, SpawnEssence, PreviewLightning }

// Created only by TestPanel after its runtime isolated-launch checks pass.
// Operator scope is deliberately the local graphical host; no remote privilege
// claim is accepted. Test endpoints and UI are absent from production.
internal sealed class RitualTestControls
{
    private const string Prefix = "SharedInventory.Ritual.Test.v1.";
    private readonly ZRoutedRpc router;
    private readonly Ritual ritual;
    private readonly bool server;
    private readonly long world;
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly Dictionary<long, string> replies = new Dictionary<long, string>();
    private long sequence, lastReceived, pending;
    private double pendingAt;
    private bool stopped;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    public bool Pending => pending != 0;
    public string Status { get; private set; } = "";
    internal long LastOperation => sequence;

    public RitualTestControls(ZRoutedRpc router, Ritual ritual)
    {
        this.router = router; this.ritual = ritual; server = ZNet.instance.IsServer(); world = ZNet.instance.GetWorldUID();
        router.Register<ZPackage>(Prefix + "Request", Receive);
        router.Register<ZPackage>(Prefix + "Reply", ReceiveReply);
    }
    public bool Operator => server && Player.m_localPlayer != null;
    private ZPackage Package() { var p = new ZPackage(); p.Write(world); p.Write(session); return p; }
    private bool Accept(ZPackage p) => !stopped && p.ReadLong() == world && p.ReadString() == session;
    public void Press(RitualTestAction action)
    {
        if (stopped || Pending) return;
        if (!Operator) { Status = "Ritual test controls require the isolated hosting player."; return; }
        pending = ++sequence; pendingAt = Now; Status = "Test operation pending...";
        var p = Package(); p.Write(pending); p.Write((int)action); p.Write(Now + 10);
        router.InvokeRoutedRPC(ZNet.GetUID(), Prefix + "Request", p);
    }
    private void Reply(long peer, long id, string status)
    {
        if (stopped) return;
        replies[id] = status;
        // At most one local operation is pending. Lower IDs are never executed
        // again even after their cached reply is evicted.
        if (replies.Count > 64) replies.Remove(id - 64);
        var p = Package(); p.Write(id); p.Write(status);
        router.InvokeRoutedRPC(peer, Prefix + "Reply", p);
    }
    private void Receive(long sender, ZPackage p)
    {
        if (!server || sender != ZNet.GetUID() || !Operator || !Accept(p)) return;
        var id = p.ReadLong(); var action = (RitualTestAction)p.ReadInt(); var expires = p.ReadDouble();
        if (replies.TryGetValue(id, out var prior)) { Reply(sender, id, prior); return; }
        if (id <= lastReceived || id <= 0) return;
        lastReceived = id; // Reserve before any asynchronous write or spawn.
        if (double.IsNaN(expires) || expires < Now || expires > Now + 11) { Reply(sender, id, "Test operation expired; no action taken."); return; }
        if (action == RitualTestAction.ResetCooldown)
        {
            if (ritual.Active) { Reply(sender, id, "Reset unavailable while a ritual is active."); return; }
            if (ritual.Outcomes?.Reset(error => Reply(sender, id, error == null ? "Cooldown reset and persisted." : "Reset failed: " + error.Message)) != true)
                Reply(sender, id, "Ritual state is unavailable; reset was not performed.");
            return;
        }
        if (action != RitualTestAction.SpawnEssence && action != RitualTestAction.PreviewLightning)
        { Reply(sender, id, "Unknown ritual test operation."); return; }
        var player = Player.m_localPlayer;
        if (player == null || player.IsDead() || player.InIntro() || player.IsTeleporting() || player.GetComponent<ZNetView>()?.IsOwner() != true)
        { Reply(sender, id, (action == RitualTestAction.SpawnEssence ? "Spawn" : "Lightning") + " unavailable: a living, fully spawned test player is required."); return; }
        try
        {
            if (action == RitualTestAction.PreviewLightning)
            {
                RitualLightning.Play(player.transform.position);
                Reply(sender, id, "Played Obliterator lightning and sound without damage.");
            }
            else
            { Ritual.SpawnEssence(player.transform.position + player.transform.forward * 1.5f); Reply(sender, id, "Spawned one Einherjar Essence pickup."); }
        }
        catch (Exception error) { Reply(sender, id, "Test operation failed or uncertain; not retried: " + error.Message); }
    }
    private void ReceiveReply(long sender, ZPackage p)
    {
        if (sender != ZNet.GetUID() || !Accept(p)) return;
        var id = p.ReadLong(); var status = p.ReadString();
        if (pending != id) return;
        pending = 0; Status = status;
    }
    public void Update()
    {
        if (!stopped && Pending && Now - pendingAt >= 10)
        { pending = 0; Status = "Test operation timed out; outcome uncertain. It will not be replayed."; }
    }
    public void Stop() { stopped = true; pending = 0; replies.Clear(); }
}
