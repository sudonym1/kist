using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class PatchChecks
{
    private static void CheckRitualHooks(AssemblyDefinition game)
    {
        foreach (var type in new[] { "RuneStone", "ItemStand" })
        {
            var control = game.MainModule.Types.Single(t => t.Name == type);
            var interact = control.Methods.Single(m => m.Name == "Interact");
            if (interact.ReturnType.FullName != "System.Boolean" ||
                !interact.Parameters.Select(p => p.ParameterType.Name)
                    .SequenceEqual(new[] { "Humanoid", "Boolean", "Boolean" }) ||
                control.Methods.Single(m => m.Name == "GetHoverText").ReturnType.FullName != "System.String")
                throw new Exception("Ritual stone hook signature changed: " + type);
        }
        var location = game.MainModule.Types.Single(t => t.Name == "Location");
        var player = game.MainModule.Types.Single(t => t.Name == "Player");
        var death = player.Methods.Single(m => m.Name == "OnDeath");
        if (game.MainModule.Types.Single(t => t.Name == "Character").Fields.Single(f => f.Name == "m_lastHit").FieldType.Name != "HitData")
            throw new Exception("Ritual death attribution field changed");
        if (!death.IsPublic || !death.IsVirtual || death.Parameters.Count != 0 || death.ReturnType.FullName != "System.Void" ||
            !death.Body.Instructions.Any(i => i.Operand is MethodReference method && method.Name == "CreateTombStone"))
            throw new Exception("Ritual owner-side normal death path changed");
        foreach (var name in new[] { "Awake", "OnDestroy" })
            if (location.Methods.Single(m => m.Name == name).Parameters.Count != 0)
                throw new Exception("Original-site lifecycle hook signature changed");
        System.Console.WriteLine("PASS sacrificial-stone reading/trophy and site lifecycle hook signatures");
    }
    private static void CheckChestRadius(AssemblyDefinition game)
    {
        var player = game.MainModule.Types.Single(type => type.Name == "Player");
        if (player.Fields.Single(field => field.Name == "m_placementGhost").FieldType.FullName != "UnityEngine.GameObject")
            throw new Exception("Chest radius placement-ghost field changed");
        System.Console.WriteLine("PASS chest radius placement-ghost reflection signature");
    }
    private static void CheckChestConstruction(AssemblyDefinition mod, AssemblyDefinition game)
    {
        var player = game.MainModule.Types.Single(type => type.Name == "Player");
        var attempt = player.Methods.Single(method => method.Name == "TryPlacePiece");
        var placementCalls = attempt.Body.Instructions.Where(i => i.Operand is MethodReference method &&
            method.DeclaringType.Name == "Player" && method.Name == "PlacePiece").ToArray();
        if (placementCalls.Length != 1 || placementCalls[0].Next.OpCode.Code != Mono.Cecil.Cil.Code.Ldc_I4_1 ||
            placementCalls[0].Next.Next.OpCode.Code != Mono.Cecil.Cil.Code.Ret)
            throw new Exception("Chest placement success-return hook changed");
        var original = (MethodReference)placementCalls[0].Operand;
        var construction = mod.MainModule.Types.Single(type => type.FullName == "SharedInventory.ChestConstruction");
        var wrapper = construction.Methods.Single(method => method.Name == "Place");
        if (wrapper.ReturnType.FullName != "System.Boolean" || !wrapper.Parameters.Select(p => p.ParameterType.FullName)
                .SequenceEqual(new[] { original.DeclaringType.FullName }.Concat(original.Parameters.Select(p => p.ParameterType.FullName))))
            throw new Exception("Chest placement replacement signature changed");
        var actualPlacement = player.Methods.Single(method => method.Name == "PlacePiece");
        if (!actualPlacement.Body.Instructions.Any(i => i.Operand is MethodReference method &&
                method.DeclaringType.Name == "IPlaced" && method.Name == "OnPlaced") ||
            !mod.MainModule.Types.Single(type => type.FullName == "SharedInventory.SharedChest").Interfaces.Any(i => i.InterfaceType.Name == "IPlaced"))
            throw new Exception("Chest binding placement callback changed");
        var removal = game.MainModule.Types.Single(type => type.Name == "WearNTear").Methods.Single(method => method.Name == "RPC_Remove");
        if (!removal.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Int64", "System.Boolean" }) ||
            !removal.Body.Instructions.Any(i => i.Operand is MethodReference method && method.Name == "Destroy"))
            throw new Exception("Owner-side hammer refund hook changed");
        var item = game.MainModule.Types.Single(type => type.Name == "ItemDrop").NestedTypes.Single(type => type.Name == "ItemData");
        foreach (var methodName in new[] { "Save", "Load", "Clone" })
            if (!item.Methods.Where(m => m.Name == methodName && m.HasBody).Any(m => m.Body.Instructions.Any(i =>
                    i.Operand is FieldReference field && field.Name == "m_customData")))
                throw new Exception("Item-local binding persistence hook changed: " + methodName);
        System.Console.WriteLine("PASS chest placement, IPlaced binding, owner hammer refund and item metadata persistence signatures");
    }
    public static int CheckHarness(string path, bool expected)
    {
        using var mod = AssemblyDefinition.ReadAssembly(path);
        var types = AllTypes(mod.MainModule.Types).ToArray();
        foreach (var name in new[] { "TestSettings", "TestHarness", "LabMetrics", "TestPanel", "AssetCapture", "ContentAcceptance", "AccessAcceptance", "RitualAcceptance", "RitualTestControls", "RitualTestAction", "RitualOutcomeAcceptance" })
        {
            var present = types.Any(type => type.FullName == "SharedInventory." + name);
            if (present != expected) throw new Exception("Test harness inclusion mismatch: " + name);
        }
        var literals = types.SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions).Select(instruction => instruction.Operand).OfType<string>().ToList();
        foreach (var literal in new[] { "Reset cooldown", "Spawn essence", "Test lightning", "SharedInventory.Ritual.Test.v1.Request" })
            if (literals.Contains(literal) != expected) throw new Exception("Ritual test UI/endpoint isolation mismatch: " + literal);
        System.Console.WriteLine(expected ? "PASS test harness included in explicitly opted-in build" : "PASS production DLL contains no test harness types");
        return 0;
    }
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }
    public static int CheckRelease(string path, string manifestPath)
    {
        CheckHarness(path, false);
        using var mod = AssemblyDefinition.ReadAssembly(path);
        var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifestPath));
        var version = (string?)manifest["version_number"];
        var plugin = mod.MainModule.Types.Single(type => type.FullName == "SharedInventory.Plugin");
        var identity = plugin.CustomAttributes.Single(attribute => attribute.AttributeType.FullName == "BepInEx.BepInPlugin");
        if ((string)identity.ConstructorArguments[0].Value != "local.sharedinventory" ||
            (string)identity.ConstructorArguments[1].Value != "Einherjar Chest" ||
            (string)identity.ConstructorArguments[2].Value != version ||
            mod.Name.Version != new System.Version(version + ".0"))
            throw new Exception("Release manifest, assembly version and plugin identity must agree");
        if (!plugin.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "BepInEx.BepInIncompatibility" &&
                (string)attribute.ConstructorArguments[0].Value == "posixone_ThorsKist"))
            throw new Exception("Release must declare the old Thor's Kist plugin incompatible");
        var types = AllTypes(mod.MainModule.Types).ToArray();
        if (types.SelectMany(type => type.Fields).Any(field => field.Name == "TestingPanel" || field.Name == "testingTab"))
            throw new Exception("Production window still contains Testing menu state");
        System.Console.WriteLine("PASS production release identity, version and Thor's Kist incompatibility");
        return 0;
    }
    // Inspect actual game IL and replacement signatures without running Unity
    // or the game's Mono-targeted Harmony inside the .NET 8 test runner.
    public static int Run(string modPath, string corePath, string managedPath)
    {
        using var mod = AssemblyDefinition.ReadAssembly(modPath);
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(managedPath, "assembly_valheim.dll"));
        using var utils = AssemblyDefinition.ReadAssembly(Path.Combine(managedPath, "assembly_utils.dll"));
        CheckWindowInput(mod, utils);
        CheckChestRadius(game);
        CheckChestConstruction(mod, game);
        CheckRitualHooks(game);
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

    private static void CheckWindowInput(AssemblyDefinition mod, AssemblyDefinition utils)
    {
        var input = utils.MainModule.Types.Single(type => type.Name == "ZInput");
        var plugin = mod.MainModule.Types.Single(type => type.Name == "Plugin");
        foreach (var name in new[] { "GetKeyDown", "GetKeyUp", "GetButton", "GetButtonDown", "GetButtonUp" })
        {
            var target = input.Methods.Single(method => method.Name == name);
            var hook = plugin.NestedTypes.Single(type => type.Name ==
                (name.StartsWith("GetKey", StringComparison.Ordinal) ? "BlockWindowKeys" : "BlockWindowShortcuts"));
            var prefix = hook.Methods.Single(method => method.Name == "Prefix");
            if (!target.IsStatic || target.ReturnType.FullName != "System.Boolean" ||
                prefix.ReturnType.FullName != "System.Boolean" ||
                prefix.Parameters.Last().Name != "__result" ||
                prefix.Parameters.Last().ParameterType.FullName != "System.Boolean&" ||
                !hook.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "HarmonyLib.HarmonyPatch") ||
                !hook.Methods.Single(method => method.Name == "TargetMethods").Body.Instructions
                    .Any(instruction => instruction.Operand is string methodName && methodName == name))
                throw new Exception("Invalid window input hook: " + name);
            if (name.StartsWith("GetKey", StringComparison.Ordinal) &&
                (target.Parameters[0].Name != prefix.Parameters[0].Name ||
                 target.Parameters[0].ParameterType.FullName != prefix.Parameters[0].ParameterType.FullName))
                throw new Exception("Invalid window key hook signature: " + name);
            System.Console.WriteLine($"PASS ZInput.{name}: window input hook matches game input signature");
        }
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
