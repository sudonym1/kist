using System;
using Microsoft.Data.Sqlite;

namespace ThorsKist.Sync.Sqlite
{
    // Shared production/probe configuration. Caller owns the connection and must
    // execute database work off the game thread.
    public static class SqliteConnections
    {
        private static readonly object ProviderLock = new object();
        private static bool initialized;
        private static void Initialize()
        {
            lock (ProviderLock)
            {
                if (initialized) return;
                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                SQLitePCL.raw.FreezeProvider();
                initialized = true;
            }
        }
        public static SqliteConnection Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == ":memory:") throw new ArgumentException("File path required", nameof(path));
            Initialize();
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            try
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA journal_mode=WAL;";
                    if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "wal", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("SQLite did not enable WAL");
                    command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                    command.ExecuteNonQuery();
                }
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }
    }
}
