using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace SharedInventory;

// Direct registration deliberately avoids an additional mod dependency or an
// editor-version-dependent bundle. Prefabs remain inactive in this private root;
// only placed/dropped instances run Valheim lifecycle and network code.
internal static class Content
{
    private static GameObject? root;
    internal static GameObject? Essence { get; private set; }
    internal static GameObject? DimmedEssence { get; private set; }
    internal static GameObject? Chest { get; private set; }
    internal static ContentModels? Models { get; private set; }
    internal static bool EssencePortalsAllowed { get; private set; }

    internal static void ApplyPortalSetting(bool allowed)
    {
        EssencePortalsAllowed = allowed;
        if (Essence != null) Essence.GetComponent<ItemDrop>().m_itemData.m_shared.m_teleportable = allowed;
        if (DimmedEssence != null) DimmedEssence.GetComponent<ItemDrop>().m_itemData.m_shared.m_teleportable = allowed;
        if (Player.m_localPlayer != null) SyncPortalEligibility(Player.m_localPlayer.GetInventory());
        if (Localization.instance != null) Localize.Postfix(Localization.instance);
    }
    private static readonly List<UnityEngine.Object> icons = new List<UnityEngine.Object>();
    private static bool Headless => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

    private static Transform Root
    {
        get
        {
            if (root == null)
            {
                root = new GameObject("Einherjar prefab library");
                root.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(root);
            }
            return root.transform;
        }
    }

    internal static void Register(ObjectDB db)
    {
        Localize.Postfix(Localization.instance);
        var amber = db.m_items.Find(item => item.name == "Amber");
        var hammer = db.m_items.Find(item => item.name == "Hammer")?.GetComponent<ItemDrop>();
        var pieces = hammer?.m_itemData.m_shared.m_buildPieces;
        var vanillaChest = pieces?.m_pieces.Find(piece => piece.name == "piece_chest_wood");
        if (amber == null || vanillaChest == null || pieces == null) return;
        if (Models == null && !Headless)
        {
            var material = vanillaChest.GetComponentsInChildren<MeshRenderer>(true)
                .SelectMany(renderer => renderer.sharedMaterials)
                .First(candidate => candidate.shader.name == "Custom/Piece");
            Models = new ContentModels(material);
        }
        if (Essence == null)
        {
            Essence = UnityEngine.Object.Instantiate(amber, Root);
            Essence.name = ContentIds.Essence;
            ReplaceChildren(Essence);
            foreach (var collider in Essence.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(collider);
            var colliderNew = Essence.AddComponent<BoxCollider>();
            colliderNew.center = new Vector3(0, .23f, 0);
            colliderNew.size = new Vector3(.24f, .46f, .2f);
            if (Models != null) Models.Attach(Essence.transform, Models.Essence);
            var drop = Essence.GetComponent<ItemDrop>();
            drop.m_itemData = new ItemDrop.ItemData
            {
                m_dropPrefab = Essence,
                m_stack = 1,
                m_shared = new ItemDrop.ItemData.SharedData
                {
                    m_name = "$item_einherjaressence",
                    m_description = "$item_einherjaressence_description",
                    m_itemType = ItemDrop.ItemData.ItemType.Material,
                    m_weight = 1f,
                    m_maxStackSize = 20,
                    m_maxQuality = 1,
                    m_teleportable = EssencePortalsAllowed,
                    m_icons = LoadIcon("essence") is Sprite essenceIcon ? new[] { essenceIcon } : Array.Empty<Sprite>()
                }
            };
            drop.m_autoDestroy = false;
        }
        if (!db.m_items.Contains(Essence)) db.m_items.Add(Essence);
        if (DimmedEssence == null)
        {
            DimmedEssence = UnityEngine.Object.Instantiate(Essence, Root);
            DimmedEssence.name = ContentIds.DimmedEssence;
            if (Models != null) DimmedEssence.GetComponentInChildren<MeshRenderer>(true).sharedMaterials = Models.DimmedMaterials;
            var drop = DimmedEssence.GetComponent<ItemDrop>();
            drop.m_itemData = Essence.GetComponent<ItemDrop>().m_itemData.Clone();
            drop.m_itemData.m_dropPrefab = DimmedEssence;
            drop.m_itemData.m_shared = new ItemDrop.ItemData.SharedData
            {
                m_name = "$item_dimmedeinherjaressence",
                m_description = "$item_dimmedeinherjaressence_description",
                m_itemType = ItemDrop.ItemData.ItemType.Material,
                m_weight = 1f, m_maxStackSize = 1, m_maxQuality = 1,
                m_teleportable = EssencePortalsAllowed,
                m_icons = LoadIcon("essence", true) is Sprite dimmedIcon ? new[] { dimmedIcon } : Array.Empty<Sprite>()
            };
            drop.m_autoDestroy = false;
        }
        if (!db.m_items.Contains(DimmedEssence)) db.m_items.Add(DimmedEssence);
        if (Chest == null)
        {
            Chest = UnityEngine.Object.Instantiate(vanillaChest, Root);
            Chest.name = ContentIds.Chest;
            // Remove storage and all inherited meshes, colliders, lid animation,
            // loot/snap children. Only the building/network/damage shell remains.
            foreach (var container in Chest.GetComponents<Container>()) UnityEngine.Object.DestroyImmediate(container);
            ReplaceChildren(Chest);
            foreach (var collider in Chest.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var renderer in Chest.GetComponents<Renderer>()) UnityEngine.Object.DestroyImmediate(renderer);
            var model = Models != null ? Models.Attach(Chest.transform, Models.Chest) : new GameObject("model");
            if (Models == null) model.transform.SetParent(Chest.transform, false);
            model.layer = LayerMask.NameToLayer("piece");
            var colliderNew = Chest.AddComponent<BoxCollider>();
            colliderNew.center = new Vector3(0, .64f, 0);
            colliderNew.size = new Vector3(1.98f, 1.28f, 1.02f);
            var piece = Chest.GetComponent<Piece>();
            piece.m_name = "$piece_einherjarchest";
            piece.m_description = "$piece_einherjarchest_description";
            piece.m_icon = LoadIcon("chest");
            piece.m_craftingStation = null;
            piece.m_category = Piece.PieceCategory.Furniture;
            piece.m_destroyedLootPrefab = null;
            piece.m_resources = new[] { new Piece.Requirement
            {
                m_resItem = Essence.GetComponent<ItemDrop>(), m_amount = 1, m_amountPerLevel = 0, m_recover = false
            } };
            var wear = Chest.GetComponent<WearNTear>();
            wear.m_new = wear.m_worn = wear.m_broken = model;
            wear.m_wet = null;
            wear.m_snow = wear.m_snowWorn = wear.m_snowBroken = null;
            wear.m_fragmentRoots = new[] { model };
            wear.m_autoCreateFragments = false;
            wear.m_nonSolidRenderers = new List<Renderer>();
            Chest.AddComponent<SharedChest>();
        }
        if (!pieces.m_pieces.Contains(Chest)) pieces.m_pieces.Add(Chest);
    }

    private static void ReplaceChildren(GameObject prefab)
    {
        for (var i = prefab.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.DestroyImmediate(prefab.transform.GetChild(i).gameObject);
    }

    private static Sprite? LoadIcon(string name, bool dimmed = false)
    {
        if (Headless) return null;
        using var stream = typeof(Content).Assembly.GetManifestResourceStream("SharedInventory.assets." + name + ".png");
        if (stream == null) throw new InvalidDataException("Missing embedded Einherjar icon: " + name);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Einherjar " + name, filterMode = FilterMode.Bilinear };
        if (!ImageConversion.LoadImage(texture, bytes.ToArray())) throw new InvalidDataException("Invalid Einherjar icon: " + name);
        if (dimmed)
        {
            // Reuse the original rendered geometry and alpha, with a purple
            // palette. Neutral rune/highlight pixels retain their appearance.
            texture.name = "Einherjar dimmed essence";
            var pixels = texture.GetPixels();
            for (var i = 0; i < pixels.Length; i++)
            {
                Color.RGBToHSV(pixels[i], out _, out var saturation, out var value);
                if (saturation < .15f) continue;
                var purple = Color.HSVToRGB(.76f, saturation, value * .85f);
                purple.a = pixels[i].a;
                pixels[i] = purple;
            }
            texture.SetPixels(pixels); texture.Apply();
        }
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100);
        icons.Add(texture); icons.Add(sprite);
        return sprite;
    }

    internal static void Dispose()
    {
        if (root != null) UnityEngine.Object.Destroy(root);
        Models?.Dispose();
        foreach (var icon in icons) UnityEngine.Object.Destroy(icon);
        icons.Clear(); Models = null; Chest = null; Essence = null; DimmedEssence = null; root = null;
    }

    [HarmonyPatch(typeof(ObjectDB), "UpdateRegisters")]
    private static class RegisterItems
    {
        private static void Prefix(ObjectDB __instance) => Register(__instance);
    }

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    private static class RegisterNetworkPrefabs
    {
        private static void Prefix(ZNetScene __instance)
        {
            // ObjectDB and its Hammer table are also available on dedicated servers.
            if (ObjectDB.instance != null) Register(ObjectDB.instance);
            foreach (var prefab in new[] { Essence, DimmedEssence, Chest })
            {
                if (prefab == null) throw new InvalidOperationException("Einherjar content was not registered before world loading.");
                var collision = __instance.m_prefabs.Find(p => p.name.GetStableHashCode() == prefab.name.GetStableHashCode());
                if (collision != null && collision != prefab) throw new InvalidOperationException("Einherjar prefab ID collision: " + prefab.name);
                if (collision == null) __instance.m_prefabs.Add(prefab);
            }
        }
    }

    [HarmonyPatch(typeof(Localization), "SetupLanguage")]
    private static class Localize
    {
        private static readonly Action<Localization, string, string> AddWord = AccessTools.MethodDelegate<Action<Localization, string, string>>(AccessTools.Method(typeof(Localization), "AddWord"));
        internal static void Postfix(Localization __instance)
        {
            AddWord(__instance, "item_einherjaressence", "Einherjar Essence");
            AddWord(__instance, "item_einherjaressence_description", "An amber soul bound by Oden. Builds an Einherjar Chest. Must be carried; cannot enter shared storage." +
                (EssencePortalsAllowed ? "" : " Cannot pass through portals.") + " Deconstructing its chest returns a Dimmed Essence bound to that site.");
            AddWord(__instance, "item_dimmedeinherjaressence", "Dimmed Einherjar Essence");
            AddWord(__instance, "item_dimmedeinherjaressence_description", "A fading purple soul bound to its original chest site. Builds an Einherjar Chest only near that site. Must be carried; cannot enter shared storage." +
                (EssencePortalsAllowed ? "" : " Cannot pass through portals."));
            AddWord(__instance, "piece_einherjarchest", "Einherjar Chest");
            AddWord(__instance, "piece_einherjarchest_description", "A reliquary connected to the world's shared inventory. Build with an Einherjar Essence or a Dimmed Essence bound to this area. Hammer deconstruction returns a Dimmed Essence; damage destruction returns none.");
            AddWord(__instance, "einherjar_open", "Open shared inventory");
            AddWord(__instance, "einherjar_noportal", "Einherjar Essence cannot pass through portals.");
        }
    }

    // This runs after Valheim applies world/portal overrides (including the
    // unrestricted portal). Scope it to portal eligibility, not admin TeleportTo.
    [HarmonyPatch(typeof(Humanoid), "IsTeleportable")]
    private static class BlockEssencePortal
    {
        private static void Postfix(Humanoid __instance, ref bool __result)
        {
            if (__result && !EssencePortalsAllowed && CarriesEssence(__instance)) __result = false;
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), "Teleport")]
    private static class ExplainBlockedPortal
    {
        private static bool Prefix(Player player)
        {
            if (EssencePortalsAllowed || !CarriesEssence(player)) return true;
            player.Message(MessageHud.MessageType.Center, "$einherjar_noportal");
            return false;
        }
    }

    private static bool CarriesEssence(Humanoid player) => Items.Count(player.GetInventory(), ContentIds.Essence) > 0 ||
        Items.Count(player.GetInventory(), ContentIds.DimmedEssence) > 0;

    // Dropped prefabs can carry their own SharedData copy. Refresh item instances
    // at the actual inventory eligibility check, including traded/old pickups.
    internal static void SyncPortalEligibility(Inventory inventory)
    {
        foreach (var item in inventory.GetAllItems())
            if (item.m_dropPrefab != null && !ContentIds.CanShare(Items.Id(item)))
                item.m_shared.m_teleportable = EssencePortalsAllowed;
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.IsTeleportable))]
    private static class InventoryPortalEligibility
    {
        private static void Prefix(Inventory __instance) => SyncPortalEligibility(__instance);
    }
}

// No Update, duplicate inventory, or vanilla Container on these pieces.
public sealed class SharedChest : MonoBehaviour, Hoverable, Interactable, IPlaced
{
    public void OnPlaced() => ChestConstruction.OnPlaced(this);
    private void OnEnable() => ChestAccess.Register(this);
    private void OnDisable() => ChestAccess.Unregister(this);
    public float GetHoverOffset() => .7f;
    public string GetHoverName() => Localization.instance.Localize("$piece_einherjarchest");
    public string GetHoverText() => Localization.instance.Localize("$piece_einherjarchest\n[<color=yellow><b>$KEY_Use</b></color>] $einherjar_open");
    public bool Interact(Humanoid user, bool hold, bool alt)
    {
        if (hold || user != Player.m_localPlayer || GetComponent<ZNetView>()?.IsValid() != true) return false;
        if (Plugin.Instance?.OpenChest() == true)
            Plugin.Instance.Tutorials?.Record(Player.m_localPlayer, TutorialLesson.Chest);
        return true;
    }
    public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
}
