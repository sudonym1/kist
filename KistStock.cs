using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ThorsKist
{
    /// <summary>
    /// Reading and spending the vault.
    ///
    /// Every lookup goes through here for one reason: vanilla's inventory
    /// methods apply a WORLD LEVEL rule, and that rule must not be applied to
    /// the Kist.
    ///
    /// An item is stamped with the world level of the moment it entered an
    /// inventory (Inventory.AddItem sets m_worldLevel = Game.m_worldLevel), and
    /// CountItems, HaveItem and RemoveItem all default to ignoring anything
    /// stamped below the world level you are on now. GetItem is worse: it
    /// applies the same test with no way to switch it off.
    ///
    /// For the player's own pack that is the intended difficulty rule. For a
    /// shared vault it is a trap. Wood banked before a boss fell is still wood,
    /// but it silently stopped counting toward anything the moment the world
    /// level rose -- and because the Kist window lists items with GetAllItems(),
    /// which applies no filter at all, that wood stayed plainly visible on
    /// screen while every craft and every station refused to spend it. Storage
    /// the owner can see but cannot use is worse than no storage.
    ///
    /// So the vault is counted and spent without the world-level test, and the
    /// search is hand-rolled because vanilla offers no way to opt out of it.
    /// </summary>
    internal static class KistStock
    {
        private sealed class Counts
        {
            public bool Dirty = true;
            public readonly Dictionary<string, Dictionary<int, int>> Values = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        }
        private static readonly ConditionalWeakTable<Inventory, Counts> Cache = new ConditionalWeakTable<Inventory, Counts>();
        public static int Count(Inventory kist, string name, int quality)
        {
            if (kist == null || string.IsNullOrEmpty(name)) return 0;
            var counts = Cache.GetValue(kist, inventory =>
            {
                var value = new Counts();
                inventory.m_onChanged += () => value.Dirty = true;
                return value;
            });
            if (counts.Dirty)
            {
                counts.Values.Clear();
                foreach (var item in kist.GetAllItems())
                {
                    if (item?.m_shared == null) continue;
                    if (!counts.Values.TryGetValue(item.m_shared.m_name, out var qualities))
                        counts.Values.Add(item.m_shared.m_name, qualities = new Dictionary<int, int>());
                    qualities.TryGetValue(item.m_quality, out int old);
                    qualities[item.m_quality] = (int)Math.Min(int.MaxValue, (long)old + item.m_stack);
                }
                counts.Dirty = false;
            }
            if (!counts.Values.TryGetValue(name, out var found)) return 0;
            if (quality >= 0) return found.TryGetValue(quality, out int amount) ? amount : 0;
            long total = 0; foreach (int value in found.Values) total += value;
            return (int)Math.Min(int.MaxValue, total);
        }

        public static void Take(Inventory kist, string name, int amount, int quality)
        {
            if (kist == null || amount <= 0) return;
            kist.RemoveItem(name, amount, quality, false);
        }

        /// <summary>
        /// The Kist's equivalent of Inventory.GetItem, minus the world-level
        /// test that method applies unconditionally.
        /// </summary>
        public static ItemDrop.ItemData Find(Inventory kist, string name, int quality)
        {
            if (kist == null || string.IsNullOrEmpty(name)) return null;
            foreach (ItemDrop.ItemData item in kist.GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_shared.m_name != name) continue;
                if (quality >= 0 && item.m_quality != quality) continue;
                return item;
            }
            return null;
        }
    }
}
