using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ThorsKist.Sync;

namespace ThorsKist.Sync.Sqlite
{
    public sealed partial class SqliteJournalStorage
    {
        private static void CreateProtocolRows(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, @"
CREATE TABLE replication_sessions (vault_id INTEGER NOT NULL REFERENCES vaults(id), session_id TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(vault_id,session_id));
CREATE TABLE replication_settlements (vault_id INTEGER NOT NULL REFERENCES vaults(id), operation_id TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(vault_id,operation_id));");

        // Include lengths and every retained row in the root checksum. Removing a
        // closed session or settlement must fail recovery, not silently forget a retry.
        private static string ProtocolHash(byte[] root, IEnumerable<byte[]> sessions, IEnumerable<byte[]> settlements)
        {
            using (var hash = SHA256.Create())
            {
                void Part(byte[] bytes)
                {
                    var size = BitConverter.GetBytes(bytes.Length);
                    hash.TransformBlock(size, 0, size.Length, size, 0);
                    hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                }
                Part(root);
                foreach (var row in sessions) Part(row);
                Part(Array.Empty<byte>()); // Separate row categories.
                foreach (var row in settlements) Part(row);
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(hash.Hash!).Replace("-", "");
            }
        }
        private static void WriteProtocol(SqliteConnection db, SqliteTransaction tx, long id, ReplicationState state)
        {
            var root = StorageEncoding.Encode(new ReplicationState(state.Revision, state.NextHeartbeat,
                Array.Empty<ReplicationSession>(), Array.Empty<EffectSettlement>()), StorageEncoding.Replication);
            var sessions = state.Sessions.OrderBy(s => s.Address.Session.ToString("N"), StringComparer.Ordinal).Select(s =>
                (Key: s.Address.Session.ToString("N"), Body: StorageEncoding.Encode(new ReplicationState(0, DateTimeOffset.MinValue,
                    new[] { s }, Array.Empty<EffectSettlement>()), StorageEncoding.Replication))).ToArray();
            var settlements = state.Settlements.OrderBy(s => s.Operation.ToString("N"), StringComparer.Ordinal).Select(s =>
                (Key: s.Operation.ToString("N"), Body: StorageEncoding.Encode(new ReplicationState(0, DateTimeOffset.MinValue,
                    Array.Empty<ReplicationSession>(), new[] { s }), StorageEncoding.Replication))).ToArray();
            Execute(db, tx, "DELETE FROM replication_sessions WHERE vault_id=$id;", ("$id", id));
            Execute(db, tx, "DELETE FROM replication_settlements WHERE vault_id=$id;", ("$id", id));
            foreach (var row in sessions) Execute(db, tx, "INSERT INTO replication_sessions VALUES($id,$key,$body);", ("$id", id), ("$key", row.Key), ("$body", row.Body));
            foreach (var row in settlements) Execute(db, tx, "INSERT INTO replication_settlements VALUES($id,$key,$body);", ("$id", id), ("$key", row.Key), ("$body", row.Body));
            Execute(db, tx, "INSERT INTO replication VALUES($id,$body,$hash) ON CONFLICT(vault_id) DO UPDATE SET body=excluded.body,checksum=excluded.checksum;",
                ("$id", id), ("$body", root), ("$hash", ProtocolHash(root, sessions.Select(s => s.Body), settlements.Select(s => s.Body))));
        }
        private static ReplicationState ReadProtocol(SqliteConnection db, SqliteTransaction tx, long id)
        {
            byte[] root; string checksum;
            using (var q = Query(db, tx, "SELECT body,checksum FROM replication WHERE vault_id=$id;", ("$id", id)))
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidDataException("Missing replication state");
                root = (byte[])r[0]; checksum = r.GetString(1);
            }
            var index = StorageEncoding.Decode(root, StorageEncoding.Replication);
            if (index.Sessions.Count != 0 || index.Settlements.Count != 0) throw new InvalidDataException("Invalid replication root");
            var sessions = new List<ReplicationSession>(); var settlements = new List<EffectSettlement>();
            var sessionRows = new List<byte[]>(); var settlementRows = new List<byte[]>();
            using (var q = Query(db, tx, "SELECT session_id,body FROM replication_sessions WHERE vault_id=$id ORDER BY session_id;", ("$id", id)))
            using (var r = q.ExecuteReader()) while (r.Read())
            {
                var bytes = (byte[])r[1]; var row = StorageEncoding.Decode(bytes, StorageEncoding.Replication);
                if (row.Revision != 0 || row.NextHeartbeat != DateTimeOffset.MinValue || row.Sessions.Count != 1 || row.Settlements.Count != 0
                    || row.Sessions[0].Address.Session.ToString("N") != r.GetString(0)) throw new InvalidDataException("Invalid session row");
                sessions.Add(row.Sessions[0]); sessionRows.Add(bytes);
            }
            using (var q = Query(db, tx, "SELECT operation_id,body FROM replication_settlements WHERE vault_id=$id ORDER BY operation_id;", ("$id", id)))
            using (var r = q.ExecuteReader()) while (r.Read())
            {
                var bytes = (byte[])r[1]; var row = StorageEncoding.Decode(bytes, StorageEncoding.Replication);
                if (row.Revision != 0 || row.NextHeartbeat != DateTimeOffset.MinValue || row.Sessions.Count != 0 || row.Settlements.Count != 1
                    || row.Settlements[0].Operation.ToString("N") != r.GetString(0)) throw new InvalidDataException("Invalid settlement row");
                settlements.Add(row.Settlements[0]); settlementRows.Add(bytes);
            }
            if (ProtocolHash(root, sessionRows, settlementRows) != checksum) throw new InvalidDataException("Corrupt replication rows");
            return new ReplicationState(index.Revision, index.NextHeartbeat, sessions, settlements);
        }
    }
}
