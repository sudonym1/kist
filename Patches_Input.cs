using HarmonyLib;

namespace ThorsKist
{
    /// <summary>
    /// While the Kist window is up the player must stop walking and the camera
    /// must stop tracking the mouse -- exactly what vanilla does for its own
    /// panels.
    ///
    /// Valheim gates both on a list of "is some GUI up?" checks, and
    /// StoreGui.IsVisible() is the one member of that list referenced from those
    /// two call sites and nowhere else in the game. Answering yes to it while
    /// this window is open borrows the trader screen's input handling wholesale,
    /// without touching InventoryGui.IsVisible(), which a dozen other systems
    /// branch on.
    /// </summary>
    [HarmonyPatch(typeof(StoreGui), nameof(StoreGui.IsVisible))]
    public static class Patches_Input
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (KistUI.IsOpen) __result = true;
        }
    }
}
