using System;
using System.IO;
using ThorsKist.Sync.Sqlite;

// Run the unmodified build output on .NET Framework (Windows) or Mono (Linux).
// A modern .NET harness alone cannot establish compatibility with Valheim.
static class Program
{
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error)
        {
            Console.Error.WriteLine("SQLite runtime probe failed: " + error);
            return 1;
        }
    }
    static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "thorskist-runtime-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using (var db = SqliteConnections.Open(Path.Combine(directory, "probe.db")))
            using (var query = db.CreateCommand())
            {
                query.CommandText = "SELECT sqlite_version();";
                Console.WriteLine("net472 provider loaded: SQLite " + query.ExecuteScalar() + ", CLR " + Environment.Version);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
