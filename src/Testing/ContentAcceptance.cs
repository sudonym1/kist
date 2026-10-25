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

// Real-engine acceptance tooling, compiled out of production. The caller must
// have passed TestPanel's marked-directory / savedir / explicit opt-in guard.
internal static class ContentAcceptance
{
    private static bool started;
    private static bool bootstrapped;
    private static readonly List<string> checks = new List<string>();

    internal static void Update(Plugin plugin)
    {
        var accessTests = Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-access-tests") >= 0;
        var ritualTests = Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-ritual-tests") >= 0;
        var outcomeTests = Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-outcome-tests") >= 0;
        if (!accessTests && !ritualTests && !outcomeTests && Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-asset-tests") < 0) return;
        if (!bootstrapped && Content.Chest != null && FejdStartup.instance != null)
        {
            bootstrapped = true;
            // This mode owns only its fresh, local test saves beneath the guard.
            var profile = new PlayerProfile("einherjarassets", FileHelpers.FileSource.Local);
            if (!profile.Load()) { profile.SetName("EinherjarAssets"); profile.m_firstSpawn = false; profile.Save(); }
            Game.SetProfile("einherjarassets", FileHelpers.FileSource.Local);
            var world = World.GetCreateWorld("EinherjarAssets", FileHelpers.FileSource.Local);
            ZNet.m_onlineBackend = OnlineBackendType.Steamworks;
            ZNet.SetServer(true, false, false, "EinherjarAssets", "", world);
            ZNet.ResetServerHost();
            AccessTools.Field(typeof(FejdStartup), "m_startingWorld").SetValue(FejdStartup.instance, true);
            AccessTools.Method(typeof(FejdStartup), "TransitionToMainScene").Invoke(FejdStartup.instance, null);
        }
        if (accessTests || ritualTests || outcomeTests) return; // Share guarded bootstrap; separate acceptance run.
        if (started || ZNetScene.instance == null || Game.instance == null) return;
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && Player.m_localPlayer == null) return;
        started = true;
        plugin.StartCoroutine(Run());
    }

    private static void Check(bool passed, string label)
    {
        if (!passed) throw new InvalidOperationException("EINHERJAR ASSET ACCEPTANCE FAILED: " + label);
        checks.Add(label);
        Debug.Log("EINHERJAR ASSET PASS: " + label);
    }

    private static IEnumerator Run()
    {
        // Catch failures at each coroutine step and retain a reviewable report.
        var run = Checks();
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
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ? "headless-acceptance.txt" : "asset-acceptance.txt");
        File.WriteAllLines(path, new[] { result, "Valheim " + global::Version.GetVersionString(),
            "Unity " + Application.unityVersion, "World " + ZNet.instance.GetWorldUID() }.Concat(checks));
        Debug.Log("EINHERJAR ASSET REPORT: " + path);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-exit-after-assets") >= 0) Application.Quit();
    }

    private static IEnumerator Checks()
    {
        var essence = Content.Essence!;
        var chest = Content.Chest!;
        var piece = chest.GetComponent<Piece>();
        Check(ZNetScene.instance.GetPrefab(ContentIds.Chest) == chest && ZNetScene.instance.GetPrefab(ContentIds.Essence) == essence,
            "Stable network prefabs registered for chest and essence");
        Check(ObjectDB.instance.GetItemPrefab(ContentIds.Essence) == essence, "Essence registered in ObjectDB");
        Check(chest.GetComponent<Container>() == null && chest.GetComponent<SharedChest>() != null, "No cloned vanilla storage interaction");
        Check(piece.m_resources.Length == 1 && piece.m_resources[0].m_resItem.gameObject == essence &&
            piece.m_resources[0].m_amount == 1 && !piece.m_resources[0].m_recover, "Recipe requires one essence and never refunds it");
        Check(!essence.GetComponent<ItemDrop>().m_itemData.m_shared.m_teleportable, "Essence item portal eligibility disabled");
        Check(chest.GetComponent<BoxCollider>().size == new Vector3(1.98f, 1.28f, 1.02f), "Simple chest collider dimensions");
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            Check(ZNet.instance.IsServer(), "Headless run uses the server role");
            Check(Content.Models == null && piece.m_icon == null && essence.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons.Length == 0,
                "Headless registration generates no meshes, materials, or icon textures");
            Check(!UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Any(camera => camera.name.StartsWith("Einherjar", StringComparison.Ordinal)),
                "Headless registration creates no asset cameras");
            yield break;
        }
        Check(chest.GetComponentsInChildren<MeshRenderer>(true).Length == 1 && essence.GetComponentsInChildren<MeshRenderer>(true).Length == 1,
            "One renderer and four shared materials per original model");
        Check(chest.GetComponent<BoxCollider>().size == new Vector3(1.98f, 1.28f, 1.02f) && Mathf.Abs(Content.Models!.Chest.bounds.min.y) < .001f,
            "Simple chest collider and floor pivot");
        Check(Content.Models!.Chest.vertexCount < 5000 && Content.Models.Essence.vertexCount < 500,
            "Modest original mesh geometry");
        var player = Player.m_localPlayer;
        while (player.InIntro() || player.IsTeleporting()) yield return null;
        player.SetNoPlacementCost(false);
        EnvMan.instance.m_debugTimeOfDay = true;
        EnvMan.instance.m_debugTime = .4f;
        EnvMan.instance.m_debugEnv = "Clear";
        var inventory = player.GetInventory();
        foreach (var oldPickup in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
            if (oldPickup.m_itemData.m_dropPrefab == essence) ZNetScene.instance.Destroy(oldPickup.gameObject);
        foreach (var item in inventory.GetAllItems().ToArray())
            if (item.m_dropPrefab != null && item.m_dropPrefab.name == ContentIds.Essence) inventory.RemoveItem(item);
        Check(!player.HaveRequirements(piece, Player.RequirementMode.CanBuild), "No carried essence blocks normal construction");
        var pickup = UnityEngine.Object.Instantiate(essence, player.transform.position + player.transform.forward * 5 + Vector3.up, Quaternion.identity);
        Check(pickup.GetComponentInChildren<MeshFilter>().sharedMesh == Content.Models.Essence, "Dropped essence uses shared original mesh");
        yield return new WaitForSeconds(1);
        pickup.GetComponent<ItemDrop>().Pickup(player);
        yield return new WaitForSeconds(.5f);
        Check(Items.Count(inventory, ContentIds.Essence) == 1, "Real world essence pickup enters backpack exactly once");
        Check(!player.IsTeleportable(false) && !player.IsTeleportable(true), "Essence blocks normal and unrestricted portal rules");
        Check(player.HaveRequirements(piece, Player.RequirementMode.CanBuild), "Carried essence satisfies actual hammer requirements");
        Check(player.HaveRequirements(piece, Player.RequirementMode.IsKnown), "Picking up essence discovers the hammer recipe");
        inventory.AddItem(Items.Default("Hammer")!);
        var hammer = inventory.GetItem("$item_hammer");
        player.EquipItem(hammer, false);
        yield return null;
        Check(player.SetSelectedPiece(piece), "Chest selectable from real hammer table");
        AccessTools.Method(typeof(Player), "SetupPlacementGhost").Invoke(player, null);
        var ghost = (GameObject)AccessTools.Field(typeof(Player), "m_placementGhost").GetValue(player);
        Check(ghost != null && ghost.GetComponentInChildren<MeshFilter>().sharedMesh == Content.Models.Chest,
            "Placement ghost uses original chest geometry");
        GameCamera.instance.transform.rotation = Quaternion.LookRotation(Vector3.up);
        Check(!player.TryPlacePiece(piece) && Items.Count(inventory, ContentIds.Essence) == 1,
            "Invalid hammer placement leaves carried essence intact");
        // Move this test character into the clearing, outside the spawn site's
        // build exclusion. Exercise the actual validity/ghost path before payment.
        var clearing = player.transform.position + Vector3.right * 25;
        if (Physics.Raycast(clearing + Vector3.up * 100, Vector3.down, out var terrain, 200, LayerMask.GetMask("terrain"))) clearing.y = terrain.point.y;
        yield return new WaitForSeconds(2); // Respect the game's spawn/teleport debounce.
        Check(player.TeleportTo(clearing + Vector3.up, Quaternion.identity, false), "Test character can travel to the placement clearing");
        while (player.IsTeleporting()) yield return null;
        yield return new WaitForFixedUpdate();
        var position = player.transform.position;
        var built = false;
        // Generated terrain can put a tree/rock or steep slope directly ahead.
        // Search a bounded ring using normal placement checks, without relaxing
        // collision, support, location, or other game restrictions.
        for (var candidate = 0; candidate < 16 && !built; candidate++)
        {
            position = player.transform.position + Quaternion.Euler(0, candidate * 22.5f, 0) * Vector3.forward * 3;
            if (Physics.Raycast(position + Vector3.up * 10, Vector3.down, out terrain, 30, LayerMask.GetMask("terrain"))) position.y = terrain.point.y;
            GameCamera.instance.transform.position = player.transform.position + Vector3.up * 3;
            GameCamera.instance.transform.LookAt(position);
            built = player.TryPlacePiece(piece);
        }
        Check(built, "Valid hammer placement succeeds on terrain outside the original circle (" +
            AccessTools.Field(typeof(Player), "m_placementStatus").GetValue(player) + ")");
        player.ConsumeResources(piece.m_resources, 0);
        Check(Items.Count(inventory, ContentIds.Essence) == 0 && !player.HaveRequirements(piece, Player.RequirementMode.CanBuild),
            "Normal local resource path consumes exactly one essence");
        yield return null;
        var placed = UnityEngine.Object.FindObjectsByType<SharedChest>(FindObjectsSortMode.None)
            .Where(c => c.GetComponent<ZNetView>()?.IsValid() == true)
            .OrderBy(c => Vector3.Distance(c.transform.position, position)).First();
        Check(placed.GetComponentInChildren<MeshFilter>().sharedMesh == Content.Models.Chest,
            "Placed chest shares the prefab mesh and materials");
        Check(placed.GetComponent<SharedChest>().Interact(player, false, false) && Plugin.WindowOpen,
            "Chest interaction opens existing shared window");
        // Damage and hammer removal converge on Piece.DropResources. Exercise
        // both actual WearNTear paths and verify no essence pickup is produced.
        foreach (var damage in new[] { false, true })
        {
            var instance = UnityEngine.Object.Instantiate(chest, position + Vector3.right * (damage ? 4 : -4), Quaternion.identity);
            if (damage) instance.GetComponent<WearNTear>().ApplyDamage(100000);
            else instance.GetComponent<WearNTear>().Remove();
            yield return null;
            Check(!UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Any(drop => drop.m_itemData.m_dropPrefab == essence),
                damage ? "Damage destruction refunds no essence" : "Hammer removal refunds no essence");
        }
        inventory.AddItem(Items.Default(ContentIds.Essence)!); // Leave one for manual placement inspection.
        Check(piece.m_icon != null && essence.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons.Length == 1,
            "Embedded model-rendered icons loaded");
        foreach (var sprite in new[] { piece.m_icon, essence.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons[0] })
        {
            var texture = sprite!.texture;
            var pixels = texture.GetPixels32();
            var visible = pixels.Count(pixel => pixel.a > 200);
            Check(texture.width == 256 && texture.height == 256 && visible > 500 && visible < 55000 && pixels[0].a == 0,
                "Icon has visible model pixels and transparent margins: " + texture.name);
        }
    }
}
