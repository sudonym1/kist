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

// Called only after TestPanel's explicit opt-in and isolated-save guard.
internal static class AccessAcceptance
{
    private static bool started;
    private static readonly List<string> checks = new List<string>();
    internal static void Update(Plugin plugin)
    {
        if (started || Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-access-tests") < 0 ||
            ZNetScene.instance == null || Game.instance == null) return;
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && Player.m_localPlayer == null) return;
        started = true;
        plugin.StartCoroutine(Run(plugin));
    }
    private static void Check(bool passed, string label)
    {
        if (!passed) throw new InvalidOperationException("EINHERJAR ACCESS ACCEPTANCE FAILED: " + label);
        checks.Add(label); Debug.Log("EINHERJAR ACCESS PASS: " + label);
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
        var path = Path.Combine(Directory.GetParent(Paths.GameRootPath)!.FullName,
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ? "access-headless-acceptance.txt" : "access-acceptance.txt");
        File.WriteAllLines(path, new[] { result, "Valheim " + global::Version.GetVersionString(),
            "Unity " + Application.unityVersion, "World " + ZNet.instance.GetWorldUID() }.Concat(checks));
        Debug.Log("EINHERJAR ACCESS REPORT: " + path);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-exit-after-access") >= 0) Application.Quit();
    }
    private static IEnumerator Checks(Plugin plugin)
    {
        var graphical = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
        var player = Player.m_localPlayer;
        if (graphical)
            while (player.InIntro() || player.IsTeleporting()) yield return null;
        var anchor = new Vector3(5500, 500, 5500);
        Check(!ChestAccess.Covers(anchor), "Unloaded world locations provide no access");
        var instance = UnityEngine.Object.Instantiate(Content.Chest!, anchor, Quaternion.identity);
        var record = instance.GetComponent<ZNetView>().GetZDO();
        var id = record.m_uid;
        Check(ChestAccess.Covers(anchor), "Loaded prefab immediately provides local access");
        Check(ChestAccess.Covers(anchor + Vector3.up * 100) && !ChestAccess.Covers(anchor + Vector3.up * 100.01f),
            "Fixed 100-metre 3D boundary is inclusive");
        yield return new WaitForSeconds(1);
        Check(instance == null && ZNetScene.instance.FindInstance(record) == null && ZDOMan.instance.GetZDO(id) != null,
            "Natural scene unloading retains the chest world record");
        Check(!ChestAccess.Covers(anchor), "Unloaded chest loses access even though its ZDO remains");
        var moved = anchor + Vector3.right * 300;
        record.SetPosition(moved);
        Check(!ChestAccess.Covers(moved), "Moving an unloaded ZDO cannot grant local access");
        var reloaded = (GameObject)AccessTools.Method(typeof(ZNetScene), "CreateObject").Invoke(ZNetScene.instance, new object[] { record });
        Check(ChestAccess.Covers(moved), "Reloading the actual chest restores access immediately");
        reloaded.SetActive(false);
        Check(!ChestAccess.Covers(moved), "Disabling a chest immediately removes local coverage");
        reloaded.SetActive(true);
        Check(ChestAccess.Covers(moved), "Re-enabling a valid chest restores local coverage");
        if (graphical)
        {
            var client = (Client)AccessTools.Field(typeof(Plugin), "client").GetValue(plugin);
            var savedPosition = player.transform.position;
            var old = client.Counts.TryGetValue("Wood", out var count) ? count : 0;
            client.Counts["Wood"] = 10;
            try
            {
                var carried = player.GetInventory().CountItems("$item_wood");
                player.transform.position = moved;
                Check(client.CanAccessSharedInventory && Plugin.Resources!.Count(player.GetInventory(), "$item_wood", -1, true) == carried + 10,
                    "Live resource path includes shared stock near a loaded chest");
                reloaded.SetActive(false);
                Check(!client.CanAccessSharedInventory && Plugin.Resources!.Count(player.GetInventory(), "$item_wood", -1, true) == carried,
                    "Unloading blocks shared stock while retaining carried resources");
                reloaded.SetActive(true);
                Check(client.CanAccessSharedInventory, "Local player access resumes immediately on reload");
            }
            finally
            {
                player.transform.position = savedPosition;
                if (old > 0) client.Counts["Wood"] = old; else client.Counts.Remove("Wood");
            }
        }
        ZNetScene.instance.Destroy(reloaded);
        Check(!ChestAccess.Covers(moved), "Destroyed or detached scene instances grant no access");
        yield return new WaitForSeconds(1);
        Check(ZDOMan.instance.GetZDO(id) == null, "Normal chest destruction removes its world record");
        if (!graphical)
        {
            Check(Content.Models == null && !UnityEngine.Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None)
                .Any(line => line.transform.parent?.name == "Einherjar access sphere"), "Headless execution creates no preview render resources");
            yield break;
        }
        var origin = player.transform.position;
        var inventory = player.GetInventory();
        EnvMan.instance.m_debugTimeOfDay = true; EnvMan.instance.m_debugTime = .4f; EnvMan.instance.m_debugEnv = "Clear";
        if (inventory.GetItem("$item_hammer") == null) inventory.AddItem(Items.Default("Hammer")!);
        player.EquipItem(inventory.GetItem("$item_hammer"), false);
        Check(player.SetSelectedPiece(Content.Chest!.GetComponent<Piece>()), "Real hammer selects chest for radius preview");
        AccessTools.Method(typeof(Player), "SetupPlacementGhost").Invoke(player, null);
        var ghost = (GameObject)AccessTools.Field(typeof(Player), "m_placementGhost").GetValue(player);
        var visual = (ChestRadiusVisual)AccessTools.Field(typeof(Plugin), "radiusVisual").GetValue(plugin);
        var savedGhostPosition = ghost.transform.position;
        ghost.transform.position = anchor; ghost.SetActive(true);
        Check(!ChestAccess.Covers(anchor), "Actual hammer ghost never provides shared access");
        ghost.transform.position = savedGhostPosition;

        // Drive one real placement update before the camera controller advances
        // again. Saved look direction must not make this test miss the ground.
        for (var candidate = 0; candidate < 16 && !visual.Visible; candidate++)
        {
            var target = origin + Quaternion.Euler(0, candidate * 22.5f, 0) * Vector3.forward * 3;
            if (Physics.Raycast(target + Vector3.up * 10, Vector3.down, out var terrain, 30, LayerMask.GetMask("terrain"))) target.y = terrain.point.y;
            GameCamera.instance.transform.position = origin + Vector3.up * 3;
            GameCamera.instance.transform.LookAt(target);
            AccessTools.Method(typeof(Player), "UpdatePlacementGhost").Invoke(player, new object[] { false });
            visual.Update();
        }
        Check(visual.Visible && UnityEngine.Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None)
            .Count(line => line.transform.parent?.name == "Einherjar access sphere" && line.positionCount == 96) == 3,
            "One three-circle spherical radius preview appears for the actual placement ghost (active=" + ghost.activeInHierarchy +
            ", shader=" + (Shader.Find("Sprites/Default") != null) + ")");
        Check(Content.Chest.GetComponent<SharedChest>().GetHoverText().Contains("100"), "Chest inspection displays fixed loaded-only radius");
        CapturePreview(ghost.transform.position);
        // Leave the hammer ghost available for visual review; no new paid chest.
    }

    private static void CapturePreview(Vector3 center)
    {
        var cameraObject = new GameObject("Einherjar radius review camera");
        var target = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 4);
        var texture = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
        var previous = RenderTexture.active;
        var fog = RenderSettings.fog;
        try
        {
            var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.05f, .06f, .07f);
            camera.orthographic = true; camera.orthographicSize = 125;
            camera.nearClipPlane = .1f; camera.farClipPlane = 1000;
            camera.transform.position = center + new Vector3(170, 150, -170); camera.transform.LookAt(center);
            camera.targetTexture = target;
            RenderSettings.fog = false;
            camera.Render(); RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); texture.Apply();
            File.WriteAllBytes(Path.Combine(Directory.GetParent(Paths.GameRootPath)!.FullName, "access-radius-preview.png"), ImageConversion.EncodeToPNG(texture));
        }
        finally
        {
            RenderSettings.fog = fog; RenderTexture.active = previous;
            UnityEngine.Object.Destroy(cameraObject); UnityEngine.Object.Destroy(texture); RenderTexture.ReleaseTemporary(target);
        }
    }
}
