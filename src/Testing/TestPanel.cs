using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SharedInventory;

internal sealed class TestPanel
{
    private readonly ConfigFile config;
    private readonly ManualLogSource log;
    private RpcApi? api;
    private Client? client;
    private TestHarness? harness;
    internal RitualTestControls? RitualControls { get; private set; }
    private readonly RavenTestControls ravenControls;
    internal bool RavenSampleActive => ravenControls.Active;
    private string status = "";
    private string diagnostics = "";
    private double nextDiagnostics;
    private Vector2 scroll;
    private GUIStyle? ravenStatusStyle;

    private TestPanel(ConfigFile config, ManualLogSource log)
    {
        this.config = config; this.log = log;
        ravenControls = new RavenTestControls(error => log.LogError(error));
    }
    public static TestPanel? Create(ConfigFile config, ManualLogSource log)
    {
        if (!config.Bind("Testing", "Enabled", false, "Enable the test panel only in a guarded isolated test launch. Restart required.").Value) return null;
        var args = Environment.GetCommandLineArgs();
        var root = Directory.GetParent(Paths.GameRootPath)?.FullName;
        var marker = root == null ? "" : Path.Combine(root, ".sharedinventory-test");
        var index = Array.IndexOf(args, "-savedir");
        if (Array.IndexOf(args, "-sharedinventory-test") < 0 || !File.Exists(marker) ||
            File.ReadAllText(marker).Trim() != "sharedinventory-test-v1" ||
            index < 0 || index + 1 >= args.Length || root == null ||
            !string.Equals(Path.GetFullPath(args[index + 1]).TrimEnd(Path.DirectorySeparatorChar),
                Path.Combine(root, "saves"), StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Test harness remains disabled: use the guarded isolated test launcher.");
            return null;
        }
        log.LogWarning("Einherjar Chest test harness enabled; impairments default OFF.");
        return new TestPanel(config, log);
    }
    public TestHarness NewSession()
    {
        int Number(string name, int fallback = 0, int maximum = 60000) => Math.Max(0, Math.Min(maximum,
            config.Bind("Testing", name, fallback, "Test-only setting; restart to reload configuration.").Value));
        return harness = new TestHarness(new TestSettings
        {
            Active = config.Bind("Testing", "Impairments", false, "Enable configured faults. Defaults off.").Value,
            Seed = Number("Seed", 12345, int.MaxValue), RequestDelayMs = Number("RequestDelayMs"),
            ReplyDelayMs = Number("ReplyDelayMs"), JitterMs = Number("JitterMs"),
            BytesPerSecond = Number("BytesPerSecond", 0, int.MaxValue), ProcessingDelayMs = Number("ProcessingDelayMs"),
            SaveDelayMs = Number("SaveDelayMs"),
            TargetPeer = config.Bind("Testing", "TargetPeer", 0L, "Target peer ID; zero affects all peers.").Value
        });
    }
    public void Attach(RpcApi api, Client client) { this.api = api; this.client = client; }
    public void AttachRitual(ZRoutedRpc router, Ritual ritual) => RitualControls = new RitualTestControls(router, ritual);
    public void Update()
    {
        ravenControls.Update();
        RitualControls?.Update();
        if (harness == null || api == null) return;
        harness.Metrics.Observe("frameMs", Time.unscaledDeltaTime * 1000);
        if (harness.Now < nextDiagnostics) return;
        nextDiagnostics = harness.Now + .5;
        diagnostics = $"Worker queue {api.WorkerQueueDepth} | Main queue {api.MainQueueDepth}\n" +
            $"Network queue {harness.Queued} | Held {harness.Held} | Oldest {harness.OldestMs:F0} ms\n" +
            $"Pending refresh {harness.PendingRefreshes} | Transfers {client?.PendingTransfers} | Batches {client?.PendingBatches}\n" +
            $"Oldest request {harness.OldestPendingMs:F0} ms | Cache age " +
            (client?.LastSnapshotAt > 0 ? $"{harness.Now - client.LastSnapshotAt:F1} s" : "unknown");
        var snapshot = JObject.FromObject(harness.Snapshot(false));
        var samples = snapshot["Metrics"]!["Samples"]!;
        var counters = snapshot["Metrics"]!["Counters"]!;
        diagnostics += $"\nRefresh timeouts {counters["refreshTimeouts"] ?? 0} | Retries {counters["refreshRetries"] ?? 0} | Ignored replies {counters["ignoredRefreshReplies"] ?? 0}";
        var elapsed = Math.Max(.001, snapshot["ElapsedSeconds"]!.Value<double>());
        double Value(string metric, string statistic) => samples[metric]?[statistic]?.Value<double>() ?? 0;
        diagnostics += $"\nRTT p50/p95/p99 {Value("roundTripMs", "P50"):F0}/{Value("roundTripMs", "P95"):F0}/{Value("roundTripMs", "P99"):F0} ms\n" +
            $"Replies/s {(counters["completed"]?.Value<long>() ?? 0) / elapsed:F1} | Frame p95 {Value("frameMs", "P95"):F1} ms\n" +
            $"Worker wait p95 {Value("workerWaitMs", "P95"):F0} ms | Save max {Value("saveMs", "Max"):F0} ms\n" +
            $"Traffic {((counters["requestBytes"]?.Value<long>() ?? 0) + (counters["replyBytes"]?.Value<long>() ?? 0)) / elapsed / 1024:F1} KiB/s";
    }
    private void Export()
    {
        if (harness == null) return;
        try
        {
            var path = Path.Combine(Paths.ConfigPath, "SharedInventory-TestReports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json");
            harness.Export(path, new { Counts = client?.Counts, Qualities = client?.Qualities,
                PendingRefreshes = harness.PendingRefreshes, RefreshStatus = client?.RefreshStatus,
                PendingTransfers = client?.PendingTransfers, PendingBatches = client?.PendingBatches });
            status = "Report exported; path is in the BepInEx log.";
            log.LogInfo("Test report: " + path);
        }
        catch (Exception error) { status = "Export failed: " + error.Message; log.LogError(error); }
    }
    public void Detach()
    {
        ravenControls.Stop();
        RitualControls?.Stop(); RitualControls = null;
        Export();
        api = null; client = null; harness = null;
    }
    public void DrawContents(float width, float height)
    {
        if (harness == null) return;
        var settings = harness.Settings;
        scroll = GUI.BeginScrollView(new Rect(0, 0, width, height), scroll, new Rect(0, 0, width - 18, 1030));
        var w = width - 20;
        GUI.Label(new Rect(0, 0, w, 25), "ISOLATED TEST HARNESS - " + (settings.Active ? "FAULTS ACTIVE" : "faults off"));
        if (GUI.Button(new Rect(0, 28, w, 25), "Disable faults and release queued packets")) harness.DisableFaults();
        if (GUI.Button(new Rect(0, 58, w / 2 - 3, 25), "Slow connection"))
        { harness.DisableFaults(); settings.Active = true; settings.RequestDelayMs = settings.ReplyDelayMs = 150; settings.JitterMs = 50; settings.BytesPerSecond = 0; }
        if (GUI.Button(new Rect(w / 2, 58, w / 2, 25), "Limited bandwidth"))
        { harness.DisableFaults(); settings.Active = true; settings.RequestDelayMs = settings.ReplyDelayMs = 50; settings.JitterMs = 0; settings.BytesPerSecond = 16000; }
        if (GUI.Button(new Rect(0, 88, w / 2 - 3, 25), "Outage 10 seconds")) { settings.Active = true; harness.Outage(10); }
        if (GUI.Button(new Rect(w / 2, 88, w / 2, 25), "Release held replies")) harness.Release();
        if (GUI.Button(new Rect(0, 118, w / 2 - 3, 25), "Hold next reply")) { settings.Active = true; harness.HoldNext = "Reply"; }
        if (GUI.Button(new Rect(w / 2, 118, w / 2, 25), "Discard next reply")) { settings.Active = true; harness.DropNext = "Reply"; }
        if (GUI.Button(new Rect(0, 148, w / 2 - 3, 25), "Hold next request")) { settings.Active = true; harness.HoldNext = "Request"; }
        if (GUI.Button(new Rect(w / 2, 148, w / 2, 25), "Discard next request")) { settings.Active = true; harness.DropNext = "Request"; }
        if (GUI.Button(new Rect(0, 178, w / 2 - 3, 25), "Worker delay 100 ms")) { settings.Active = true; settings.ProcessingDelayMs = 100; }
        if (GUI.Button(new Rect(w / 2, 178, w / 2, 25), "Save delay 2 seconds")) { settings.Active = true; settings.SaveDelayMs = 2000; }
        if (GUI.Button(new Rect(0, 208, w / 2 - 3, 25), "Fail next save")) { settings.Active = true; settings.SaveFailures = 1; }
        if (GUI.Button(new Rect(w / 2, 208, w / 2, 25), "Export report")) Export();
        GUI.Label(new Rect(0, 242, w, 44), $"Request {settings.RequestDelayMs} ms | Reply {settings.ReplyDelayMs} ms\nJitter {settings.JitterMs} ms | Rate {settings.BytesPerSecond} B/s (0 unlimited)");
        GUI.Label(new Rect(0, 290, w, 200), diagnostics);
        GUI.Label(new Rect(0, 500, w, 75), "These faults affect Einherjar Chest RPCs.\nDiscarding a reply can leave a transfer uncertain.\nItem mutations are never automatically retried or restored.");
        var enabled = GUI.enabled;
        GUI.enabled = enabled && RitualControls?.Operator == true && RitualControls.Pending == false;
        if (GUI.Button(new Rect(0, 580, w / 2 - 3, 28), "Reset cooldown")) RitualControls?.Press(RitualTestAction.ResetCooldown);
        if (GUI.Button(new Rect(w / 2, 580, w / 2, 28), "Spawn essence")) RitualControls?.Press(RitualTestAction.SpawnEssence);
        if (GUI.Button(new Rect(0, 614, w, 28), "Test lightning")) RitualControls?.Press(RitualTestAction.PreviewLightning);
        GUI.enabled = enabled;
        GUI.Label(new Rect(0, 650, w, 80), RitualControls?.Status ?? "Ritual test controls unavailable.");
        GUI.enabled = enabled && ravenControls.CanQueue;
        if (GUI.Button(new Rect(0, 738, w, 28), "Queue / replay Hugin tutorial (POC)"))
        { Plugin.Instance?.Tutorials?.Pause(); ravenControls.Press(); }
        GUI.enabled = enabled;
        ravenStatusStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.UpperLeft };
        GUI.Label(new Rect(0, 772, w, 140), ravenControls.Status, ravenStatusStyle);
        GUI.Label(new Rect(0, 920, w, 100), status);
        GUI.EndScrollView();
    }
}
