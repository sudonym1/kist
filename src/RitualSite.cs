using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

internal sealed class RitualSite
{
    // Valheim 1.0.20 StartTemple, measured in the isolated game. These are the
    // seven standing-stone origins in location space, clockwise. The playable
    // interior joins the stones, rather than accepting the whole starting area.
    internal static readonly Vector2[] Perimeter = {
        new Vector2(-6.40f, 5.04f), new Vector2(-1.76f, 8.11f),
        new Vector2(7.02f, 5.53f), new Vector2(9.61f, -4.28f),
        new Vector2(1.61f, -11.27f), new Vector2(-5.38f, -8.71f),
        new Vector2(-9.13f, -1.03f)
    };
    private static readonly HashSet<Location> loaded = new HashSet<Location>();
    private readonly Location location;
    public Vector3 Center => location.transform.position;
    public Quaternion Rotation => location.transform.rotation;
    private RitualSite(Location location) { this.location = location; }

    public static bool TryGet(out RitualSite site)
    {
        site = null!;
        var zones = ZoneSystem.instance;
        if (zones == null || !zones.GetLocationIcon("StartTemple", out var original)) return false;
        foreach (var candidate in loaded)
        {
            if (candidate == null || !candidate.gameObject.activeInHierarchy ||
                (candidate.transform.position - original).sqrMagnitude > .25f) continue;
            if (site != null) return false; // Ambiguous copied proxies fail closed.
            site = new RitualSite(candidate);
        }
        return site != null;
    }

    public bool Contains(Vector3 position)
    {
        var p = location.transform.InverseTransformPoint(position);
        if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || p.y < -1 || p.y > 2.5f) return false;
        for (var i = 0; i < Perimeter.Length; i++)
        {
            var a = Perimeter[i]; var b = Perimeter[(i + 1) % Perimeter.Length];
            if ((b.x - a.x) * (p.z - a.y) - (b.y - a.y) * (p.x - a.x) > .001f) return false;
        }
        return true;
    }

    public bool IsStone(BossStone stone)
    {
        if (stone == null || !stone.gameObject.activeInHierarchy) return false;
        var view = stone.GetComponent<ZNetView>();
        if (view == null || !view.IsValid()) return false;
        var prefab = ZNetScene.instance?.GetPrefab(view.GetZDO().GetPrefab());
        if (prefab == null || prefab.GetComponent<BossStone>() == null) return false;
        var local = location.transform.InverseTransformPoint(stone.transform.position);
        if (!Finite(local.x) || !Finite(local.y) || !Finite(local.z) || local.y < -1 || local.y > 1) return false;
        foreach (var origin in Perimeter)
        {
            var x = local.x - origin.x;
            var z = local.z - origin.y;
            if (x * x + z * z < .09f) return true;
        }
        return false;
    }

    public static bool IsOriginal(BossStone stone) => stone != null && TryGet(out var site) && site.IsStone(stone);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static void Clear() => loaded.Clear();

    [HarmonyPatch(typeof(Location), "Awake")]
    private static class Loaded
    {
        private static void Postfix(Location __instance)
        {
            if (__instance.name == "StartTemple(Clone)") loaded.Add(__instance);
        }
    }
    [HarmonyPatch(typeof(Location), "OnDestroy")]
    private static class Unloaded
    {
        private static void Prefix(Location __instance) => loaded.Remove(__instance);
    }

    // Both the reading surface and trophy/power control offer the same action.
    // Normal use and UseItem still belong to Valheim.
    [HarmonyPatch]
    private static class Hover
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(RuneStone), nameof(RuneStone.GetHoverText));
            yield return AccessTools.Method(typeof(ItemStand), nameof(ItemStand.GetHoverText));
        }
        private static void Postfix(Component __instance, ref string __result)
        {
            var stone = __instance.GetComponentInParent<BossStone>();
            if (!IsOriginal(stone)) return;
            var ritual = Plugin.Instance?.Ritual;
            ritual?.Inspect(stone);
            __result += Localization.instance.Localize("\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] Begin Einherjar ritual") +
                "\nGather every connected player inside the stones.\nOne will be chosen at random. Odin punishes avarice.";
            if (ritual != null && ritual.Presence.Length != 0) __result += "\n" + ritual.Presence;
        }
    }
    [HarmonyPatch]
    private static class Trigger
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(RuneStone), nameof(RuneStone.Interact));
            yield return AccessTools.Method(typeof(ItemStand), nameof(ItemStand.Interact));
        }
        private static bool Prefix(Component __instance, Humanoid __0, bool __1, bool __2, ref bool __result)
        {
            if (!__2) return true;
            var stone = __instance.GetComponentInParent<BossStone>();
            if (!IsOriginal(stone)) return true;
            __result = false;
            if (!__1 && __0 == Player.m_localPlayer) Plugin.Instance?.Ritual?.Begin(stone);
            return false;
        }
    }
}
