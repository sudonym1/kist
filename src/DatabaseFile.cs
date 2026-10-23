using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharedInventory;

internal sealed class DatabaseFile
{
    private readonly string path;
    public CountDatabase Database { get; }

    public DatabaseFile(string path)
    {
        this.path = path;
        // Fail closed on corrupt data; never silently replace the inventory with an empty one.
        var counts = new Dictionary<string, long>();
        var qualities = new Dictionary<string, int>();
        if (File.Exists(path))
        {
            var entries = JObject.Parse(File.ReadAllText(path));
            foreach (var entry in entries.Properties())
            {
                if (entry.Value.Type == JTokenType.Integer)
                {
                    counts.Add(entry.Name, entry.Value.Value<long>());
                    qualities.Add(entry.Name, 1);
                }
                else if (entry.Value is JObject value && value["Count"]?.Type == JTokenType.Integer
                    && value["Quality"]?.Type == JTokenType.Integer)
                {
                    counts.Add(entry.Name, value["Count"]!.Value<long>());
                    qualities.Add(entry.Name, value["Quality"]!.Value<int>());
                }
                else throw new InvalidDataException("Invalid shared inventory entry: " + entry.Name);
            }
        }
        Database = new CountDatabase(counts, qualities);
    }

    public void Save()
    {
        if (!Database.Dirty) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 4096, true))
            {
                var entries = new JObject();
                foreach (var entry in Database.List())
                    entries.Add(entry.Key, new JObject { ["Count"] = entry.Value, ["Quality"] = Database.Quality(entry.Key) });
                writer.Write(entries.ToString(Formatting.None));
                writer.Flush();
            }
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
        Database.MarkSaved();
    }
}
