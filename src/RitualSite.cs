using System.Collections.Generic;
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
    public Vector3 PlaquePosition => location.transform.TransformPoint(new Vector3(-6.4f, -.144f, 5.04f));
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

    public bool IsPlaque(RuneStone rune)
    {
        if (rune == null || !rune.gameObject.activeInHierarchy || rune.GetComponent<BossStone>() == null ||
            rune.m_text != "$guardianstone_eikthyr_desc") return false;
        var view = rune.GetComponent<ZNetView>();
        return view != null && view.IsValid() && view.GetZDO().GetPrefab() == "BossStone_Eikthyr".GetStableHashCode() &&
            (rune.transform.position - PlaquePosition).sqrMagnitude < .09f;
    }

    public static bool IsOriginal(RuneStone rune) => TryGet(out var site) && site.IsPlaque(rune);
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

    [HarmonyPatch(typeof(RuneStone), nameof(RuneStone.GetHoverText))]
    private static class Hover
    {
        private static void Postfix(RuneStone __instance, ref string __result)
        {
            if (!IsOriginal(__instance)) return;
            var ritual = Plugin.Instance?.Ritual;
            ritual?.Inspect(__instance);
            __result += Localization.instance.Localize("\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] Begin Einherjar ritual") +
                "\nGather every connected player inside the stones.\nOne will be chosen at random. Odin punishes avarice.";
            if (ritual != null && ritual.Presence.Length != 0) __result += "\n" + ritual.Presence;
        }
    }
    [HarmonyPatch(typeof(RuneStone), nameof(RuneStone.Interact))]
    private static class Trigger
    {
        private static bool Prefix(RuneStone __instance, Humanoid character, bool hold, bool alt, ref bool __result)
        {
            if (!alt || !IsOriginal(__instance)) return true;
            __result = false;
            if (!hold && character == Player.m_localPlayer) Plugin.Instance?.Ritual?.Begin(__instance);
            return false;
        }
    }
}
