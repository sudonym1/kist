using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

[BepInPlugin(Guid, "Shared Inventory", "0.1.0")]
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
    private float nextList;
    internal static bool WindowOpen => Instance?.window?.Visible == true;

    private void Awake()
    {
        Instance = this;
        toggleKey = Config.Bind("Client", "ToggleKey", KeyCode.F3, "Open shared inventory from anywhere.");
        windowPosition = Config.Bind("Client", "WindowPosition", new Vector2(-1, -1),
            "Window position in screen pixels. Set both coordinates to -1 to start centered.");
        harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(Plugin).Assembly);
    }

    private void Attach()
    {
        if (ZRoutedRpc.instance == null || ReferenceEquals(router, ZRoutedRpc.instance)) return;
        api?.Stop();
        window?.Dispose();
        router = ZRoutedRpc.instance;
        api = new RpcApi(router, Logger,
            reply => client?.OnList(reply),
            reply => client?.OnDeposit(reply),
            reply => client?.OnWithdrawal(reply));
        client = new Client(api);
        window = new Window(client, windowPosition);
        nextList = 0;
    }

    private void Update()
    {
        var now = Time.realtimeSinceStartup;
        api?.Pump();
        if (Player.m_localPlayer == null || Player.m_localPlayer.IsDead())
        {
            window?.Close();
            return;
        }
        if (Input.GetKeyDown(toggleKey.Value)) window?.Toggle();
        if (WindowOpen && Input.GetKeyDown(KeyCode.Escape)) window?.Close();
        if (WindowOpen && now >= nextList && client != null)
        {
            nextList = now + 2;
            client.List();
        }
    }

    private void OnGUI() => window?.Draw();
    private void LateUpdate() => api?.Pump();

    private void ResetSession()
    {
        api?.Stop();
        window?.Close();
        window?.Dispose();
        router = null;
        api = null;
        client = null;
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
            if (!WindowOpen) return true;
            // IMGUI reads Event.current directly, so the list still scrolls.
            // Camera zoom and other gameplay wheel actions read ZInput.
            __result = 0f;
            return false;
        }
    }
}
