using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SharedInventory;

internal sealed class DatabaseFile
{
    private readonly string path;
    public CountDatabase Database { get; }

    public DatabaseFile(string path)
    {
        this.path = path;
        // Fail closed on corrupt data; never silently replace the inventory with an empty one.
        Database = new CountDatabase(File.Exists(path)
            ? JsonConvert.DeserializeObject<Dictionary<string, long>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Empty shared inventory save: " + path)
            : null);
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
                writer.Write(JsonConvert.SerializeObject(Database.List()));
                writer.Flush();
            }
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
        Database.MarkSaved();
    }
}
