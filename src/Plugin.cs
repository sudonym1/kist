using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

[BepInPlugin(Guid, "Einherjar Chest", "0.10.2")]
[BepInIncompatibility("posixone_ThorsKist")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "local.sharedinventory";
    internal static Plugin? Instance;
    private Harmony harmony = null!;
    private ConfigEntry<KeyCode> toggleKey = null!;
    private ConfigEntry<double> ritualMinutes = null!;
    private ChestRadiusVisual? radiusVisual;
    private ZRoutedRpc? router;
    private RpcApi? api;
    private Client? client;
    private Window? window;
    private bool attachPending;
    internal Ritual? Ritual { get; private set; }
#if SHAREDINVENTORY_TESTING
    private TestPanel? testPanel;
    internal TestPanel? TestingPanel => testPanel;
#endif
    private readonly WindowInput windowInput = new WindowInput();
    internal static ResourceInventory? Resources { get; private set; }
    internal static StationInventory? StationItems { get; private set; }
    internal static StationShortcuts? Shortcuts { get; private set; }
    internal static bool WindowOpen => Instance?.window?.Visible == true;
    internal void OpenChest() { if (!WindowOpen) ToggleWindow(); }

    private void ToggleWindow()
    {
        if (WindowOpen) { window?.Close(); return; }
        window?.Toggle();
    }

    private void Awake()
    {
        Instance = this;
        toggleKey = Config.Bind("Client", "ToggleKey", KeyCode.F3, "Open shared inventory. Transfers require an Einherjar Chest in range.");
        ritualMinutes = Config.Bind("Server", "RitualAngerMinutes", 30d,
            "Hidden world-wide ritual interval. Must be finite and positive. Changes apply to future timer starts; existing deadlines remain unchanged.");
#if SHAREDINVENTORY_TESTING
        testPanel = TestPanel.Create(Config, Logger);
#endif
        harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(Plugin).Assembly);
    }

    private void RequestAttach()
    {
        attachPending = true;
        Attach();
    }

    private void Attach()
    {
        // Game.Start runs before a joining client's PeerInfo handshake supplies
        // its World. GetWorldUID dereferences that still-null World. Wait before
        // constructing either the ritual endpoint or its Testing controls.
        if (ZRoutedRpc.instance == null || ZNet.instance == null || ZNet.instance.GetWorld() == null) return;
        if (ReferenceEquals(router, ZRoutedRpc.instance)) { attachPending = false; return; }
        api?.Stop();
        Ritual?.Stop();
        radiusVisual?.Dispose();
#if SHAREDINVENTORY_TESTING
        testPanel?.Detach();
#endif
        window?.Dispose();
        router = ZRoutedRpc.instance;
        Ritual = new Ritual(router, Logger, () => ritualMinutes.Value);
        radiusVisual = new ChestRadiusVisual();
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
        window = new Window(client);
#if SHAREDINVENTORY_TESTING
        testPanel?.Attach(api, client);
        testPanel?.AttachRitual(router, Ritual);
        window.TestingPanel = testPanel;
#endif
        attachPending = false;
    }

    private void Update()
    {
        if (attachPending) Attach();
        Ritual?.Update();
        radiusVisual?.Update();
        api?.Pump();
        client?.MaintainRefresh();
#if SHAREDINVENTORY_TESTING
        testPanel?.Update();
        if (testPanel != null) AssetCapture.Update();
        if (testPanel != null) ContentAcceptance.Update(this);
        if (testPanel != null) AccessAcceptance.Update(this);
        if (testPanel != null) RitualAcceptance.Update(this);
        if (testPanel != null) RitualOutcomeAcceptance.Update(this);
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
            case WindowInput.Action.Toggle: ToggleWindow(); break;
            case WindowInput.Action.Close: window?.Close(); break;
        }
        client?.Refresh(WindowOpen, InventoryGui.IsVisible(), Hud.IsPieceSelectionVisible(),
            StationTargets.IsInputTarget(Player.m_localPlayer.GetHoverObject()));
    }

    private void OnGUI()
    {
        Ritual?.Draw();
        window?.Draw();
    }
    private void LateUpdate() => api?.Pump();

    private void ResetSession()
    {
        attachPending = false;
        api?.Stop();
        Ritual?.Stop(); Ritual = null;
        RitualSite.Clear();
        ChestAccess.Clear();
        radiusVisual?.Dispose(); radiusVisual = null;
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
        Content.Dispose();
        RitualLightning.Dispose();
        harmony.UnpatchSelf();
        Instance = null;
    }

    private void OnApplicationQuit()
    {
        ResetSession();
        RpcApi.FinishShutdown();
        RitualStorage.FinishShutdown();
    }

    [HarmonyPatch(typeof(Game), "Start")]
    private static class RegisterRpc
    {
        private static void Prefix() => Instance?.RequestAttach();
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

    [HarmonyPatch]
    private static class BlockWindowKeys
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(ZInput), "GetKeyDown"),
            AccessTools.Method(typeof(ZInput), "GetKeyUp")
        };

        private static bool Prefix(KeyCode key, ref bool __result)
        {
            if (Instance == null || !Instance.windowInput.BlockKey(WindowOpen,
                key == KeyCode.Escape, Time.frameCount)) return true;
            // Suppress raw shortcuts while open, and keep consuming Escape
            // after Plugin.Update closes the window so it cannot open a menu.
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    private static class BlockWindowShortcuts
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(ZInput), "GetButton"),
            AccessTools.Method(typeof(ZInput), "GetButtonDown"),
            AccessTools.Method(typeof(ZInput), "GetButtonUp")
        };

        private static bool Prefix(ref bool __result)
        {
            if (!WindowOpen) return true;
            // Map, inventory, and chat read named bindings independently of
            // Player.TakeInput. IMGUI still receives text and mouse events;
            // Plugin.Update reads held keys directly for toggle and Escape.
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
