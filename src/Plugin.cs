using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

[BepInPlugin(Guid, "Shared Inventory", "0.4.1")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "local.sharedinventory";
    internal static Plugin? Instance;
    private Harmony harmony = null!;
    private ConfigEntry<KeyCode> toggleKey = null!;
    private ConfigEntry<Vector2> windowPosition = null!;
    private ZRoutedRpc? router;
    private RpcApi? api;
    private Client? client;
    private Window? window;
#if SHAREDINVENTORY_TESTING
    private TestPanel? testPanel;
#endif
    private readonly WindowInput windowInput = new WindowInput();
    internal static ResourceInventory? Resources { get; private set; }
    internal static StationInventory? StationItems { get; private set; }
    internal static StationShortcuts? Shortcuts { get; private set; }
    internal static bool WindowOpen => Instance?.window?.Visible == true;

    private void Awake()
    {
        Instance = this;
        toggleKey = Config.Bind("Client", "ToggleKey", KeyCode.F3, "Open shared inventory from anywhere.");
#if SHAREDINVENTORY_TESTING
        testPanel = TestPanel.Create(Config, Logger);
#endif
        windowPosition = Config.Bind("Client", "WindowPosition", new Vector2(-1, -1),
            "Window position in screen pixels. Set both coordinates to -1 to start centered.");
        harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(Plugin).Assembly);
    }

    private void Attach()
    {
        if (ZRoutedRpc.instance == null || ReferenceEquals(router, ZRoutedRpc.instance)) return;
        api?.Stop();
#if SHAREDINVENTORY_TESTING
        testPanel?.Detach();
#endif
        window?.Dispose();
        router = ZRoutedRpc.instance;
        api = new RpcApi(router, Logger,
            reply => client?.OnList(reply),
            reply => client?.OnDeposit(reply),
            reply => client?.OnWithdrawal(reply),
            reply => client?.OnBatch(reply)
#if SHAREDINVENTORY_TESTING
            , testPanel?.NewSession()
#endif
            );
        client = new Client(api);
        Resources = new ResourceInventory(client);
        StationItems = new StationInventory(client);
        Shortcuts = new StationShortcuts(StationItems);
        window = new Window(client, windowPosition);
#if SHAREDINVENTORY_TESTING
        testPanel?.Attach(api, client);
        window.TestingPanel = testPanel;
#endif
    }

    private void Update()
    {
        api?.Pump();
        client?.MaintainRefresh();
#if SHAREDINVENTORY_TESTING
        testPanel?.Update();
#endif
        // Read the same input backend as Valheim. Track held-key edges so the
        // game's manual input updates cannot clear a press before our Update.
        var input = windowInput.Update(WindowOpen, ZInput.GetKey(toggleKey.Value),
            ZInput.GetKey(KeyCode.Escape), Time.frameCount);
        if (Player.m_localPlayer == null || Player.m_localPlayer.IsDead())
        {
            window?.Close();
            return;
        }
        switch (input)
        {
            case WindowInput.Action.Toggle: window?.Toggle(); break;
            case WindowInput.Action.Close: window?.Close(); break;
        }
        client?.Refresh(WindowOpen, InventoryGui.IsVisible(), Hud.IsPieceSelectionVisible(),
            StationTargets.IsInputTarget(Player.m_localPlayer.GetHoverObject()));
    }

    private void OnGUI()
    {
        window?.Draw();
    }
    private void LateUpdate() => api?.Pump();

    private void ResetSession()
    {
        api?.Stop();
#if SHAREDINVENTORY_TESTING
        testPanel?.Detach();
#endif
        window?.Close();
        window?.Dispose();
        router = null;
        api = null;
        client = null;
        Resources = null;
        StationItems = null;
        Shortcuts = null;
        window = null;
    }

    private void OnDestroy()
    {
        ResetSession();
        harmony.UnpatchSelf();
        Instance = null;
    }

    private void OnApplicationQuit()
    {
        ResetSession();
        RpcApi.FinishShutdown();
    }

    [HarmonyPatch(typeof(Game), "Start")]
    private static class RegisterRpc
    {
        private static void Prefix() => Instance?.Attach();
    }

    [HarmonyPatch(typeof(ZNet), "Shutdown")]
    private static class SaveOnShutdown
    {
        private static void Prefix() => Instance?.ResetSession();
    }

    [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
    private static class AltClickDeposit
    {
        private static bool Prefix(InventoryGrid grid, ItemDrop.ItemData item, GameObject ___m_dragGo)
        {
            if (!Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt)) return true;
            if (___m_dragGo != null) return true;
            var player = Player.m_localPlayer;
            if (player == null || item == null || grid.GetInventory() != player.GetInventory()) return true;
            Instance?.client?.Deposit(item);
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), "TakeInput")]
    private static class BlockPlayerInput
    {
        private static void Postfix(ref bool __result) { if (WindowOpen) __result = false; }
    }

    [HarmonyPatch(typeof(ZInput), "GetKeyDown")]
    private static class ConsumeWindowEscape
    {
        private static bool Prefix(KeyCode key, ref bool __result)
        {
            if (key != KeyCode.Escape || Instance == null ||
                !Instance.windowInput.BlockEscape(WindowOpen, Time.frameCount)) return true;
            // Cover readers both before and after Plugin.Update closes the
            // window, including the pause menu and the UI underneath us.
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    private static class BlockMovementInput
    {
        private static void Postfix(ref bool __result) { if (WindowOpen) __result = false; }
    }

    [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture")]
    private static class ReleaseCursor
    {
        private static bool Prefix()
        {
            if (!WindowOpen) return true;
            // Replace capture rather than undoing it each frame. Use the game's
            // cursor API so its software cursor and hardware cursor agree.
            ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();
            return false;
        }
    }

    [HarmonyPatch(typeof(ZInput), "GetMouseScrollWheel")]
    private static class BlockGameplayScroll
    {
        private static bool Prefix(ref float __result)
        {
            if (Instance?.window?.IsPointerOver != true) return true;
            // IMGUI reads Event.current directly, so the list still scrolls.
            // Only block gameplay wheel actions while hovering this window.
            __result = 0f;
            return false;
        }
    }
}
