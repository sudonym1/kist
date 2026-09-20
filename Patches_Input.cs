using HarmonyLib;
using UnityEngine;

namespace ThorsKist
{
    /// <summary>
    /// While the Kist window is up the game must not also act on the same key
    /// press or click.
    ///
    /// Gating Player.TakeInput() alone is NOT enough, and that was the bug:
    /// Player.Update does respect it, but InventoryGui.Update and Minimap.Update
    /// read ZInput directly and never consult it, so Tab still opened the
    /// inventory and M still opened the map underneath this window. Every game
    /// system reads input through ZInput's static getters, so answering "nothing
    /// is pressed" there is the one place that covers all of them at once.
    ///
    /// This mod's own UI is unaffected: it reads UnityEngine.Input, and uGUI
    /// clicks arrive through Unity's InputSystemUIInputModule. Neither goes near
    /// ZInput.
    /// </summary>
    [HarmonyPatch]
    public static class Patches_Input
    {
        /// <summary>
        /// Borrows the trader screen's input handling, which is what stops the
        /// player walking and frees the mouse. StoreGui.IsVisible() is consulted
        /// from exactly two places in the game -- Player.TakeInput and the camera
        /// control gate -- so it is a precise thing to answer yes to.
        /// </summary>
        [HarmonyPatch(typeof(StoreGui), nameof(StoreGui.IsVisible))]
        [HarmonyPostfix]
        public static void StoreGuiVisible(ref bool __result)
        {
            if (KistUI.BlocksGameInput) __result = true;
        }

        // ------------------------------------------------------------------
        // ZInput: report nothing pressed, nothing moved.
        //
        // Written out one target at a time rather than through TargetMethods so
        // that tools/VerifyPatches can resolve every one of them against the
        // real assembly. An input block that silently stops binding would be
        // invisible until someone noticed the game reacting again.
        // ------------------------------------------------------------------

        private static bool Blocked { get { return KistUI.BlocksGameInput; } }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton), new[] { typeof(string) })]
        [HarmonyPostfix]
        public static void Button(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown), new[] { typeof(string) })]
        [HarmonyPostfix]
        public static void ButtonDown(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonUp), new[] { typeof(string) })]
        [HarmonyPostfix]
        public static void ButtonUp(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseButton), new[] { typeof(int) })]
        [HarmonyPostfix]
        public static void MouseButton(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseButtonDown), new[] { typeof(int) })]
        [HarmonyPostfix]
        public static void MouseButtonDown(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseButtonUp), new[] { typeof(int) })]
        [HarmonyPostfix]
        public static void MouseButtonUp(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKey), new[] { typeof(KeyCode), typeof(bool) })]
        [HarmonyPostfix]
        public static void Key(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyDown), new[] { typeof(KeyCode), typeof(bool) })]
        [HarmonyPostfix]
        public static void KeyDown(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyUp), new[] { typeof(KeyCode), typeof(bool) })]
        [HarmonyPostfix]
        public static void KeyUp(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetRadialTap))]
        [HarmonyPostfix]
        public static void RadialTap(ref bool __result) { if (Blocked) __result = false; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetRadialMultiTap))]
        [HarmonyPostfix]
        public static void RadialMultiTap(ref bool __result) { if (Blocked) __result = false; }

        // Mouse look, and the wheel -- without this, scrolling the item list
        // also zooms the camera and cycles the hotbar.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseDelta))]
        [HarmonyPostfix]
        public static void MouseDelta(ref Vector2 __result) { if (Blocked) __result = Vector2.zero; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
        [HarmonyPostfix]
        public static void ScrollWheel(ref float __result) { if (Blocked) __result = 0f; }

        // Gamepad sticks and triggers, so a controller cannot drive the player
        // around behind the window either.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetJoyLeftStick))]
        [HarmonyPostfix]
        public static void JoyLeft(ref Vector2 __result) { if (Blocked) __result = Vector2.zero; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetJoyRightStick))]
        [HarmonyPostfix]
        public static void JoyRight(ref Vector2 __result) { if (Blocked) __result = Vector2.zero; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetJoyLeftStickX))]
        [HarmonyPostfix]
        public static void JoyLeftX(ref float __result) { if (Blocked) __result = 0f; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetJoyLeftStickY))]
        [HarmonyPostfix]
        public static void JoyLeftY(ref float __result) { if (Blocked) __result = 0f; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetJoyRightStickX))]
        [HarmonyPostfix]
        public static void JoyRightX(ref float __result) { if (Blocked) __result = 0f; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetJoyRightStickY))]
        [HarmonyPostfix]
        public static void JoyRightY(ref float __result) { if (Blocked) __result = 0f; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetJoyLTrigger))]
        [HarmonyPostfix]
        public static void JoyLTrigger(ref float __result) { if (Blocked) __result = 0f; }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetJoyRTrigger))]
        [HarmonyPostfix]
        public static void JoyRTrigger(ref float __result) { if (Blocked) __result = 0f; }
    }
}
