using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// Proves every Harmony patch in ThorsKist.dll names a method that actually
// exists on the Valheim assembly it was built against.
//
// Why this exists: a Harmony annotation is a string and a Type, resolved at
// runtime. A misspelled method, a signature the game changed, or an ambiguous
// overload all compile perfectly and then fail silently in-game -- the plugin
// loads, logs one line nobody reads, and simply does nothing. A green build is
// not evidence that any patch will bind. This reads the emitted attributes and
// resolves each target for real.
//
// Usage: VerifyPatches <ThorsKist.dll> <lib-dir>

internal static class Program
{
    private static readonly List<string> Errors = new();
    private static readonly List<string> Ok = new();

    private static int Main(string[] args)
    {
        string dll = args.Length > 0 ? args[0] : "bin/Release/net472/ThorsKist.dll";
        string libDir = args.Length > 1 ? args[1] : "lib";
        // The REAL, unpublicized game assembly. Optional, because it cannot be
        // redistributed -- but without it the publicizer trap goes unchecked.
        string realAssembly = args.Length > 2 ? args[2] : Path.Combine(libDir, "..", ".refs", "assembly_valheim.dll");

        if (!File.Exists(dll)) { Console.WriteLine($"ERROR: {dll} not found"); return 1; }
        if (!Directory.Exists(libDir)) { Console.WriteLine($"ERROR: {libDir} not found"); return 1; }

        var paths = new List<string> { Path.GetFullPath(dll) };
        paths.AddRange(Directory.GetFiles(libDir, "*.dll").Select(Path.GetFullPath));
        // The net472 reference assemblies, so mscorlib and friends resolve.
        string refPack = FindReferenceAssemblies();
        if (refPack != null) paths.AddRange(Directory.GetFiles(refPack, "*.dll"));

        // Matching on the simple name alone: the vendored game assemblies carry
        // version 0.0.0.0 and the stock resolver refuses those outright.
        var resolver = new SimpleNameResolver(paths.Distinct().ToList());
        using var mlc = new MetadataLoadContext(resolver, "mscorlib");

        Assembly mod = mlc.LoadFromAssemblyPath(Path.GetFullPath(dll));

        foreach (Type type in mod.GetTypes())
        {
            var classTargets = ReadPatchAttributes(type.GetCustomAttributesData());

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Static | BindingFlags.Instance |
                                                          BindingFlags.DeclaredOnly))
            {
                var data = method.GetCustomAttributesData();
                bool isPatchMethod = data.Any(a => a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix"
                                                       or "HarmonyTranspiler" or "HarmonyFinalizer");
                if (!isPatchMethod) continue;

                var targets = ReadPatchAttributes(data);
                var merged = Merge(classTargets, targets);
                Verify(mlc, $"{type.Name}.{method.Name}", merged);
            }
        }

        foreach (string s in Ok) Console.WriteLine("  ok   " + s);
        foreach (string s in Errors) Console.WriteLine("  FAIL " + s);

        if (Ok.Count == 0 && Errors.Count == 0)
        {
            Console.WriteLine("ERROR: no Harmony patches found at all -- the verifier is not seeing the mod.");
            return 1;
        }
        Console.WriteLine($"\n{Ok.Count} patch target(s) resolved, {Errors.Count} broken.");

        Console.WriteLine("\n-- runtime accessibility --");
        int accessFailures = AccessCheck.Run(Path.GetFullPath(dll), realAssembly, paths);

        return Errors.Count == 0 && accessFailures == 0 ? 0 : 1;
    }

    private sealed class PatchSpec
    {
        public string TypeName;
        public string MethodName;
        public List<string> ArgTypes;   // null = do not filter by signature
        public bool IsConstructor;
    }

    private static PatchSpec ReadPatchAttributes(IEnumerable<CustomAttributeData> attrs)
    {
        var spec = new PatchSpec();
        foreach (CustomAttributeData a in attrs)
        {
            if (a.AttributeType.Name != "HarmonyPatch") continue;
            foreach (CustomAttributeTypedArgument arg in a.ConstructorArguments)
            {
                string argTypeName = arg.ArgumentType.Name;
                if (argTypeName == "Type" && arg.Value is Type t) spec.TypeName ??= t.FullName;
                else if (argTypeName == "String" && arg.Value is string s) spec.MethodName ??= s;
                // HarmonyLib.MethodType: Normal=0, Getter=1, Setter=2, Constructor=3, StaticConstructor=4
                else if (argTypeName == "MethodType") spec.IsConstructor = Convert.ToInt32(arg.Value) is 3 or 4;
                else if (argTypeName == "Type[]" &&
                         arg.Value is System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument> list)
                {
                    spec.ArgTypes = list.Select(x => ((Type)x.Value).FullName).ToList();
                }
            }
        }
        return spec;
    }

    private static PatchSpec Merge(PatchSpec cls, PatchSpec method) => new()
    {
        TypeName = method.TypeName ?? cls.TypeName,
        MethodName = method.MethodName ?? cls.MethodName,
        ArgTypes = method.ArgTypes ?? cls.ArgTypes,
        IsConstructor = method.IsConstructor || cls.IsConstructor,
    };

    private static void Verify(MetadataLoadContext mlc, string label, PatchSpec spec)
    {
        if (spec.TypeName == null)
        {
            Errors.Add($"{label}: no target type named in any HarmonyPatch attribute");
            return;
        }

        Type target = mlc.GetAssemblies()
            .Select(a => a.GetType(spec.TypeName, false))
            .FirstOrDefault(t => t != null);

        if (target == null)
        {
            Errors.Add($"{label}: type '{spec.TypeName}' not found in any referenced assembly");
            return;
        }

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Static | BindingFlags.Instance;

        if (spec.IsConstructor)
        {
            var ctors = target.GetConstructors(all)
                .Where(c => spec.ArgTypes == null || Matches(c.GetParameters(), spec.ArgTypes)).ToList();
            if (ctors.Count == 1) Ok.Add($"{label} -> {spec.TypeName}..ctor");
            else Errors.Add($"{label}: {ctors.Count} constructor(s) matched on {spec.TypeName}");
            return;
        }

        if (spec.MethodName == null)
        {
            Errors.Add($"{label}: no target method name");
            return;
        }

        var candidates = target.GetMethods(all).Where(m => m.Name == spec.MethodName).ToList();
        if (candidates.Count == 0)
        {
            Errors.Add($"{label}: {spec.TypeName} has no method '{spec.MethodName}'");
            return;
        }

        if (spec.ArgTypes != null)
            candidates = candidates.Where(m => Matches(m.GetParameters(), spec.ArgTypes)).ToList();

        if (candidates.Count == 1)
        {
            Ok.Add($"{label} -> {spec.TypeName}.{spec.MethodName}");
        }
        else if (candidates.Count == 0)
        {
            Errors.Add($"{label}: no overload of {spec.TypeName}.{spec.MethodName} matches the declared argument types");
        }
        else
        {
            // Harmony throws on an ambiguous target rather than guessing, so an
            // unqualified patch of an overloaded method is a real defect.
            Errors.Add($"{label}: {candidates.Count} overloads of {spec.TypeName}.{spec.MethodName} " +
                       "-- the patch must name argument types");
        }
    }

    private static bool Matches(ParameterInfo[] ps, List<string> wanted)
    {
        if (ps.Length != wanted.Count) return false;
        for (int i = 0; i < ps.Length; i++)
            if (ps[i].ParameterType.FullName != wanted[i]) return false;
        return true;
    }

    private static string FindReferenceAssemblies()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root = Path.Combine(home, ".nuget", "packages", "microsoft.netframework.referenceassemblies.net472");
        if (!Directory.Exists(root)) return null;
        foreach (string version in Directory.GetDirectories(root))
        {
            string dir = Path.Combine(version, "build", ".NETFramework", "v4.7.2");
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }
}
