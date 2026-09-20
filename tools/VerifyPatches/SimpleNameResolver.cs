using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal sealed class SimpleNameResolver : MetadataAssemblyResolver
{
    private readonly List<string> _paths;
    private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public SimpleNameResolver(List<string> paths) { _paths = paths; }

    public override Assembly Resolve(MetadataLoadContext context, AssemblyName name)
    {
        if (_loaded.TryGetValue(name.Name, out Assembly cached)) return cached;
        foreach (string path in _paths)
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), name.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            Assembly a = context.LoadFromAssemblyPath(path);
            _loaded[name.Name] = a;
            return a;
        }
        // Fall back to a scan of the files' real identities: a publicized
        // assembly keeps its original name but is shipped under a new file
        // name, so file name alone is not enough.
        foreach (string path in _paths)
        {
            Assembly a;
            try { a = context.LoadFromAssemblyPath(path); } catch { continue; }
            if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
            {
                _loaded[name.Name] = a;
                return a;
            }
        }
        return null;
    }
}
