using System.Collections.Generic;
using UnityEngine;

namespace SharedInventory;

// Only live local scene instances count. No ZDO scans, settings, persistence,
// or access RPCs: loading distance may reduce the usable 100-metre radius.
internal static class ChestAccess
{
    internal const float Radius = 100;
    internal const string Unavailable = "There is no Einherjar Chest in range.";
    private static readonly HashSet<SharedChest> loaded = new HashSet<SharedChest>();

    internal static bool CanAccess => Player.m_localPlayer != null &&
        !Player.m_localPlayer.IsDead() && Covers(Player.m_localPlayer.transform.position);

    internal static void Register(SharedChest chest) => loaded.Add(chest);
    internal static void Unregister(SharedChest chest) => loaded.Remove(chest);
    internal static void Clear() => loaded.Clear();

    internal static bool Covers(Vector3 position)
    {
        foreach (var chest in loaded)
        {
            // Prefab-library objects, placement ghosts, inactive/unloaded
            // pieces, and objects already detached from their ZDO grant nothing.
            if (chest == null || !chest.isActiveAndEnabled || !chest.gameObject.activeInHierarchy ||
                chest.GetComponent<ZNetView>()?.IsValid() != true) continue;
            var center = chest.transform.position;
            var x = (double)position.x - center.x;
            var y = (double)position.y - center.y;
            var z = (double)position.z - center.z;
            if (x * x + y * y + z * z <= Radius * Radius) return true;
        }
        return false;
    }
}
