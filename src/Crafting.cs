using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SharedInventory;

// Replace calls only inside Valheim's resource paths. Inventory itself, chests,
// item discovery, and unrelated gameplay continue to see physical items only.
[HarmonyPatch]
internal static class Crafting
{
    private static readonly Dictionary<MethodInfo, MethodInfo> replacements = new Dictionary<MethodInfo, MethodInfo>
    {
        { AccessTools.Method(typeof(Inventory), "CountItems", new[] { typeof(string), typeof(int), typeof(bool) }), AccessTools.Method(typeof(Crafting), nameof(Count)) },
        { AccessTools.Method(typeof(Inventory), "HaveItem", new[] { typeof(string), typeof(bool) }), AccessTools.Method(typeof(Crafting), nameof(Have)) },
        { AccessTools.Method(typeof(Inventory), "GetItem", new[] { typeof(string), typeof(int), typeof(bool) }), AccessTools.Method(typeof(Crafting), nameof(Get)) },
        { AccessTools.Method(typeof(Inventory), "RemoveItem", new[] { typeof(string), typeof(int), typeof(int), typeof(bool) }), AccessTools.Method(typeof(Crafting), nameof(Remove)) }
    };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Player), "HaveRequirementItems");
        yield return AccessTools.Method(typeof(Player), "GetFirstRequiredItem");
        yield return AccessTools.Method(typeof(Player), "HaveRequirements", new[] { typeof(Piece), typeof(Player.RequirementMode) });
        yield return AccessTools.Method(typeof(Player), "ConsumeResources");
        yield return AccessTools.Method(typeof(InventoryGui), "SetupRequirement");
        yield return AccessTools.Method(typeof(InventoryGui), "DoCrafting");
    }

    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo method && replacements.TryGetValue(method, out var replacement))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
    }

    private static int Count(Inventory inventory, string name, int quality, bool matchWorldLevel) =>
        Plugin.Resources?.Count(inventory, name, quality, matchWorldLevel) ?? inventory.CountItems(name, quality, matchWorldLevel);
    private static bool Have(Inventory inventory, string name, bool matchWorldLevel) =>
        Plugin.Resources?.Have(inventory, name, matchWorldLevel) ?? inventory.HaveItem(name, matchWorldLevel);
    private static ItemDrop.ItemData? Get(Inventory inventory, string name, int quality, bool isPrefabName) =>
        Plugin.Resources != null ? Plugin.Resources.Get(inventory, name, quality, isPrefabName) : inventory.GetItem(name, quality, isPrefabName);
    private static void Remove(Inventory inventory, string name, int amount, int quality, bool worldLevelBased)
    {
        if (Plugin.Resources != null) Plugin.Resources.Remove(inventory, name, amount, quality, worldLevelBased);
        else inventory.RemoveItem(name, amount, quality, worldLevelBased);
    }

    [HarmonyPatch(typeof(Player), "ConsumeResources")]
    private static class BatchResources
    {
        private static void Prefix(out IDisposable? __state) => __state = Plugin.Resources?.Begin();
        private static void Finalizer(IDisposable? __state) => __state?.Dispose();
    }

    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    private static class BatchCraft
    {
        private static void Prefix(out IDisposable? __state) => __state = Plugin.Resources?.Begin();
        private static void Finalizer(IDisposable? __state) => __state?.Dispose();
    }
}
