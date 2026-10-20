using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ThorsKist.Sync;

namespace ThorsKist
{
    // Quantity is owned by ItemRecord; location/equipment belong to an inventory.
    // All remaining vanilla fields, including custom mod data, are canonical.
    // Use on the Unity thread (prefab lookup and vanilla serialization).
    internal static class JournalItemCodec
    {
        public const int MaximumPayload = 256 * 1024;
        private const string UnknownPrefix = "hash:";

        public static ItemRecord Capture(ItemDrop.ItemData item, int quantity)
        {
            if (item == null || item.m_dropPrefab == null || item.m_shared == null
                || quantity <= 0 || quantity > item.m_stack || quantity > item.m_shared.m_maxStackSize)
                throw new InvalidDataException("Invalid item or stack quantity");
            int version = ItemWire.InventoryVersion;
            RequireVersion(version);
            return new ItemRecord(item.m_dropPrefab.name, quantity, version,
                Encode(item, item.m_dropPrefab.name.GetStableHashCode()));
        }

        // Decode without Inventory.Load: that method discards unavailable mod
        // prefabs. A stable hash and the complete metadata survive migration.
        public static ItemRecord ReadLegacyItem(ZPackage package, int version, out Vector2i slot)
            => ReadLegacyItem(package, version, ItemWire.InventoryVersion, hash =>
            {
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null;
                return prefab != null ? prefab.name : null;
            }, out slot);

        // Scalar decoding/canonicalization is worker-safe when prefab names and
        // the audited current version have already been captured on Unity's thread.
        internal static ItemRecord ReadLegacyItem(ZPackage package, int version, int currentVersion,
            Func<int, string> prefabName, out Vector2i slot)
        {
            if (currentVersion != 110 || version < 108 || version > currentVersion)
                throw new InvalidDataException("Legacy inventory requires audited compact formats 108-110");
            int start = package.GetPos();
            var parsed = ItemDrop.ItemData.Load(package, (Version.Item)version);
            var item = parsed.itemData;
            slot = item.m_gridPos;
            if (parsed.prefabHash == 0 || item.m_stack <= 0 || package.GetPos() - start > MaximumPayload)
                throw new InvalidDataException("Invalid legacy item");
            string name = prefabName(parsed.prefabHash) ?? UnknownPrefix + parsed.prefabHash.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new ItemRecord(name, item.m_stack, currentVersion, Encode(item, parsed.prefabHash));
        }

        public static void ValidateLegacyVersion(int version)
        {
            RequireVersion(ItemWire.InventoryVersion);
            if (version < 108 || version > 110) throw new InvalidDataException("Legacy inventory requires an audited compact format (108-110)");
        }

        private static byte[] Encode(ItemDrop.ItemData item, int prefabHash)
        {
            // Audited format 110 ItemData.Save layout, normalized to one item,
            // unequipped at (0,0). Writing the hash directly retains unknowns.
            var p = new ZPackage();
            int flags = (item.m_pickedUp ? 1 : 0) | (item.m_quality != 1 ? 4 : 0)
                | (item.m_variant != 0 ? 16 : 0) | (item.m_crafterID != 0 ? 32 : 0) | 64
                | (item.m_customData.Count != 0 ? 128 : 0);
            // Round to the game's hundredth-unit precision. Truncating a
            // decoded float again can turn 1.13 -> 1.12 on the next replay.
            // Nearest-unit canonicalization is stable under load/save cycles.
            double units = Math.Round((double)item.m_durability * 100, MidpointRounding.AwayFromZero);
            if (double.IsNaN(units) || units < 0 || units > int.MaxValue
                || item.m_quality <= 0 || item.m_quality > ushort.MaxValue || item.m_worldLevel < 0 || item.m_worldLevel > byte.MaxValue)
                throw new InvalidDataException("Invalid canonical item fields");
            p.Write((int)units); p.Write((byte)0); p.Write((byte)0);
            p.Write((byte)item.m_worldLevel); p.Write((byte)flags);
            if ((flags & 4) != 0) p.Write((ushort)item.m_quality);
            if ((flags & 16) != 0) p.Write(item.m_variant);
            if ((flags & 32) != 0) { p.Write(item.m_crafterID); p.Write(item.m_crafterName); }
            p.Write(prefabHash);
            if ((flags & 128) != 0) p.WriteNumItems(item.m_customData.Count);
            foreach (var pair in item.m_customData.OrderBy(k => k.Key, StringComparer.Ordinal))
            { p.Write(pair.Key); p.Write(pair.Value); }
            p.Write((byte)(item.m_cheated ? 1 : 0));
            var bytes = p.GetArray();
            if (bytes.Length > MaximumPayload) throw new InvalidDataException("Item metadata exceeds limit");
            return bytes;
        }

        public static ItemDrop.ItemData Restore(ItemRecord record)
        {
            RequireVersion(record.FormatVersion);
            var bytes = record.CopyPayload();
            if (bytes.Length == 0 || bytes.Length > MaximumPayload) throw new InvalidDataException("Invalid item payload length");
            int expectedHash;
            bool unknown = record.Prefab.StartsWith(UnknownPrefix, StringComparison.Ordinal);
            if (unknown)
            {
                if (!int.TryParse(record.Prefab.Substring(UnknownPrefix.Length), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out expectedHash)) throw new InvalidDataException("Invalid prefab hash");
            }
            else expectedHash = record.Prefab.GetStableHashCode();
            var prefab = ObjectDB.instance == null ? null : unknown
                ? ObjectDB.instance.GetItemPrefab(expectedHash) : ObjectDB.instance.GetItemPrefab(record.Prefab);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) throw new InvalidDataException("Unavailable item prefab: " + record.Prefab);
            var item = drop.m_itemData.Clone();
            item.m_dropPrefab = prefab;
            var package = new ZPackage(bytes);
            int hash = ItemDrop.ItemData.Load(package, item, (Version.Item)record.FormatVersion);
            if (hash != expectedHash || package.GetPos() != bytes.Length
                || item.m_stack != 1 || item.m_equipped || item.m_gridPos != Vector2i.zero
                || record.Quantity > item.m_shared.m_maxStackSize)
                throw new InvalidDataException("Item metadata/prefab/quantity mismatch");
            item.m_stack = record.Quantity;
            // Reject noncanonical byte order and unsupported field layouts, not
            // unknown-prefab data in storage (storage retains opaque records).
            if (!Encode(item, expectedHash).SequenceEqual(bytes))
                throw new InvalidDataException("Noncanonical item payload");
            return item;
        }

        private static void RequireVersion(int version)
        {
            if (version != 110 || ItemWire.InventoryVersion != version)
                throw new InvalidDataException("Journal item adapter requires audited inventory format 110");
        }
    }
}
