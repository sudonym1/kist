using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace SharedInventory;

internal static class RitualOutcomeAcceptance
{
    private static bool started;
    private static readonly List<string> checks = new List<string>();
    private static readonly List<string> messages = new List<string>();
    private static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
    private static string Root => Directory.GetParent(Paths.GameRootPath)!.FullName;
    private static string StatePath => Path.Combine(Paths.ConfigPath, "SharedInventory", "ritual-" + ZNet.instance.GetWorldUID() + ".json");
    public static void Update(Plugin plugin)
    {
        if (started || Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-outcome-tests") < 0 ||
            Game.instance == null || ZNetScene.instance == null || plugin.Ritual?.Outcomes?.Ready != true) return;
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && Player.m_localPlayer == null) return;
        started = true; plugin.StartCoroutine(Run(plugin));
    }
    private static void Check(bool pass, string label)
    {
        if (!pass) throw new InvalidOperationException("EINHERJAR OUTCOME ACCEPTANCE FAILED: " + label);
        checks.Add(label); Debug.Log("EINHERJAR OUTCOME PASS: " + label);
    }
    private static IEnumerator Run(Plugin plugin)
    {
        var run = Checks(plugin);
        while (true)
        {
            object? value;
            try { if (!run.MoveNext()) { Report("passed"); break; } value = run.Current; }
            catch (Exception error) { Debug.LogException(error); Report(error.ToString()); break; }
            yield return value;
        }
    }
    private static void Report(string result)
    {
        File.WriteAllLines(Path.Combine(Root, SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ?
            "outcome-headless-acceptance.txt" : "outcome-acceptance.txt"), new[] { result,
            "Valheim " + global::Version.GetVersionString(), "Unity " + Application.unityVersion,
            "World " + ZNet.instance.GetWorldUID() }.Concat(checks).Concat(messages.Select(m => "MESSAGE: " + m)));
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-exit-after-outcomes") >= 0) Application.Quit();
    }
    private static int EssenceCount() => UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
        .Where(d => d.GetComponent<ZNetView>()?.IsValid() == true && d.m_itemData.m_dropPrefab?.name == ContentIds.Essence)
        .Sum(d => d.m_itemData.m_stack);
    private static void Move(Player player, Vector3 p)
    {
        player.transform.position = p; player.GetComponent<Rigidbody>().position = p;
        player.GetComponent<ZNetView>().GetZDO().SetPosition(p);
    }
    private static ZPackage ControlRequest(RitualTestControls controls, long id, RitualTestAction action, string? token = null)
    {
        var p = new ZPackage(); p.Write(ZNet.instance.GetWorldUID());
        p.Write(token ?? (string)AccessTools.Field(typeof(RitualTestControls), "session").GetValue(controls));
        p.Write(id); p.Write((int)action); p.Write(Now + 10); return new ZPackage(p.GetArray());
    }
    private static ZPackage DeathPacket(Ritual ritual, RitualRecord record)
    {
        var victim = record.Victims.Single(); var p = new ZPackage(); p.Write(ZNet.instance.GetWorldUID());
        p.Write((string)AccessTools.Field(typeof(Ritual), "session").GetValue(ritual));
        p.Write(record.Id); p.Write(victim.Life); p.Write(victim.OwnerLife); p.Write(Now + 15); return new ZPackage(p.GetArray());
    }
    private static IEnumerator Checks(Plugin plugin)
    {
        var ritual = plugin.Ritual!; var engine = ritual.Outcomes!; var controls = plugin.TestingPanel!.RitualControls!;
        Check(ZNet.instance.IsServer() && File.Exists(StatePath), "World server asynchronously loads and persists a separate world-bound ritual file");
        DescribeObliterator();
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            Check(!controls.Operator, "Headless server accepts no client claim to local test-operator authority");
            var before = RitualLightning.Played; RitualLightning.Play(Vector3.zero);
            Check(RitualLightning.Played == before && Content.Models == null, "Headless outcome path creates no lightning, audio, camera or model resources");
            var deadline = engine.State.DeadlineUtcTicks; Ritual.SpawnEssence(new Vector3(0, 200, 0));
            Check(EssenceCount() >= 1 && deadline == engine.State.DeadlineUtcTicks, "Headless server can publish the registered physical essence without altering the timer");
            yield break;
        }
        var player = Player.m_localPlayer;
        while (player.InIntro() || player.IsTeleporting()) yield return null;
        Check(controls.Operator, "Guarded Testing controls authorize the graphical hosting player with faults disabled");
        var beforeResetSpawns = EssenceCount();
        controls.Press(RitualTestAction.ResetCooldown); controls.Press(RitualTestAction.SpawnEssence);
        Check(controls.Pending && EssenceCount() == beforeResetSpawns, "A pending persisted reset disables a second operation without replaying a spawn");
        var timeout = Now + 5;
        while (controls.Pending && Now < timeout) yield return null;
        Check(controls.Status == "Cooldown reset and persisted." && RitualStateFile.Load(StatePath, ZNet.instance.GetWorldUID()).DeadlineUtcTicks == 0,
            "Reset cooldown clears the server deadline and persists before reporting completion");
        var drops = EssenceCount(); var deadlineBefore = engine.State.DeadlineUtcTicks;
        controls.Press(RitualTestAction.SpawnEssence);
        Check(!controls.Pending && EssenceCount() == drops + 1 && controls.Status.StartsWith("Spawned one"),
            "Spawn essence creates exactly one real pickup without a chest or circle gathering");
        var duplicate = ControlRequest(controls, controls.LastOperation, RitualTestAction.SpawnEssence);
        AccessTools.Method(typeof(RitualTestControls), "Receive").Invoke(controls, new object[] { ZNet.GetUID(), duplicate });
        var unauthorized = ControlRequest(controls, controls.LastOperation + 100, RitualTestAction.SpawnEssence);
        AccessTools.Method(typeof(RitualTestControls), "Receive").Invoke(controls, new object[] { 912345L, unauthorized });
        var oldSession = ControlRequest(controls, controls.LastOperation + 100, RitualTestAction.SpawnEssence, "old-session");
        AccessTools.Method(typeof(RitualTestControls), "Receive").Invoke(controls, new object[] { ZNet.GetUID(), oldSession });
        Check(EssenceCount() == drops + 1 && engine.State.DeadlineUtcTicks == deadlineBefore,
            "Duplicate, unauthorized and stale-session test requests never add a second pickup or alter anger");
        player.m_autoPickupRange = 0;
        player.SetHealth(player.GetMaxHealth()); player.SetGodMode(false);
        var previewHealth = player.GetHealth(); var previewBolts = RitualLightning.Played; var previewDrops = EssenceCount();
        var previewAttempts = engine.State.Attempts.Count;
        controls.Press(RitualTestAction.PreviewLightning);
        Check(controls.Status == "Played Obliterator lightning and sound without damage.", "Test lightning button dispatches the original Obliterator presentation without a death command");
        var repeatedPreview = ControlRequest(controls, controls.LastOperation, RitualTestAction.PreviewLightning);
        AccessTools.Method(typeof(RitualTestControls), "Receive").Invoke(controls, new object[] { ZNet.GetUID(), repeatedPreview });
        yield return null;
        Check(!player.IsDead() && player.GetHealth() == previewHealth && RitualLightning.Played == previewBolts + 1 &&
            EssenceCount() == previewDrops && engine.State.Attempts.Count == previewAttempts && engine.State.DeadlineUtcTicks == deadlineBefore,
            "Lightning preview and duplicate preview requests cause one effect, zero damage, no death, no reward and no timer change");
        var window = (Window)AccessTools.Field(typeof(Plugin), "window").GetValue(plugin);
        AccessTools.Field(typeof(Window), "testingTab").SetValue(window, true);
        AccessTools.Field(typeof(TestPanel), "scroll").SetValue(plugin.TestingPanel, new Vector2(0, 500));
        AccessTools.Method(typeof(Plugin), "ToggleWindow").Invoke(plugin, null);
        Check(Plugin.WindowOpen, "Testing shell opens through the F3 entry");
        yield return null;
        window.Close();
        Check(ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out var original), "Outcome suite locates the original ritual site");
        player.TeleportTo(original.m_position + Vector3.up, Quaternion.identity, true);
        while (player.IsTeleporting()) yield return null;
        timeout = Now + 40;
        while (!RitualSite.TryGet(out _) && Now < timeout) yield return null;
        Check(RitualSite.TryGet(out var site), "Original stone circle is loaded");
        var rune = UnityEngine.Object.FindObjectsByType<RuneStone>(FindObjectsSortMode.None).Single(r => r.m_text == "$guardianstone_eikthyr_desc" && site.IsStone(r.GetComponent<BossStone>()));
        var nearPlaque = rune.transform.position + (site.Center - rune.transform.position).normalized * 1.4f + Vector3.up * .25f;
        Move(player, nearPlaque);
        yield return new WaitForSeconds(.8f);
        var structure = UnityEngine.Object.Instantiate(Content.Chest!, nearPlaque + Vector3.right * 1.5f, Quaternion.identity);
        var wear = structure.GetComponent<WearNTear>(); var structureHealth = wear.GetHealthPercentage();
        var stockPath = Path.Combine(Paths.ConfigPath, "SharedInventory", ZNet.instance.GetWorldUID() + ".json");
        var stock = File.Exists(stockPath) ? File.ReadAllText(stockPath) : "";
        // Force a normal tombstone and prove god mode cannot protect the chosen life.
        player.GetInventory().AddItem(Items.Default("Wood")!); player.SetGodMode(true);
        var graves = UnityEngine.Object.FindObjectsByType<TombStone>(FindObjectsSortMode.None).Length;
        drops = EssenceCount(); var bolts = RitualLightning.Played;
        rune.Interact(player, false, true);
        timeout = Now + 10;
        while (!player.IsDead() && Now < timeout) yield return null;
        Check(player.IsDead() && RitualLightning.Played == bolts + 1, "Eligible solo ritual strikes once and kills through owner-side OnDeath despite god mode");
        var bolt = GameObject.Find("Einherjar ritual lightning");
        Check(bolt != null && bolt.GetComponentsInChildren<ParticleSystem>().Length > 0 &&
            bolt.GetComponentsInChildren<ZSFX>().SelectMany(s => s.m_audioClips).Any(c => c.name.StartsWith("Obliterator_Lightning_")) &&
            bolt.GetComponentsInChildren<Collider>(true).Length == 0 && bolt.GetComponentsInChildren<Aoe>(true).Length == 0 &&
            bolt.GetComponentsInChildren<ZNetView>(true).Length == 0,
            "Real-engine strike reuses Obliterator particles and original thunder clips with all damage, colliders and network components removed");
        yield return null; // The game's ZSFX updater starts sound after Awake.
        Check(bolt!.GetComponentsInChildren<AudioSource>().Any(s => s.isPlaying && s.clip != null && s.clip.name.StartsWith("Obliterator_")),
            "The original Obliterator sound actually plays during the strike");
        yield return new WaitForEndOfFrame();
        CaptureBolt(site, player.transform.position);
        timeout = Now + 5;
        while (ritual.Active && Now < timeout) yield return null;
        Check(!ritual.Active && EssenceCount() == drops + 1 && UnityEngine.Object.FindObjectsByType<TombStone>(FindObjectsSortMode.None).Length == graves + 1,
            "Confirmed ordinary death completes tombstone handling and publishes exactly one physical essence afterward");
        var sacrifice = engine.State.Attempts.Last().Copy();
        var persisted = RitualStateFile.Load(StatePath, ZNet.instance.GetWorldUID());
        Check(!sacrifice.Punishment && sacrifice.Reward == "published" && persisted.Attempts.Last().Reward == "published" && persisted.DeadlineUtcTicks > DateTime.UtcNow.Ticks,
            "Confirmed sacrifice persists its deadline and deduplicated reward journal");
        AccessTools.Method(typeof(Ritual), "ReceiveDeath").Invoke(ritual, new object[] { ZNet.GetUID(), DeathPacket(ritual, sacrifice) });
        Check(RitualLightning.Played == bolts + 1 && EssenceCount() == drops + 1, "Duplicate death command cannot repeat its strike or reward");
        controls.Press(RitualTestAction.SpawnEssence);
        Check(controls.Status.StartsWith("Spawn unavailable"), "Dead test players receive a clear spawn-unavailable reply");
        controls.Press(RitualTestAction.PreviewLightning);
        Check(controls.Status.StartsWith("Lightning unavailable") && RitualLightning.Played == bolts + 1,
            "Dead test players cannot trigger lightning previews");
        timeout = Now + 40;
        while ((Player.m_localPlayer == null || Player.m_localPlayer.IsDead() || Player.m_localPlayer.IsTeleporting()) && Now < timeout) yield return null;
        player = Player.m_localPlayer;
        Check(player != null && !player.IsDead(), "Normal game respawn completes after ritual death");
        player!.SetGodMode(false);
        AccessTools.Method(typeof(Ritual), "ReceiveDeath").Invoke(ritual, new object[] { ZNet.GetUID(), DeathPacket(ritual, sacrifice) });
        Check(!player.IsDead() && RitualLightning.Played == bolts + 1, "A delayed command with renewed packet expiry cannot kill a respawned character life");
        Move(player, nearPlaque); yield return new WaitForSeconds(.8f);
        drops = EssenceCount(); deadlineBefore = engine.State.DeadlineUtcTicks;
        rune.Interact(player, false, true);
        timeout = Now + 3;
        while (ritual.Gathering.State != RitualGathering.Stage.Countdown && Now < timeout) yield return null;
        Check(ritual.Gathering.State == RitualGathering.Stage.Countdown, "Early repeat remains available and starts the same five-second countdown");
        controls.Press(RitualTestAction.ResetCooldown);
        Check(controls.Status.Contains("while a ritual is active") && engine.State.DeadlineUtcTicks == deadlineBefore,
            "Server rejects cooldown reset during an active countdown without changing its outcome");
        timeout = Now + 10;
        while (!player.IsDead() && Now < timeout) yield return null;
        Check(player.IsDead() && messages.Contains(RitualOutcomes.PunishmentMessage) && RitualLightning.Played == bolts + 2,
            "Early repeat shows exact Odin punishment text and strikes every committed solo participant");
        yield return new WaitForEndOfFrame();
        Check(messages.Last() == "$msg_youdied" && ritual.Announcement == RitualOutcomes.PunishmentMessage,
            "Odin's visible ritual announcement survives the game's center-text replacement on death");
        timeout = Now + 5;
        while (ritual.Active && Now < timeout) yield return null;
        Check(!ritual.Active && EssenceCount() == drops && engine.State.Attempts.Last().Punishment && engine.State.Attempts.Last().Reward == "none" &&
            engine.State.DeadlineUtcTicks > deadlineBefore, "Punishment restarts persisted anger and produces no essence");
        Check(structure != null && wear.GetHealthPercentage() == structureHealth, "A structure beside both strikes takes no incidental lightning damage");
        ZNetScene.instance.Destroy(structure);
        Check((File.Exists(stockPath) ? File.ReadAllText(stockPath) : "") == stock, "Ritual deaths, rewards and test controls never mutate shared stock");
        controls.Press(RitualTestAction.ResetCooldown); timeout = Now + 5;
        while (controls.Pending && Now < timeout) yield return null;
        Check(RitualStateFile.Load(StatePath, ZNet.instance.GetWorldUID()).DeadlineUtcTicks == 0, "Final Testing reset survives a disk reload without any numeric timer UI");
    }

    private static void CaptureBolt(RitualSite site, Vector3 position)
    {
        var obj = new GameObject("Einherjar outcome review camera"); var camera = obj.AddComponent<Camera>();
        camera.CopyFrom(Camera.main); camera.enabled = false;
        camera.transform.position = site.Center + new Vector3(15, 9, -18); camera.transform.LookAt(position + Vector3.up * 7);
        var target = RenderTexture.GetTemporary(1024, 768, 24); var texture = new Texture2D(1024, 768, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try
        {
            camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 1024, 768), 0, 0); texture.Apply();
            File.WriteAllBytes(Path.Combine(Root, "ritual-lightning-review.png"), texture.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous; UnityEngine.Object.Destroy(obj); UnityEngine.Object.Destroy(texture); RenderTexture.ReleaseTemporary(target);
        }
    }
    private static void DescribeObliterator()
    {
        var incinerator = ZNetScene.instance.m_prefabs.Select(p => p.GetComponent<Incinerator>()).First(i => i != null);
        var lines = new List<string>(); var seen = new HashSet<GameObject>();
        void Describe(GameObject prefab, int depth)
        {
            if (prefab == null || depth > 4 || !seen.Add(prefab)) return;
            lines.Add("PREFAB " + prefab.name);
            foreach (var component in prefab.GetComponentsInChildren<Component>(true))
            {
                lines.Add(component.gameObject.name + ": " + component.GetType().Name);
                if (component is AudioSource source) lines.Add("AUDIO " + (source.clip == null ? "null" : source.clip.name));
                if (component is ParticleSystem particles)
                {
                    var main = particles.main;
                    lines.Add("PARTICLES pos=" + particles.transform.localPosition + " delay=" + main.startDelay.constantMin + "/" + main.startDelay.constantMax +
                        " life=" + main.startLifetime.constantMin + "/" + main.startLifetime.constantMax + " duration=" + main.duration);
                }
                if (component is ZSFX sound) lines.Add("SOUND DELAY " + sound.m_minDelay + "/" + sound.m_maxDelay);
                if (component is not MonoBehaviour) continue;
                foreach (var field in component.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                {
                    var value = field.GetValue(component);
                    if (value is GameObject linked) { lines.Add("LINK " + field.Name + " -> " + linked.name); Describe(linked, depth + 1); }
                    if (value is AudioClip[] clips) lines.Add("CLIPS " + field.Name + ": " + string.Join(", ", clips.Select(c => c == null ? "null" : c.name)));
                    if (value is EffectList effects)
                        foreach (var effect in effects.m_effectPrefabs) if (effect.m_prefab != null)
                        { lines.Add("EFFECT " + field.Name + " -> " + effect.m_prefab.name); Describe(effect.m_prefab, depth + 1); }
                }
            }
        }
        Describe(incinerator.m_lightingAOEs, 0);
        File.WriteAllLines(Path.Combine(Root, "obliterator-lightning-prefabs.txt"), lines);
    }
    [HarmonyPatch(typeof(Player), nameof(Player.Message))]
    private static class ObserveMessages
    {
        private static void Prefix(MessageHud.MessageType type, string msg)
        { if (started && type == MessageHud.MessageType.Center) messages.Add(msg); }
    }
}
