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

// Explicit guarded isolated-game acceptance; absent from production.
internal static class RitualAcceptance
{
    private static bool started;
    private static readonly List<string> checks = new List<string>();
    private static readonly List<string> messages = new List<string>();
    private static string Root => Directory.GetParent(Paths.GameRootPath)!.FullName;
    internal static void Update(Plugin plugin)
    {
        if (started || Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-ritual-tests") < 0 ||
            ZNetScene.instance == null || Game.instance == null) return;
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && Player.m_localPlayer == null) return;
        started = true; plugin.StartCoroutine(Run(plugin));
    }
    private static void Check(bool passed, string label)
    {
        if (!passed) throw new InvalidOperationException("EINHERJAR RITUAL ACCEPTANCE FAILED: " + label);
        checks.Add(label); Debug.Log("EINHERJAR RITUAL PASS: " + label);
    }
    private static IEnumerator Run(Plugin plugin)
    {
        var run = Checks(plugin);
        while (true)
        {
            object? current;
            try
            {
                if (!run.MoveNext()) { Report("passed"); break; }
                current = run.Current;
            }
            catch (Exception error) { Debug.LogException(error); Report(error.ToString()); break; }
            yield return current;
        }
    }
    private static void Report(string result)
    {
        File.WriteAllLines(Path.Combine(Root, SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ?
            "ritual-headless-acceptance.txt" : "ritual-acceptance.txt"), new[] { result,
            "Valheim " + global::Version.GetVersionString(), "Unity " + Application.unityVersion,
            "World " + ZNet.instance.GetWorldUID() }.Concat(checks).Concat(messages.Select(m => "MESSAGE: " + m)));
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-exit-after-ritual") >= 0) Application.Quit();
    }
    private static void Move(Player player, Vector3 position)
    {
        player.transform.position = position; player.GetComponent<Rigidbody>().position = position;
        player.GetComponent<ZNetView>().GetZDO().SetPosition(position);
    }
    private static double TimeNow => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
    private static int Number(Ritual ritual)
    {
        return ritual.CountdownNumber;
    }
    private static IEnumerator Checks(Plugin plugin)
    {
        var graphical = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
        var ritual = plugin.Ritual ?? throw new InvalidOperationException("Ritual endpoint is not attached");
        Check(ZNet.instance.IsServer(), "Ritual endpoint attaches to the actual world router");
        Check(ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out var original), "Server resolves the original StartTemple world identity");
        var player = Player.m_localPlayer;
        if (graphical)
        {
            while (player.InIntro() || player.IsTeleporting()) yield return null;
            player.TeleportTo(original.m_position + Vector3.up, Quaternion.identity, true);
            while (player.IsTeleporting()) yield return null;
        }
        else ZNet.instance.SetReferencePosition(original.m_position);
        var timeout = TimeNow + 40;
        while (!RitualSite.TryGet(out _) && TimeNow < timeout) yield return null;
        Check(RitualSite.TryGet(out var site), "Original location streams in through the real scene lifecycle");
        var stones = Array.Empty<BossStone>();
        timeout = TimeNow + 10;
        while (TimeNow < timeout)
        {
            stones = UnityEngine.Object.FindObjectsByType<BossStone>(FindObjectsSortMode.None).Where(site.IsStone).ToArray();
            if (stones.Length == 7) break;
            yield return null; // Location proxies can stream before their networked stones.
        }
        Check(stones.Length == 7, "All seven original sacrificial stones qualify; found " + stones.Length + "; " +
            string.Join("; ", UnityEngine.Object.FindObjectsByType<BossStone>(FindObjectsSortMode.None).Select(s =>
                s.name + " local=" + (Quaternion.Inverse(site.Rotation) * (s.transform.position - site.Center)) +
                " valid=" + s.GetComponent<ZNetView>()?.IsValid())));
        var runes = UnityEngine.Object.FindObjectsByType<RuneStone>(FindObjectsSortMode.None);
        var rune = stones.Select(s => s.GetComponent<RuneStone>()).Single(r => r.m_text == "$guardianstone_eikthyr_desc");
        Check(rune.name == "BossStone_Eikthyr(Clone)" && rune.m_text == "$guardianstone_eikthyr_desc",
            "Actual plaque is RuneStone on BossStone_Eikthyr, not the altar or vegvisir");
        Check(runes.Where(r => r.GetComponent<BossStone>() == null).All(r => !RitualSite.IsOriginal(r.GetComponent<BossStone>())),
            "Unrelated runestones do not qualify");
        foreach (var originalStone in stones)
        {
            var prefab = ZNetScene.instance.GetPrefab(originalStone.GetComponent<ZNetView>().GetZDO().GetPrefab());
            foreach (var position in new[] { site.Center, site.Center + Vector3.right * 35 })
            {
                var copy = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
                Check(!RitualSite.IsOriginal(copy.GetComponent<BossStone>()),
                    "Copied " + prefab.name + " outside an original stone position cannot trigger the ritual");
                ZNetScene.instance.Destroy(copy);
            }
        }
        Check(site.Contains(site.Center) && !site.Contains(site.Center + Vector3.up * 4) && !site.Contains(site.Center - Vector3.up * 2),
            "Ground-level interior rejects above/below-site positions");
        foreach (var vertex in RitualSite.Perimeter)
        {
            var inside = site.Center + site.Rotation * new Vector3(vertex.x * .92f, .1f, vertex.y * .92f);
            var outside = site.Center + site.Rotation * new Vector3(vertex.x * 1.08f, .1f, vertex.y * 1.08f);
            Check(site.Contains(inside) && !site.Contains(outside), "Measured standing-stone boundary at " + vertex);
        }
        Check(!site.Contains(site.Center + Vector3.right * 19), "Nearby terrain outside the stones is rejected");
        Check(UnityEngine.Object.FindObjectsByType<Location>(FindObjectsSortMode.None)
            .Where(l => l.name == "StoneCircle(Clone)").All(l => !site.Contains(l.transform.position)),
            "Other actual loaded stone circles are outside the gathering boundary");
        if (!graphical)
        {
            Check(ritual.Roster(false).Count == 0, "Headless server process is not a participant");
            Check(UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).All(c => !c.name.StartsWith("Einherjar ritual")),
                "Headless ritual validation creates no cameras");
            yield break;
        }
        var stand = rune.GetComponent<BossStone>().m_itemStand ?? throw new InvalidOperationException("Missing trophy stand");
        Check(rune.GetComponent<BossStone>().m_itemStand != null, "Vanilla trophy and guardian-power ItemStand remains attached");
        Move(player, site.Center + Vector3.up * .1f);
        var playerData = player.GetComponent<ZNetView>().GetZDO();
        player.SetHealth(player.GetMaxHealth());
        playerData.RemoveFloat(ZDOVars.s_health);
        Check(ritual.Roster(false).Single().Unavailable.Length == 0,
            "A fully healthy player with the game's omitted health field remains available");
        playerData.Set(ZDOVars.s_health, 0f);
        try
        {
            Check(ritual.Roster(false).Single().Unavailable == "dead/unavailable", "Explicit zero health still blocks the ritual");
        }
        finally { playerData.RemoveFloat(ZDOVars.s_health); }
        foreach (var originalStone in stones)
        {
            var reading = originalStone.GetComponent<RuneStone>() ?? throw new InvalidOperationException("Missing reading surface");
            var trophyStand = originalStone.m_itemStand ?? throw new InvalidOperationException("Missing trophy control");
            Move(player, originalStone.transform.position + (site.Center - originalStone.transform.position).normalized * 1.4f + Vector3.up * .25f);
            yield return new WaitForSeconds(.8f);
            Check(reading.GetHoverText().Contains("Begin Einherjar ritual") && trophyStand.GetHoverText().Contains("Begin Einherjar ritual"),
                originalStone.name + " offers the ritual on both reading and trophy surfaces");
            reading.Interact(player, false, false);
            Check(TextViewer.instance.IsVisible() && !ritual.Active, originalStone.name + " preserves normal reading");
            TextViewer.instance.Hide();
            foreach (var control in new Interactable[] { reading, trophyStand })
            {
                control.Interact(player, false, true);
                timeout = TimeNow + 3;
                while (ritual.Gathering.State != RitualGathering.Stage.Countdown && TimeNow < timeout) yield return null;
                Check(ritual.Gathering.State == RitualGathering.Stage.Countdown,
                    originalStone.name + " starts through " + control.GetType().Name + " alternate use");
                Move(player, site.Center + Vector3.right * 20);
                timeout = TimeNow + 2;
                while (ritual.Active && TimeNow < timeout) yield return null;
                Check(!ritual.Active && Number(ritual) == 0 && messages.Last() == "Ritual Cancelled",
                    "Leaving " + originalStone.name + " shows only Ritual Cancelled");
                Move(player, originalStone.transform.position + (site.Center - originalStone.transform.position).normalized * 1.4f + Vector3.up * .25f);
                yield return new WaitForSeconds(.8f);
            }
        }
        Move(player, rune.transform.position + (site.Center - rune.transform.position).normalized * 1.4f + Vector3.up * .25f);
        yield return new WaitForSeconds(1);
        rune.Interact(player, false, false);
        Check(TextViewer.instance.IsVisible() && !ritual.Active, "Normal plaque reading opens vanilla text and starts no ritual");
        TextViewer.instance.Hide();
        Check(!rune.UseItem(player, null!), "Plaque UseItem retains vanilla behavior, separate from trophy input");
        var hover = rune.GetHoverText();
        Check(hover.Contains("Begin Einherjar ritual") && hover.Contains("chosen at random") && !hover.Contains("30"),
            "Hover shows deliberate alternate use and a thematic warning without timer data");
        yield return new WaitForSeconds(.8f);
        rune.Interact(player, false, true);
        Check(ritual.Active, "A valid request begins presence validation");
        Move(player, site.Center + Vector3.right * 20);
        timeout = TimeNow + 3;
        while (ritual.Active && TimeNow < timeout) yield return null;
        Check(!ritual.Active && Number(ritual) == 0 && messages.Last() == "Ritual Cancelled",
            "Leaving during initial presence validation also shows only Ritual Cancelled");
        Move(player, rune.transform.position + (site.Center - rune.transform.position).normalized * 1.4f + Vector3.up * .25f);
        yield return new WaitForSeconds(.8f);
        ZNet.instance.SetPublicReferencePosition(false);
        Check(ritual.Roster(false).Count == 1 && ritual.Roster(false)[0].Peer == ZNet.GetUID(), "Private-map host remains in the connected roster");
        yield return new WaitForSeconds(.8f);
        var fake = new ZNetPeer(null!, false) { m_uid = 912345, m_playerName = "MissingRemote" };
        ZNet.instance.GetPeers().Add(fake);
        try
        {
            rune.Interact(player, false, true);
            Check(!ritual.Active && messages.Last().Contains("MissingRemote (loading/respawning)"),
                "Connected loading remote blocks initiation with a named center-screen error");
        }
        finally { ZNet.instance.GetPeers().Remove(fake); }
        var errors = messages.Count;
        for (var i = 0; i < 30; i++) rune.Interact(player, true, true);
        Check(errors == messages.Count && !ritual.Active, "Held alternate use cannot spam errors or restart attempts");
        yield return new WaitForSeconds(.8f);
        var character = player.GetComponent<ZNetView>().GetZDO();
        character.Set(ZDOVars.s_dead, true);
        try
        {
            rune.Interact(player, false, true);
            Check(!ritual.Active && messages.Last().Contains("EinherjarAssets (dead/unavailable)"),
                "Dead host remains required and blocks initiation before countdown");
        }
        finally { character.Set(ZDOVars.s_dead, false); }
        yield return new WaitForSeconds(.8f);
        // Display packets must come from the world server; even a high revision
        // from another peer cannot start a local countdown.
        var forged = new ZPackage(); forged.Write(ZNet.instance.GetWorldUID()); forged.Write("forged"); forged.Write(long.MaxValue);
        forged.Write("forged-attempt"); forged.Write(""); forged.Write(""); forged.Write(TimeNow); forged.Write(0d); forged.Write(true); forged.Write(TimeNow);
        AccessTools.Method(typeof(Ritual), "ReceiveDisplay").Invoke(ritual, new object[] { 912345L, new ZPackage(forged.GetArray()) });
        Check(Number(ritual) == 0, "A non-server display packet cannot start or replace the countdown");
        rune.Interact(player, false, true);
        timeout = TimeNow + 3;
        while (ritual.Gathering.State != RitualGathering.Stage.Countdown && TimeNow < timeout) yield return null;
        Check(ritual.Gathering.State == RitualGathering.Stage.Countdown && Number(ritual) == 5,
            "Valid solo gathering starts 5... through actual routed presence confirmations");
        var id = ritual.Gathering.Id;
        Move(player, site.Center + site.Rotation * new Vector3(-8.2f, .1f, 6.5f));
        yield return null; ritual.Update();
        Check(!ritual.Active && Number(ritual) == 0 && messages.Last() == "Ritual Cancelled",
            "Leaving the circle cancels and clears the countdown with the short message");
        yield return new WaitForSeconds(.8f);
        rune.Interact(player, false, true);
        Check(!ritual.Active && Number(ritual) == 0 && messages.Last().Contains("EinherjarAssets (outside the circle)"),
            "Near-but-outside host gets a visible named error with no countdown");
        yield return new WaitForSeconds(.8f);
        Move(player, rune.transform.position + (site.Center - rune.transform.position).normalized * 1.4f + Vector3.up * .25f);
        yield return new WaitForSeconds(.3f);
        rune.Interact(player, false, true);
        timeout = TimeNow + 3;
        while (ritual.Gathering.State != RitualGathering.Stage.Countdown && TimeNow < timeout) yield return null;
        Check(ritual.Gathering.State == RitualGathering.Stage.Countdown && ritual.Gathering.Id != id, "Restart uses a fresh ritual ID");
        ZNet.instance.GetPeers().Add(fake);
        try
        {
            ritual.Update();
            Check(!ritual.Active && Number(ritual) == 0 && messages.Last() == "Ritual Cancelled",
                "Joining peer cancels a live countdown before commitment");
        }
        finally { ZNet.instance.GetPeers().Remove(fake); }
        yield return new WaitForSeconds(.8f);
        var commits = 0;
        ritual.Committed += (_, roster) => { commits++; Check(roster.Count == 1, "Commit captures the validated host life exactly once"); };
        rune.Interact(player, false, true);
        var numbers = new List<int>();
        timeout = TimeNow + 9;
        double began = 0;
        while (commits == 0 && TimeNow < timeout)
        {
            var number = Number(ritual);
            if (number != 0 && (numbers.Count == 0 || numbers[numbers.Count - 1] != number))
            {
                numbers.Add(number); if (began == 0) began = TimeNow;
                Debug.Log("EINHERJAR RITUAL VISIBLE COUNTDOWN: " + number + "... at " + TimeNow);
            }
            yield return null;
        }
        var elapsed = TimeNow - began;
        Check(numbers.SequenceEqual(new[] { 5, 4, 3, 2, 1 }) && commits == 1 && elapsed >= 4.9 && elapsed < 5.3,
            "Live client displays all five one-second steps and commits at five seconds without extra delay");
        yield return new WaitForSeconds(.5f);
        Check(commits == 1 && Number(ritual) == 0 && !player.IsDead(), "Phase 4 ends with one gathering handoff and no phase 5 death/reward");
        Check(MessageHud.instance != null && messages.Count > 0, "Errors and cancellation use the actual game message UI");
        if (!stand.HaveAttachment())
        {
            var power = player.GetGuardianPowerName();
            var trophy = Items.Default(stand.m_supportedItems.Single().name)!;
            player.GetInventory().AddItem(trophy);
            try
            {
                Check(stand.UseItem(player, trophy), "Unmodified trophy stand accepts a carried Eikthyr trophy");
                yield return new WaitForSeconds(.5f);
                Check(stand.HaveAttachment() && !ritual.Active, "Trophy placement still completes without triggering a ritual");
                player.SetGuardianPower("");
                Check(stand.Interact(player, false, false), "Normal guardian-power interaction still activates");
                yield return new WaitForSeconds(stand.m_powerActivationDelay + .25f);
                Check(player.GetGuardianPowerName() == stand.m_guardianPower.name && !ritual.Active,
                    "Vanilla Eikthyr power selection completes independently of the ritual");
            }
            finally
            {
                stand.DestroyAttachment(); player.SetGuardianPower(power);
                if (player.GetInventory().ContainsItem(trophy)) player.GetInventory().RemoveItem(trophy);
            }
        }
        CaptureSite(site);
    }
    private static void CaptureSite(RitualSite site)
    {
        var obj = new GameObject("Einherjar ritual boundary review");
        var camera = obj.AddComponent<Camera>(); camera.CopyFrom(Camera.main); camera.enabled = false;
        camera.transform.position = site.Center + new Vector3(0, 38, -.01f);
        camera.transform.rotation = Quaternion.Euler(90, 0, 0);
        camera.orthographic = true; camera.orthographicSize = 16; camera.nearClipPlane = .1f; camera.farClipPlane = 80;
        var target = RenderTexture.GetTemporary(1024, 1024, 24);
        var texture = new Texture2D(1024, 1024, TextureFormat.RGB24, false);
        var previous = RenderTexture.active; var fog = RenderSettings.fog;
        var boundary = new GameObject("Einherjar measured boundary");
        var line = boundary.AddComponent<LineRenderer>(); var material = new Material(Shader.Find("Sprites/Default"));
        line.sharedMaterial = material; line.startWidth = line.endWidth = .1f;
        line.startColor = line.endColor = new Color(1, .65f, .1f); line.loop = true;
        line.positionCount = RitualSite.Perimeter.Length;
        line.SetPositions(RitualSite.Perimeter.Select(p => site.Center + site.Rotation * new Vector3(p.x, .1f, p.y)).ToArray());
        try
        {
            RenderSettings.fog = false; camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0); texture.Apply();
            File.WriteAllBytes(Path.Combine(Root, "ritual-boundary-review.png"), texture.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous; RenderSettings.fog = fog;
            UnityEngine.Object.Destroy(obj); UnityEngine.Object.Destroy(boundary); UnityEngine.Object.Destroy(material);
            UnityEngine.Object.Destroy(texture); RenderTexture.ReleaseTemporary(target);
        }
    }
    [HarmonyPatch(typeof(Player), nameof(Player.Message))]
    private static class ObserveMessages
    {
        private static void Prefix(MessageHud.MessageType type, string msg)
        {
            if (started && type == MessageHud.MessageType.Center) messages.Add(msg);
        }
    }
}
