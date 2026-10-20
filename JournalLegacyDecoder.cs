using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    internal sealed class JournalLegacyVault
    {
        public VaultState State { get; }
        private readonly byte[] header;
        public JournalLegacyVault(VaultState state, byte[] header) { State = state; this.header = (byte[])header.Clone(); }
        public byte[] CopyHeader() => (byte[])header.Clone();
    }
    internal static class JournalLegacyDecoder
    {
        // Caller captures immutable file bytes and backs up before the library's
        // transactional migration. This decoder never reads/writes save files.
        public static IReadOnlyList<JournalLegacyVault> Decode(byte[] bytes, string world, int capacity)
            => Decode(bytes, world, capacity, ItemWire.InventoryVersion, hash =>
            {
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null;
                return prefab != null ? prefab.name : null;
            });
        internal static IReadOnlyList<JournalLegacyVault> DecodeCaptured(byte[] bytes, string world, int capacity,
            int currentVersion, IReadOnlyDictionary<int, string> prefabNames)
            => Decode(bytes, world, capacity, currentVersion, hash => prefabNames.TryGetValue(hash, out var name) ? name : null);

        private static IReadOnlyList<JournalLegacyVault> Decode(byte[] bytes, string world, int capacity,
            int currentVersion, Func<int, string> prefabName)
        {
            if (bytes == null || bytes.Length > 64 * 1024 * 1024 || capacity <= 0 || currentVersion != 110)
                throw new InvalidDataException("Invalid legacy save bounds");
            var p = new ZPackage(bytes);
            if (p.ReadInt() != Wire.SaveFormat) throw new InvalidDataException("Unsupported legacy save version");
            int count = Count(p, 2048);
            var result = new List<JournalLegacyVault>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int headerStart = p.GetPos();
                string id = p.ReadString(); string name = p.ReadString(); long owner = p.ReadLong();
                var anchor = p.ReadVector3();
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || name.Length > 256 || owner == 0
                    || !Finite(anchor.x) || !Finite(anchor.y) || !Finite(anchor.z))
                    throw new InvalidDataException("Invalid legacy hird identity/anchor");
                var members = new HashSet<long>(); int n = Count(p, 10000);
                for (int j = 0; j < n; j++)
                {
                    long player = p.ReadLong(); string playerName = p.ReadString();
                    if (player == 0 || !members.Add(player) || playerName.Length > 256) throw new InvalidDataException("Invalid legacy membership");
                }
                if (!members.Contains(owner)) throw new InvalidDataException("Legacy founder is not a member");
                n = Count(p, 10000); var invited = new HashSet<long>();
                for (int j = 0; j < n; j++)
                {
                    long player = p.ReadLong(); string playerName = p.ReadString(); long inviter = p.ReadLong(); string inviterName = p.ReadString();
                    if (player == 0 || !invited.Add(player) || members.Contains(player) || playerName.Length > 256
                        || inviter == 0 || inviterName.Length > 256) throw new InvalidDataException("Invalid legacy invite");
                }
                byte[] header = bytes.Skip(headerStart).Take(p.GetPos() - headerStart).ToArray();
                var inventory = p.ReadPackage(); int version = inventory.ReadInt(); int stacks = inventory.ReadUShort();
                if (currentVersion != 110 || version < 108 || version > currentVersion) throw new InvalidDataException("Unsupported compact inventory format");
                var records = new List<StackRecord>(); var occupied = new HashSet<int>();
                for (int j = 0; j < stacks; j++)
                {
                    var item = JournalItemCodec.ReadLegacyItem(inventory, version, currentVersion, prefabName, out var position);
                    int slot = position.y * 8 + position.x;
                    if (position.x >= 8 || slot >= capacity || !occupied.Add(slot)) slot = -1;
                    records.Add(new StackRecord(Guid.NewGuid(), slot, item));
                }
                if (inventory.GetPos() != inventory.Size()) throw new InvalidDataException("Trailing legacy inventory data");
                result.Add(new JournalLegacyVault(new VaultState(new VaultIdentity(world, id, Guid.NewGuid()), capacity, records), header));
            }
            if (p.GetPos() != bytes.Length) throw new InvalidDataException("Trailing legacy save data");
            return result.AsReadOnly();
        }
        private static int Count(ZPackage p, int maximum)
        {
            int count = p.ReadInt();
            if (count < 0 || count > maximum) throw new InvalidDataException("Invalid legacy collection size");
            return count;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
