using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Catches the publicizer trap.
//
// ThorsKist compiles against a PUBLICIZED copy of assembly_valheim, where every
// member is public. The game ships the real assembly, where many of them are
// not. The compiler is happy either way; Mono throws MethodAccessException /
// FieldAccessException at the call site the first time the line runs -- which
// may be deep in a feature nobody exercised for weeks.
//
// This reads the MemberRef table of the built plugin, resolves each reference
// against the REAL assembly, and fails on anything that is not public there.
// It is the only check that can see this class of bug without running the game.

internal static class AccessCheck
{
    public static int Run(string modDll, string realAssembly, IEnumerable<string> searchPaths)
    {
        if (!File.Exists(realAssembly))
        {
            Console.WriteLine($"  skip access check: {realAssembly} not present");
            Console.WriteLine("  (pull the real assembly_valheim.dll from a game or dedicated-server install)");
            return 0;
        }

        var paths = searchPaths.ToList();
        paths.Add(Path.GetFullPath(realAssembly));
        var resolver = new SimpleNameResolver(paths.Distinct().ToList());
        using var mlc = new MetadataLoadContext(resolver, "mscorlib");
        Assembly real = mlc.LoadFromAssemblyPath(Path.GetFullPath(realAssembly));
        string realName = real.GetName().Name;

        var bad = new List<string>();
        var checkedCount = 0;

        using var fs = File.OpenRead(modDll);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();

        foreach (MemberReferenceHandle h in md.MemberReferences)
        {
            MemberReference mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;

            TypeReference tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;

            AssemblyReference ar = md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);
            if (!string.Equals(md.GetString(ar.Name), realName, StringComparison.OrdinalIgnoreCase)) continue;

            string ns = md.GetString(tr.Namespace);
            string typeName = md.GetString(tr.Name);
            string fullType = string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName;
            string memberName = md.GetString(mr.Name);

            Type t = real.GetType(fullType, false);
            if (t == null)
            {
                // A nested type reference: walk up through the enclosing types.
                t = ResolveNested(md, real, tr);
                if (t == null)
                {
                    bad.Add($"{fullType}.{memberName}  (type not found in {realName})");
                    continue;
                }
            }

            checkedCount++;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Static | BindingFlags.Instance;

            if (mr.GetKind() == MemberReferenceKind.Field)
            {
                FieldInfo f = t.GetField(memberName, all);
                if (f == null) bad.Add($"{t.Name}.{memberName}  (field missing)");
                else if (!f.IsPublic) bad.Add($"{t.Name}.{memberName}  (field is {Vis(f.Attributes)})");
            }
            else
            {
                var candidates = memberName == ".ctor"
                    ? t.GetConstructors(all).Cast<MethodBase>().ToList()
                    : t.GetMethods(all).Where(m => m.Name == memberName).Cast<MethodBase>().ToList();

                if (candidates.Count == 0)
                {
                    bad.Add($"{t.Name}.{memberName}  (method missing)");
                }
                else if (candidates.All(m => !m.IsPublic))
                {
                    // Every overload of this name is non-public, so whichever one
                    // the compiler bound to, the call will throw.
                    bad.Add($"{t.Name}.{memberName}  (method is {Vis(candidates[0].Attributes)})");
                }
            }
        }

        foreach (string b in bad.Distinct().OrderBy(x => x)) Console.WriteLine("  INACCESSIBLE " + b);
        Console.WriteLine($"\n{checkedCount} reference(s) into {realName} checked, {bad.Distinct().Count()} not public at runtime.");
        return bad.Count == 0 ? 0 : 1;
    }

    private static Type ResolveNested(MetadataReader md, Assembly real, TypeReference tr)
    {
        var chain = new List<string> { md.GetString(tr.Name) };
        EntityHandle scope = tr.ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
        {
            TypeReference outer = md.GetTypeReference((TypeReferenceHandle)scope);
            chain.Insert(0, md.GetString(outer.Name));
            if (outer.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                string ons = md.GetString(outer.Namespace);
                chain[0] = string.IsNullOrEmpty(ons) ? chain[0] : ons + "." + chain[0];
                break;
            }
            scope = outer.ResolutionScope;
        }
        return real.GetType(string.Join("+", chain), false);
    }

    private static string Vis(MethodAttributes a) => (a & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Private => "private",
        MethodAttributes.Assembly => "internal",
        MethodAttributes.Family => "protected",
        MethodAttributes.FamORAssem => "protected internal",
        MethodAttributes.FamANDAssem => "private protected",
        _ => "not public",
    };

    private static string Vis(FieldAttributes a) => (a & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Private => "private",
        FieldAttributes.Assembly => "internal",
        FieldAttributes.Family => "protected",
        FieldAttributes.FamORAssem => "protected internal",
        FieldAttributes.FamANDAssem => "private protected",
        _ => "not public",
    };
}
