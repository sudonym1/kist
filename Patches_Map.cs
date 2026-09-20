using HarmonyLib;

namespace ThorsKist
{
    /// <summary>
    /// Keeps the Kist pin on the map and out of the player's hands.
    /// </summary>
    [HarmonyPatch]
    public static class Patches_Map
    {
        /// <summary>
        /// Every way a player can delete a pin -- right-click on the large map,
        /// the pin list, the radius sweep -- ends at this one overload, so one
        /// guard here covers all of them. The pin is server state; removing it
        /// locally would only make the map lie until the next sync.
        /// </summary>
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.RemovePin), new[] { typeof(Minimap.PinData) })]
        [HarmonyPrefix]
        public static bool BlockRemovingKistPin(Minimap.PinData pin)
        {
            return !KistMap.IsProtected(pin);
        }

        /// <summary>
        /// Loading map data clears every pin. Ours is not saved with the map, so
        /// it has to be told it is gone -- otherwise it holds a reference to a
        /// destroyed pin and never re-adds itself.
        /// </summary>
        [HarmonyPatch(typeof(Minimap), "ClearPins")]
        [HarmonyPostfix]
        public static void OnPinsCleared()
        {
            KistMap.Forget();
        }
    }
}
