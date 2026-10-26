using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

internal sealed class StationShortcuts
{
    private static readonly Func<Player, bool> takeInput = AccessTools.MethodDelegate<Func<Player, bool>>(
        AccessTools.Method(typeof(Player), "TakeInput"));
    private static readonly Action<Humanoid, GameObject> animate = AccessTools.MethodDelegate<Action<Humanoid, GameObject>>(
        AccessTools.Method(typeof(Humanoid), "DoInteractAnimation"));
    private readonly StationInventory inputs;
    private readonly StationShortcutMenu menu = new StationShortcutMenu();
    private readonly StationShortcutInput keys = new StationShortcutInput();
    private readonly StationShortcutInput pageKeys = new StationShortcutInput();

    public StationShortcuts(StationInventory inputs) => this.inputs = inputs;

    private static bool Alt => ZInput.GetKey(KeyCode.LeftAlt) || ZInput.GetKey(KeyCode.RightAlt);

    private static bool Target(Player player, out Component? station, out StationInputKind kind)
    {
        station = null;
        kind = StationInputKind.Processing;
        return player == Player.m_localPlayer && !Plugin.WindowOpen && takeInput(player) &&
            !player.InAttack() && !player.InDodge() && !Hud.InRadial() &&
            StationTargets.TryGet(player.GetHoverObject(), out station, out kind);
    }

    public bool CapturesHotbar(Player player) => Alt && Target(player, out _, out _);

    public void Update(Player player)
    {
        // Hover lifetime is independent of input focus: an animation, chat, or
        // menu must not silently rebind keys while the same control is targeted.
        if (StationTargets.TryGet(player.GetHoverObject(), out var hoveredStation, out var hoveredKind) && hoveredStation != null)
            menu.Observe(StationLoading.Context(hoveredStation, hoveredKind));
        else menu.Clear();
        var held = 0;
        for (var slot = 0; slot < StationShortcutMenu.Slots; ++slot)
        {
            var digit = (slot + 1) % 10;
            if (ZInput.GetKey((KeyCode)((int)KeyCode.Alpha0 + digit)) ||
                ZInput.GetKey((KeyCode)((int)KeyCode.Keypad0 + digit))) held |= 1 << slot;
        }
        var enabled = Target(player, out var station, out var kind);
        var pressed = keys.Press(enabled, Alt, held);
        var pagePressed = pageKeys.Press(enabled, Alt, (ZInput.GetKey(KeyCode.PageUp) ? 1 : 0) | (ZInput.GetKey(KeyCode.PageDown) ? 2 : 0));
        if (!enabled) return;
        if (station == null) return;
        var context = StationLoading.Context(station, kind);
        if (pagePressed >= 0 && menu.Move(context, pagePressed == 0 ? -1 : 1)) return;
        if (pressed < 0) return;
        // Bind the key to the last displayed item, then revalidate that exact
        // prefab. A stock change must never load its newly shifted neighbour.
        var requested = menu.Item(context, pressed);
        if (requested == null) return;
        var item = inputs.Select(player.GetInventory(), context, StationLoading.AcceptedInputs(station, kind), requested);
        if (item == null) return;
        var hover = player.GetHoverObject();
        if (hover == null) return;
        var control = hover.GetComponentInParent<Interactable>();
        // Use the station directly. Humanoid.UseItem can eat a local ingredient
        // when a full cooking station rejects it; shortcuts must not do that.
        if (control != null && control.UseItem(player, item)) animate(player, hover);
    }

    public string HoverText(Component source, string original)
    {
        var player = Player.m_localPlayer;
        if (player == null || !Target(player, out var station, out var kind) || station == null) return original;
        var hover = player.GetHoverObject();
        if (hover == null || !ReferenceEquals(hover.GetComponentInParent<Hoverable>(), source)) return original;
        var context = StationLoading.Context(station, kind);
        if (inputs.HasSharedInput(context, StationLoading.AcceptedInputs(station, kind)))
            Plugin.Instance?.Tutorials?.Record(player, TutorialLesson.Station);
        var options = inputs.Options(player.GetInventory(), context, StationLoading.AcceptedInputs(station, kind), Name);
        menu.Show(context, options);
        var displayed = menu.Options;
        if (displayed.Count == 0) return original;
        var text = new StringBuilder(original);
        var offset = menu.Page * StationShortcutMenu.Slots;
        for (var slot = 0; offset + slot < displayed.Count && slot < StationShortcutMenu.Slots; ++slot)
        {
            var option = displayed[offset + slot];
            text.Append("\n[<color=yellow>Alt+").Append((slot + 1) % 10).Append("</color>] ")
                .Append(option.Name).Append("  (").Append(option.Count.ToString("N0", CultureInfo.CurrentCulture)).Append(')');
        }
        if (menu.PageCount > 1)
            text.Append("\n[<color=yellow>Alt+PageUp / PageDown</color>] ").Append(menu.Page + 1).Append('/').Append(menu.PageCount);
        return text.ToString();
    }

    private static string Name(string id) => Localization.instance.Localize(Items.Template(id)?.m_shared.m_name ?? id);

    [HarmonyPatch(typeof(Player), "Update")]
    private static class ReadShortcuts
    {
        private static void Postfix(Player __instance)
        {
            if (__instance == Player.m_localPlayer) Plugin.Shortcuts?.Update(__instance);
        }
    }

    [HarmonyPatch(typeof(Player), "UseHotbarItem")]
    private static class ReserveAltNumbers
    {
        private static bool Prefix(Player __instance) => Plugin.Shortcuts?.CapturesHotbar(__instance) != true;
    }

    [HarmonyPatch]
    private static class HoverHints
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Switch), "GetHoverText");
            yield return AccessTools.Method(typeof(CookingStation), "GetHoverText");
            yield return AccessTools.Method(typeof(Fermenter), "GetHoverText");
            yield return AccessTools.Method(typeof(Fireplace), "GetHoverText");
        }
        private static void Postfix(Component __instance, ref string __result)
        {
            if (Plugin.Shortcuts != null) __result = Plugin.Shortcuts.HoverText(__instance, __result);
        }
    }
}
