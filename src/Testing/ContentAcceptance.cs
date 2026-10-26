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
            // Asset acceptance starts with fresh equipment and position rather
            // than inheriting a prior failed run's saved swimming/weapon state.
            var profileId = accessTests || ritualTests || outcomeTests ? "einherjarassets" : "einherjarassets-" + DateTime.UtcNow.Ticks;
            var profile = new PlayerProfile(profileId, FileHelpers.FileSource.Local);
            if (!profile.Load()) { profile.SetName("EinherjarAssets"); profile.m_firstSpawn = false; profile.Save(); }
            Game.SetProfile(profileId, FileHelpers.FileSource.Local);
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
        Check(ObjectDB.instance.GetItemPrefab(ContentIds.DimmedEssence) == Content.DimmedEssence &&
            ZNetScene.instance.GetPrefab(ContentIds.DimmedEssence) == Content.DimmedEssence &&
            Content.DimmedEssence!.GetComponent<ItemDrop>().m_itemData.m_shared.m_maxStackSize == 1,
            "Dimmed essence registered in item and network tables and cannot stack");
        Check(chest.GetComponent<Container>() == null && chest.GetComponent<SharedChest>() != null, "No cloned vanilla storage interaction");
        Check(piece.m_resources.Length == 1 && piece.m_resources[0].m_resItem.gameObject == essence &&
            piece.m_resources[0].m_amount == 1 && !piece.m_resources[0].m_recover, "Recipe disables vanilla refunds in favor of bound hammer recovery");
        Check(essence.GetComponent<ItemDrop>().m_itemData.m_shared.m_teleportable == Content.EssencePortalsAllowed,
            "Essence item portal eligibility follows the host setting");
        Check(chest.GetComponent<BoxCollider>().size == new Vector3(1.98f, 1.28f, 1.02f), "Simple chest collider dimensions");
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            Check(ZNet.instance.IsServer(), "Headless run uses the server role");
            Check(Content.Models == null && piece.m_icon == null && essence.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons.Length == 0,
                "Headless registration generates no meshes, materials, or icon textures");
            Check(!UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Any(camera => camera.name.StartsWith("Einherjar", StringComparison.Ordinal)),
                "Headless registration creates no asset cameras");
            var headlessOrigin = new Vector3(6500, 500, 6500);
            var instance = UnityEngine.Object.Instantiate(chest, headlessOrigin, Quaternion.identity);
            var record = instance.GetComponent<ZNetView>().GetZDO();
            var bindingData = new EssenceBinding(ZNet.instance.GetWorldUID(), headlessOrigin).Encode();
            record.Set(EssenceBinding.Key, bindingData);
            record.Set("local.sharedinventory.essencePaid", true);
            yield return new WaitForSeconds(1);
            Check(instance == null && ZDOMan.instance.GetZDO(record.m_uid) != null,
                "Bound chest naturally unloads while retaining its world record");
            var reloaded = (GameObject)AccessTools.Method(typeof(ZNetScene), "CreateObject").Invoke(ZNetScene.instance, new object[] { record });
            Check(reloaded.GetComponent<ZNetView>().GetZDO().GetString(EssenceBinding.Key) == bindingData,
                "Chest binding survives actual scene unload and reload");
            reloaded.GetComponent<ZNetView>().ClaimOwnership();
            reloaded.GetComponent<WearNTear>().Remove();
            Check(UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Any(drop =>
                drop.m_itemData.m_dropPrefab == Content.DimmedEssence && drop.m_itemData.m_customData.TryGetValue(EssenceBinding.Key, out var value) && value == bindingData),
                "Headless owner can publish the bound hammer refund without render assets");
            var damaged = UnityEngine.Object.Instantiate(chest, headlessOrigin + Vector3.right * 4, Quaternion.identity);
            var damageRecord = damaged.GetComponent<ZNetView>().GetZDO();
            damageRecord.Set(EssenceBinding.Key, bindingData);
            damageRecord.Set("local.sharedinventory.essencePaid", true);
            var damageWear = damaged.GetComponent<WearNTear>();
            damageWear.m_onDestroyed += () => damaged.GetComponent<Piece>().DropResources();
            damageWear.ApplyDamage(100000);
            Check(UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Count(drop =>
                drop.m_itemData.m_dropPrefab == Content.DimmedEssence && drop.m_itemData.m_customData.TryGetValue(EssenceBinding.Key, out var value) && value == bindingData) == 2,
                "Headless lethal damage preserves the binding and repeated resource dropping cannot duplicate its refund");
            var blocked = UnityEngine.Object.Instantiate(chest, headlessOrigin + Vector3.right * 8, Quaternion.identity);
            blocked.GetComponent<WearNTear>().Remove(true);
            Check(UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Count(drop =>
                drop.m_itemData.m_dropPrefab == Content.DimmedEssence) == 2,
                "Explicitly blocked destruction drops still create no essence");
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
        foreach (var oldChest in UnityEngine.Object.FindObjectsByType<SharedChest>(FindObjectsSortMode.None))
            if (oldChest.GetComponent<ZNetView>()?.IsValid() == true) ZNetScene.instance.Destroy(oldChest.gameObject);
        foreach (var oldPickup in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
            if (oldPickup.m_itemData.m_dropPrefab == essence || oldPickup.m_itemData.m_dropPrefab == Content.DimmedEssence) ZNetScene.instance.Destroy(oldPickup.gameObject);
        foreach (var item in inventory.GetAllItems().ToArray())
            if (item.m_dropPrefab != null && (!ContentIds.CanShare(item.m_dropPrefab.name) || item.m_dropPrefab.name == "Hammer"))
            { player.UnequipItem(item, false); inventory.RemoveItem(item); }
        Check(!player.HaveRequirements(piece, Player.RequirementMode.CanBuild), "No carried essence blocks normal construction");
        var pickup = UnityEngine.Object.Instantiate(essence, player.transform.position + player.transform.forward * 5 + Vector3.up, Quaternion.identity);
        Check(pickup.GetComponentInChildren<MeshFilter>().sharedMesh == Content.Models.Essence, "Dropped essence uses shared original mesh");
        yield return new WaitForSeconds(1);
        pickup.GetComponent<ItemDrop>().Pickup(player);
        yield return new WaitForSeconds(.5f);
        Check(Items.Count(inventory, ContentIds.Essence) == 1, "Real world essence pickup enters backpack exactly once");
        var portalSetting = Plugin.Instance!.Config.Bind("Server", "AllowEssenceThroughPortals", false, "");
        var originalPortalSetting = portalSetting.Value;
        try
        {
            var portalPrefix = AccessTools.Method(AccessTools.Inner(typeof(Content), "ExplainBlockedPortal"), "Prefix");
            portalSetting.Value = false; yield return null;
            Check(!player.IsTeleportable(false) && !player.IsTeleportable(true) && !(bool)portalPrefix.Invoke(null, new object[] { player }),
                "Disabled setting blocks essence under normal and unrestricted portal rules and prevents portal activation");
            Check(!essence.GetComponent<ItemDrop>().m_itemData.m_shared.m_teleportable, "Disabling portal travel updates essence item data");
            var carriedEssence = inventory.GetAllItems().Single(item => item.m_dropPrefab == essence);
            inventory.RemoveItem(carriedEssence);
            var normalEligibility = player.IsTeleportable(false);
            var unrestrictedEligibility = player.IsTeleportable(true);
            inventory.AddItem(carriedEssence);
            portalSetting.Value = true; yield return null;
            Check(essence.GetComponent<ItemDrop>().m_itemData.m_shared.m_teleportable &&
                player.IsTeleportable(false) == normalEligibility && player.IsTeleportable(true) == unrestrictedEligibility &&
                (bool)portalPrefix.Invoke(null, new object[] { player }),
                "Enabled setting permits already carried essence without bypassing other portal restrictions");
            var copper = Items.Default("CopperOre")!;
            Check(Items.Add(inventory, copper, 1) == 1, "Additional portal-restricted item fits in the test inventory");
            Check(player.IsTeleportable(false) == inventory.IsTeleportable(false), "Enabled essence setting preserves the game's rules for other carried items");
            inventory.RemoveItem(copper.m_shared.m_name, 1);
        }
        finally { portalSetting.Value = originalPortalSetting; }
        yield return null;
        Check(player.HaveRequirements(piece, Player.RequirementMode.CanBuild), "Carried essence satisfies actual hammer requirements");
        Check(player.HaveRequirements(piece, Player.RequirementMode.IsKnown), "Picking up essence discovers the hammer recipe");
        var hammer = Items.Default("Hammer")!;
        Check(inventory.AddItem(hammer) && player.EquipItem(hammer, false), "Fresh test hammer equips and enters building mode");
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
        var clearings = new List<Vector3>();
        RaycastHit terrain;
        for (var site = 0; site < 64; site++)
        {
            var candidate = player.transform.position + Quaternion.Euler(0, site * 22.5f, 0) * Vector3.forward * (30 + site / 16 * 20);
            if (!Physics.Raycast(candidate + Vector3.up * 200, Vector3.down, out terrain, 400, LayerMask.GetMask("terrain")) ||
                terrain.point.y < ZoneSystem.instance.m_waterLevel + 1 || terrain.normal.y < .92f) continue;
            clearings.Add(terrain.point);
        }
        Check(clearings.Count > 0, "Generated test terrain contains a dry building clearing");
        var clearing = clearings[0];
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
            GameCamera.instance.transform.position = position + Vector3.up * 8;
            GameCamera.instance.transform.LookAt(position);
            built = player.TryPlacePiece(piece);
        }
        // A fresh generated seed can put the first clearing in dense scenery or
        // water. Try a bounded set of other sites, retaining every normal check.
        for (var clearingIndex = 1; clearingIndex < Math.Min(clearings.Count, 5) && !built; clearingIndex++)
        {
            var next = clearings[clearingIndex];
            yield return new WaitForSeconds(2);
            if (!player.TeleportTo(next + Vector3.up, Quaternion.identity, false)) continue;
            while (player.IsTeleporting()) yield return null;
            yield return new WaitForSeconds(.5f);
            for (var candidate = 0; candidate < 32 && !built; candidate++)
            {
                position = player.transform.position + Quaternion.Euler(0, candidate * 22.5f, 0) * Vector3.forward * (candidate < 16 ? 3 : 4);
                if (Physics.Raycast(position + Vector3.up * 100, Vector3.down, out terrain, 200, LayerMask.GetMask("terrain"))) position.y = terrain.point.y;
                GameCamera.instance.transform.position = position + Vector3.up * 8;
                GameCamera.instance.transform.LookAt(position);
                built = player.TryPlacePiece(piece);
            }
            Debug.Log("EINHERJAR placement site " + next + " result=" + player.GetPlacementStatus() +
                " carried=" + Items.Count(inventory, ContentIds.Essence));
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
        var originalSite = placed.transform.position;
        var encoded = placed.GetComponent<ZNetView>().GetZDO().GetString(EssenceBinding.Key);
        Check(EssenceBinding.TryRead(encoded, out var initialBinding) && initialBinding.Origin == originalSite,
            "Real hammer placement records the exact original site");
        placed.GetComponent<WearNTear>().Remove();
        yield return null;
        var refund = UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
            .Single(drop => drop.m_itemData.m_dropPrefab == Content.DimmedEssence);
        Check(refund.m_itemData.m_customData[EssenceBinding.Key] == encoded,
            "Hammer removal returns exactly one dimmed essence with the original binding");
        Check(refund.GetComponentInChildren<MeshRenderer>().sharedMaterials[3] == Content.Models.DimmedMaterials[3] &&
            Content.Models.DimmedMaterials[3] != Content.Models.Materials[3], "Dimmed world model uses its separate purple material");
        refund.Pickup(player);
        yield return new WaitForSeconds(.5f);
        var dimmed = inventory.GetAllItems().Single(item => item.m_dropPrefab == Content.DimmedEssence);
        Check(dimmed.m_customData[EssenceBinding.Key] == encoded && dimmed.GetTooltip().Contains("Bound site: X:") &&
            !dimmed.GetTooltip().Contains("Used before"), "Pickup preserves binding and tooltip shows coordinates without priority rules");
        Check(player.HaveRequirements(piece, Player.RequirementMode.CanBuild), "Valid dimmed-only inventory satisfies real hammer requirements");
        AccessTools.Method(typeof(Hud), "SetupPieceInfo").Invoke(Hud.instance, new object[] { piece });
        Check(Hud.instance.m_requirementItems[0].GetComponent<UITooltip>().m_text.Contains("Bound site: X:"),
            "Hammer ingredient slot displays the selected dimmed item and bound tooltip");
        var transfer = new Inventory("Dimmed trade", null, 8, 4);
        transfer.AddItem(dimmed.Clone()); inventory.RemoveItem(dimmed);
        var savedTrade = new ZPackage(); transfer.Save(savedTrade);
        var recipient = new Inventory("Dimmed recipient", null, 8, 4);
        recipient.Load(new ZPackage(savedTrade.GetArray()));
        var received = recipient.GetAllItems().Single();
        Check(received.m_customData[EssenceBinding.Key] == encoded, "Real inventory save/load and trade retain item-local binding");
        recipient.RemoveItem(received); inventory.AddItem(received);
        var originalPortalPolicy = portalSetting.Value;
        try
        {
            portalSetting.Value = false; yield return null;
            Check(!player.IsTeleportable(true), "Dimmed essence blocks even unrestricted portals when disabled");
            portalSetting.Value = true; yield return null;
            Check(Content.DimmedEssence!.GetComponent<ItemDrop>().m_itemData.m_shared.m_teleportable && player.IsTeleportable(true),
                "Server portal allowance also applies to already carried dimmed essence");
        }
        finally { portalSetting.Value = originalPortalPolicy; }
        var outside = originalSite + Vector3.right * (ChestAccess.Radius + 1);
        Check(!ChestConstruction.Place(player, piece, outside, Quaternion.identity, false, false) &&
            Items.Count(inventory, ContentIds.DimmedEssence) == 1, "Out-of-area construction refuses dimmed-only payment without consuming it");
        inventory.AddItem(Items.Default(ContentIds.Essence)!);
        Check(ChestConstruction.Place(player, piece, originalSite + Vector3.right * 10, Quaternion.identity, false, false) &&
            Items.Count(inventory, ContentIds.DimmedEssence) == 0 && Items.Count(inventory, ContentIds.Essence) == 1,
            "Valid dimmed essence is consumed before regular essence");
        player.ConsumeResources(piece.m_resources, 0);
        Check(Items.Count(inventory, ContentIds.Essence) == 1, "Normal follow-up resource payment cannot double-charge regular essence");
        var rebuilt = UnityEngine.Object.FindObjectsByType<SharedChest>(FindObjectsSortMode.None)
            .Single(c => c.GetComponent<ZNetView>()?.IsValid() == true && c.GetComponent<ZNetView>().GetZDO().GetString(EssenceBinding.Key) == encoded);
        Check(rebuilt.GetComponent<ZNetView>().GetZDO().GetString(EssenceBinding.Key) == encoded,
            "Rebuilt chest retains immutable original site rather than its new location");
        rebuilt.GetComponent<WearNTear>().ApplyDamage(1);
        rebuilt.GetComponent<Piece>().DropResources();
        Check(!UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Any(drop => drop.m_itemData.m_dropPrefab == Content.DimmedEssence),
            "Nonlethal damage and standalone resource drops cannot refund a living chest");
        rebuilt.GetComponent<WearNTear>().ApplyDamage(100000); yield return null;
        refund = UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
            .Single(drop => drop.m_itemData.m_dropPrefab == Content.DimmedEssence);
        Check(refund.m_itemData.m_customData[EssenceBinding.Key] == encoded, "Damage destruction of a rebuilt chest returns exactly one essence preserving its original binding");
        refund.Pickup(player);
        var pickupDeadline = Time.realtimeSinceStartup + 3;
        while (Items.Count(inventory, ContentIds.DimmedEssence) == 0 && Time.realtimeSinceStartup < pickupDeadline) yield return null;
        Check(Items.Count(inventory, ContentIds.DimmedEssence) == 1, "Repeated refund pickup completes after the normal spawn delay");
        Check(ChestConstruction.Place(player, piece, outside, Quaternion.identity, false, false) &&
            Items.Count(inventory, ContentIds.Essence) == 0 && Items.Count(inventory, ContentIds.DimmedEssence) == 1,
            "Outside the bound area regular essence is consumed while invalid dimmed essence remains");
        var fresh = UnityEngine.Object.FindObjectsByType<SharedChest>(FindObjectsSortMode.None)
            .Single(c => c.GetComponent<ZNetView>()?.IsValid() == true && c.transform.position == outside);
        Check(EssenceBinding.TryRead(fresh.GetComponent<ZNetView>().GetZDO().GetString(EssenceBinding.Key), out var freshBinding) &&
            freshBinding.Origin == outside, "Regular essence establishes a new origin at the new site");
        fresh.GetComponent<WearNTear>().ApplyDamage(100000); yield return null;
        var damageRefunds = UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
            .Where(drop => drop.m_itemData.m_dropPrefab == Content.DimmedEssence).ToArray();
        Check(damageRefunds.Length == 1 && damageRefunds[0].m_itemData.m_customData[EssenceBinding.Key] == freshBinding.Encode(),
            "Damage destruction of a new chest returns exactly one essence bound to its site");
        foreach (var drop in damageRefunds) ZNetScene.instance.Destroy(drop.gameObject);
        yield return null;
        player.SetNoPlacementCost(true);
        Check(ChestConstruction.Place(player, piece, outside, Quaternion.identity, false, false), "Admin construction remains available");
        player.SetNoPlacementCost(false);
        var admin = UnityEngine.Object.FindObjectsByType<SharedChest>(FindObjectsSortMode.None)
            .Single(c => c.GetComponent<ZNetView>()?.IsValid() == true && c.transform.position == outside);
        admin.GetComponent<WearNTear>().Remove(); yield return null;
        Check(!UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Any(drop => drop.m_itemData.m_dropPrefab == Content.DimmedEssence),
            "Free construction cannot mint refundable dimmed essence");
        // Old chests have no binding keys. Both destruction paths bind their
        // returned Essence to the existing saved location.
        foreach (var damage in new[] { false, true })
        {
            var instance = UnityEngine.Object.Instantiate(chest, position + Vector3.right * (damage ? 4 : -4), Quaternion.identity);
            if (damage) instance.GetComponent<WearNTear>().ApplyDamage(100000);
            else instance.GetComponent<WearNTear>().Remove();
            yield return null;
            var drops = UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
                .Where(drop => drop.m_itemData.m_dropPrefab == Content.DimmedEssence).ToArray();
            Check(drops.Length == 1 && EssenceBinding.TryRead(drops[0].m_itemData, out var legacy) &&
                legacy.Origin == position + Vector3.right * (damage ? 4 : -4), damage ? "Legacy damage destruction creates a site-bound dimmed essence" : "Legacy hammer removal creates a site-bound dimmed essence");
            foreach (var drop in drops) ZNetScene.instance.Destroy(drop.gameObject);
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
