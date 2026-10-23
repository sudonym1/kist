using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

// Station interaction, validation, and owner RPCs remain Valheim's. These hooks
// replace only input selection, availability, and the corresponding consumption.
internal static class StationLoading
{
    internal static StationInputContext Context(Component station, StationInputKind kind)
    {
        var id = station.gameObject.name;
        const string clone = "(Clone)";
        if (id.EndsWith(clone, StringComparison.Ordinal)) id = id.Substring(0, id.Length - clone.Length);
        return new StationInputContext(station, id, kind);
    }

    internal static IEnumerable<string> ProcessingInputs(Component station)
    {
        if (station is Smelter smelter)
        {
            foreach (var conversion in smelter.m_conversion)
                if (conversion.m_from != null) yield return conversion.m_from.gameObject.name;
        }
        else if (station is CookingStation cooking)
        {
            foreach (var conversion in cooking.m_conversion)
                if (conversion.m_from != null) yield return conversion.m_from.gameObject.name;
        }
        else if (station is Fermenter fermenter)
        {
            foreach (var conversion in fermenter.m_conversion)
                if (conversion.m_from != null) yield return conversion.m_from.gameObject.name;
        }
    }

    internal static ItemDrop? Fuel(Component station) => station is Smelter smelter ? smelter.m_fuelItem :
        station is CookingStation cooking ? cooking.m_fuelItem : station is Fireplace fire ? fire.m_fuelItem : null;

    internal static IEnumerable<string> AcceptedInputs(Component station, StationInputKind kind)
    {
        if (kind == StationInputKind.Processing)
        {
            foreach (var id in ProcessingInputs(station)) yield return id;
        }
        else
        {
            var fuel = Fuel(station);
            if (fuel != null) yield return fuel.gameObject.name;
        }
    }

    private static ItemDrop.ItemData? SelectFuel(Inventory inventory, Component station, string name)
    {
        var fuel = Fuel(station);
        if (fuel == null || fuel.m_itemData.m_shared.m_name != name) return null;
        return Plugin.StationItems?.Select(inventory, Context(station, StationInputKind.Fuel), new[] { fuel.gameObject.name });
    }

    [HarmonyPatch]
    private static class FindInput
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Smelter), "FindCookableItem");
            yield return AccessTools.Method(typeof(CookingStation), "FindCookableItem");
            yield return AccessTools.Method(typeof(Fermenter), "FindCookableItem");
        }
        private static bool Prefix(Component __instance, Inventory inventory, ref ItemDrop.ItemData? __result)
        {
            var inputs = Plugin.StationItems;
            if (inputs == null || !inputs.Applies(inventory)) return true;
            __result = inputs.Select(inventory, Context(__instance, StationInputKind.Processing), ProcessingInputs(__instance));
            return false;
        }
    }

    // Explicit item use must go through the same policy as automatic selection.
    // Fireplace fireworks are separate gameplay and retain their original path.
    [HarmonyPatch]
    private static class CheckSelectedInput
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Smelter), "OnAddOre");
            yield return AccessTools.Method(typeof(CookingStation), "OnUseItem");
            yield return AccessTools.Method(typeof(Fermenter), "AddItem");
            yield return AccessTools.Method(typeof(Fireplace), "UseItem");
        }
        private static bool Prefix(Component __instance, Humanoid user, ItemDrop.ItemData? item, ref bool __result)
        {
            var inputs = Plugin.StationItems;
            if (inputs == null || !inputs.Applies(user.GetInventory()) || item?.m_dropPrefab == null) return true;
            var kind = __instance is Fireplace ? StationInputKind.Fuel : StationInputKind.Processing;
            if (__instance is Fireplace && item.m_shared.m_name != Fuel(__instance)?.m_itemData.m_shared.m_name) return true;
            if (inputs.Allows(Context(__instance, kind), Items.Id(item))) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class ResourceCalls
    {
        private static readonly Dictionary<MethodInfo, MethodInfo> replacements = new Dictionary<MethodInfo, MethodInfo>
        {
            { AccessTools.Method(typeof(Inventory), "HaveItem", new[] { typeof(string), typeof(bool) }), AccessTools.Method(typeof(StationLoading), nameof(HaveFuel)) },
            { AccessTools.Method(typeof(Inventory), "RemoveItem", new[] { typeof(string), typeof(int), typeof(int), typeof(bool) }), AccessTools.Method(typeof(StationLoading), nameof(RemoveFuel)) },
            { AccessTools.Method(typeof(Inventory), "RemoveItem", new[] { typeof(ItemDrop.ItemData), typeof(int) }), AccessTools.Method(typeof(StationLoading), nameof(RemoveItem)) },
            { AccessTools.Method(typeof(Inventory), "RemoveOneItem", new[] { typeof(ItemDrop.ItemData) }), AccessTools.Method(typeof(StationLoading), nameof(RemoveOne)) },
            { AccessTools.Method(typeof(Inventory), "CountItemsByName", new[] { typeof(string[]), typeof(int), typeof(bool), typeof(bool) }), AccessTools.Method(typeof(StationLoading), nameof(CountInputs)) }
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Smelter), "OnAddOre");
            yield return AccessTools.Method(typeof(Smelter), "OnAddFuel");
            yield return AccessTools.Method(typeof(Smelter), "CanUseItems");
            yield return AccessTools.Method(typeof(CookingStation), "OnAddFuelSwitch");
            yield return AccessTools.Method(typeof(CookingStation), "CookItem");
            yield return AccessTools.Method(typeof(CookingStation), "CanUseItems");
            yield return AccessTools.Method(typeof(Fermenter), "AddItem");
            yield return AccessTools.Method(typeof(Fireplace), "Interact");
            yield return AccessTools.Method(typeof(Fireplace), "UseItem");
            yield return AccessTools.Method(typeof(Fireplace), "CanUseItems");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                    instruction.operand is MethodInfo method && replacements.TryGetValue(method, out var replacement) &&
                    // The string-based removal in UseItem is for fireworks, not fuel.
                    !(__originalMethod.DeclaringType == typeof(Fireplace) && __originalMethod.Name == "UseItem" && replacement.Name == nameof(RemoveFuel)))
                {
                    var instance = new CodeInstruction(OpCodes.Ldarg_0);
                    instruction.MoveLabelsTo(instance);
                    instruction.MoveBlocksTo(instance);
                    yield return instance;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }
    }

    private static bool HaveFuel(Inventory inventory, string name, bool matchWorldLevel, Component station) =>
        Plugin.StationItems?.Applies(inventory) == true ? SelectFuel(inventory, station, name) != null : inventory.HaveItem(name, matchWorldLevel);

    private static void RemoveFuel(Inventory inventory, string name, int amount, int quality, bool worldLevelBased, Component station)
    {
        var inputs = Plugin.StationItems;
        if (inputs == null || !inputs.Applies(inventory)) { inventory.RemoveItem(name, amount, quality, worldLevelBased); return; }
        var item = SelectFuel(inventory, station, name);
        if (item != null) inputs.Remove(inventory, Context(station, StationInputKind.Fuel), item, amount);
    }

    private static bool RemoveItem(Inventory inventory, ItemDrop.ItemData item, int amount, Component station)
    {
        var inputs = Plugin.StationItems;
        if (inputs == null || !inputs.Applies(inventory)) return inventory.RemoveItem(item, amount);
        return inputs.Remove(inventory, Context(station, station is Fireplace ? StationInputKind.Fuel : StationInputKind.Processing), item, amount);
    }

    private static bool RemoveOne(Inventory inventory, ItemDrop.ItemData item, Component station) => RemoveItem(inventory, item, 1, station);

    private static int CountInputs(Inventory inventory, string[] names, int quality, bool matchWorldLevel, bool stacksOnly, Component station)
    {
        var inputs = Plugin.StationItems;
        if (inputs == null || !inputs.Applies(inventory)) return inventory.CountItemsByName(names, quality, matchWorldLevel, stacksOnly);
        // This station call only tests > 0; use the same selection policy as loading.
        return inputs.Select(inventory, Context(station, StationInputKind.Processing), ProcessingInputs(station)) != null ? 1 : 0;
    }
}
