using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class PatchChecks
{
    // Inspect actual game IL and replacement signatures without running Unity
    // or the game's Mono-targeted Harmony inside the .NET 8 test runner.
    public static int Run(string modPath, string corePath, string managedPath)
    {
        using var mod = AssemblyDefinition.ReadAssembly(modPath);
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(managedPath, "assembly_valheim.dll"));
        using var utils = AssemblyDefinition.ReadAssembly(Path.Combine(managedPath, "assembly_utils.dll"));
        var escapeTarget = utils.MainModule.Types.Single(type => type.Name == "ZInput")
            .Methods.Single(method => method.Name == "GetKeyDown");
        var escapePrefix = mod.MainModule.Types.Single(type => type.Name == "Plugin")
            .NestedTypes.Single(type => type.Name == "ConsumeWindowEscape")
            .Methods.Single(method => method.Name == "Prefix");
        if (!escapeTarget.IsStatic || escapeTarget.ReturnType.FullName != "System.Boolean" ||
            escapeTarget.Parameters[0].Name != escapePrefix.Parameters[0].Name ||
            escapeTarget.Parameters[0].ParameterType.FullName != escapePrefix.Parameters[0].ParameterType.FullName ||
            escapePrefix.Parameters[1].Name != "__result" ||
            escapePrefix.Parameters[1].ParameterType.FullName != "System.Boolean&")
            throw new Exception("Invalid Escape input hook signature");
        System.Console.WriteLine("PASS ZInput.GetKeyDown: Escape hook matches game input signature");
        var hooks = mod.MainModule.Types.Single(type => type.FullName == "SharedInventory.Crafting");
        var replacements = new Dictionary<string, string>
        {
            ["CountItems"] = "Count", ["HaveItem"] = "Have", ["GetItem"] = "Get", ["RemoveItem"] = "Remove"
        };
        var targets = new[]
        {
            ("Player", "HaveRequirementItems", 1), ("Player", "GetFirstRequiredItem", 2),
            ("Player", "HaveRequirements", 2), ("Player", "ConsumeResources", 1),
            ("InventoryGui", "SetupRequirement", 1), ("InventoryGui", "DoCrafting", 1)
        };
        foreach (var (typeName, methodName, expected) in targets)
        {
            var type = game.MainModule.Types.Single(type => type.Name == typeName);
            var target = type.Methods.Single(method => method.Name == methodName &&
                (methodName != "HaveRequirements" || method.Parameters[0].ParameterType.Name == "Piece"));
            var changed = 0;
            foreach (var instruction in target.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference original || original.DeclaringType.Name != "Inventory" ||
                    original.Parameters.Count == 0 || original.Parameters[0].ParameterType.FullName != "System.String" ||
                    !replacements.TryGetValue(original.Name, out var wrapperName)) continue;
                var wrapper = hooks.Methods.Single(method => method.Name == wrapperName);
                var wanted = new[] { original.DeclaringType.FullName }.Concat(original.Parameters.Select(parameter => parameter.ParameterType.FullName));
                if (wrapper.ReturnType.FullName != original.ReturnType.FullName ||
                    !wanted.SequenceEqual(wrapper.Parameters.Select(parameter => parameter.ParameterType.FullName)))
                    throw new Exception("Invalid resource hook signature: " + wrapper.FullName);
                changed++;
            }
            if (changed != expected) throw new Exception($"Unexpected resource hook count in {target.FullName}: {changed}");
            System.Console.WriteLine($"PASS {typeName}.{methodName}: {changed} game resource calls match replacement signatures");
        }
        CheckStationHooks(mod, game);
        CheckStationShortcuts(mod, game);
        return 0;
    }

    private static void CheckStationHooks(AssemblyDefinition mod, AssemblyDefinition game)
    {
        var hooks = mod.MainModule.Types.Single(type => type.FullName == "SharedInventory.StationLoading");
        var targets = new[]
        {
            ("Smelter", "OnAddOre", 1), ("Smelter", "OnAddFuel", 2), ("Smelter", "CanUseItems", 2),
            ("CookingStation", "OnAddFuelSwitch", 2), ("CookingStation", "CookItem", 1), ("CookingStation", "CanUseItems", 1),
            ("Fermenter", "AddItem", 1), ("Fireplace", "Interact", 2), ("Fireplace", "UseItem", 1), ("Fireplace", "CanUseItems", 1)
        };
        foreach (var (typeName, methodName, expected) in targets)
        {
            var target = game.MainModule.Types.Single(type => type.Name == typeName).Methods.Single(method => method.Name == methodName);
            var changed = 0;
            foreach (var instruction in target.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference original || original.DeclaringType.Name != "Inventory") continue;
                string? wrapperName = null;
                switch (original.Name)
                {
                    case "HaveItem": wrapperName = "HaveFuel"; break;
                    case "CountItemsByName": wrapperName = "CountInputs"; break;
                    case "RemoveOneItem": wrapperName = "RemoveOne"; break;
                    case "RemoveItem":
                        if (original.Parameters[0].ParameterType.Name == "ItemData") wrapperName = "RemoveItem";
                        else if (!(typeName == "Fireplace" && methodName == "UseItem")) wrapperName = "RemoveFuel";
                        break;
                }
                if (wrapperName == null) continue;
                var wrapper = hooks.Methods.Single(method => method.Name == wrapperName);
                var wanted = new[] { original.DeclaringType.FullName }
                    .Concat(original.Parameters.Select(parameter => parameter.ParameterType.FullName)).Append("UnityEngine.Component");
                if (wrapper.ReturnType.FullName != original.ReturnType.FullName ||
                    !wanted.SequenceEqual(wrapper.Parameters.Select(parameter => parameter.ParameterType.FullName)))
                    throw new Exception("Invalid station resource hook signature: " + wrapper.FullName);
                changed++;
            }
            if (changed != expected) throw new Exception($"Unexpected station resource hook count in {target.FullName}: {changed}");
            System.Console.WriteLine($"PASS {typeName}.{methodName}: {changed} station calls match replacement signatures");
        }
        // Prefix parameters rely on the actual private game's parameter names.
        foreach (var typeName in new[] { "Smelter", "CookingStation", "Fermenter" })
        {
            var target = game.MainModule.Types.Single(type => type.Name == typeName).Methods.Single(method => method.Name == "FindCookableItem");
            if (target.Parameters.Count != 1 || target.Parameters[0].Name != "inventory" ||
                target.Parameters[0].ParameterType.FullName != "Inventory" || target.ReturnType.Name != "ItemData")
                throw new Exception("Invalid station selection hook: " + target.FullName);
        }
        foreach (var (typeName, methodName) in new[] { ("Smelter", "OnAddOre"), ("CookingStation", "OnUseItem"), ("Fermenter", "AddItem"), ("Fireplace", "UseItem") })
        {
            var target = game.MainModule.Types.Single(type => type.Name == typeName).Methods.Single(method => method.Name == methodName);
            if (!target.Parameters.Any(parameter => parameter.Name == "user" && parameter.ParameterType.FullName == "Humanoid") ||
                !target.Parameters.Any(parameter => parameter.Name == "item" && parameter.ParameterType.Name == "ItemData") ||
                target.ReturnType.FullName != "System.Boolean") throw new Exception("Invalid station policy hook: " + target.FullName);
        }
        System.Console.WriteLine("PASS station selection and explicit-input policy prefix signatures");
    }

    private static void CheckStationShortcuts(AssemblyDefinition mod, AssemblyDefinition game)
    {
        var player = game.MainModule.Types.Single(type => type.Name == "Player");
        var update = player.Methods.Single(method => method.Name == "Update");
        var hotbar = player.Methods.Single(method => method.Name == "UseHotbarItem");
        var input = player.Methods.Single(method => method.Name == "TakeInput");
        var animate = game.MainModule.Types.Single(type => type.Name == "Humanoid").Methods.Single(method => method.Name == "DoInteractAnimation");
        if (update.IsStatic || update.Parameters.Count != 0 || update.ReturnType.FullName != "System.Void" ||
            hotbar.IsStatic || hotbar.Parameters.Count != 1 || hotbar.Parameters[0].ParameterType.FullName != "System.Int32" ||
            input.IsStatic || input.Parameters.Count != 0 || input.ReturnType.FullName != "System.Boolean" ||
            animate.IsStatic || animate.Parameters.Count != 1 || animate.Parameters[0].ParameterType.FullName != "UnityEngine.GameObject")
            throw new Exception("Station shortcut input or animation signature changed");
        foreach (var typeName in new[] { "Switch", "CookingStation", "Fermenter", "Fireplace" })
        {
            var target = game.MainModule.Types.Single(type => type.Name == typeName).Methods.Single(method => method.Name == "GetHoverText");
            if (target.IsStatic || target.Parameters.Count != 0 || target.ReturnType.FullName != "System.String")
                throw new Exception("Invalid station hover text hook: " + target.FullName);
        }
        var shortcutUpdate = mod.MainModule.Types.Single(type => type.FullName == "SharedInventory.StationShortcuts")
            .Methods.Single(method => method.Name == "Update");
        var calls = shortcutUpdate.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (MethodReference)instruction.Operand).ToList();
        if (!calls.Any(method => method.DeclaringType.FullName == "Interactable" && method.Name == "UseItem") ||
            calls.Any(method => method.DeclaringType.FullName == "Humanoid" && method.Name == "UseItem"))
            throw new Exception("Station shortcuts must use the station directly without personal item consumption fallback");
        System.Console.WriteLine("PASS station shortcut input, hover text, animation, and direct station-use signatures");
    }
}
