using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

internal static class TutorialHooks
{
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
    private static class EssencePickup
    {
        private static void Prefix(Humanoid __instance, GameObject go, out bool __state)
        {
            var item = go != null ? go.GetComponent<ItemDrop>()?.m_itemData : null;
            __state = __instance == Player.m_localPlayer && item?.m_dropPrefab != null && Items.Id(item) == ContentIds.Essence;
        }
        private static void Postfix(Humanoid __instance, bool __state, bool __result)
        {
            if (__state && __result && __instance is Player player)
                Plugin.Instance?.Tutorials?.Record(player, TutorialLesson.Essence);
        }
    }
}
