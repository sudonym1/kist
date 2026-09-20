using System;

namespace ThorsKist
{
    /// <summary>
    /// Coarse buckets a player would actually name -- "wood", "ore", "hides".
    /// Deliberately finer than Valheim's own ItemType, which lumps every raw
    /// material into one value. Declaration order IS the display order.
    /// </summary>
    public enum ItemCategory
    {
        Weapon = 0,
        Ammo = 1,
        Armor = 2,
        Tool = 3,
        Food = 4,
        Seed = 5,
        Wood = 6,
        Stone = 7,
        Ore = 8,
        Hide = 9,
        Parts = 10,
        Fibre = 11,
        Gem = 12,
        Trophy = 13,
        Fish = 14,
        Valuable = 15,
        Other = 16
    }

    /// <summary>
    /// Works out what kind of thing an item is, from nothing but its ItemType
    /// and its localisation token.
    ///
    /// Deliberately free of any Unity or Valheim type so it can be compiled and
    /// exercised outside the game -- see tools/CategoryCheck, which runs the
    /// real code in this file against a table of real tokens in CI. Keyword
    /// matching is full of traps that compile perfectly and quietly mis-file
    /// things: "leatherscraps" contains "scrap", "barleyflour" contains "bar",
    /// and "eyescream" contains "eye". The order below is load-bearing, and the
    /// only way to know it still holds is to run it.
    /// </summary>
    public static class KistCategories
    {
        public static readonly string[] Names =
        {
            "Weapon", "Ammo", "Armour", "Tool", "Food", "Seeds",
            "Wood", "Stone", "Ore & Metal", "Hides", "Parts", "Fibre",
            "Gems & Cores", "Trophy", "Fish", "Valuables", "Other"
        };

        public static string NameOf(ItemCategory c)
        {
            int i = (int)c;
            return i >= 0 && i < Names.Length ? Names[i] : "";
        }

        /// <summary>
        /// The ItemType numbers the switch below is written against. Hard-coded
        /// numbers are exactly the sort of thing a game update renumbers without
        /// anything failing to compile, so tools/CategoryCheck reads the real
        /// enum out of assembly_valheim and fails the build if any of these has
        /// moved.
        /// </summary>
        public static readonly string[] ExpectedItemTypes =
        {
            "Material=1", "Consumable=2", "OneHandedWeapon=3", "Bow=4", "Shield=5",
            "Helmet=6", "Chest=7", "Ammo=9", "Customization=10", "Legs=11", "Hands=12",
            "Trophy=13", "TwoHandedWeapon=14", "Torch=15", "Misc=16", "Shoulder=17",
            "Utility=18", "Tool=19", "Attach_Atgeir=20", "Fish=21",
            "TwoHandedWeaponLeft=22", "AmmoNonEquipable=23", "Trinket=24"
        };

        /// <summary>
        /// Valheim's ItemType, by its numeric value so this file needs no
        /// reference to the game. Equipment is exact here; everything that
        /// returns false falls through to the token rules.
        /// </summary>
        public static bool FromItemType(int itemType, out ItemCategory category)
        {
            switch (itemType)
            {
                case 3:   // OneHandedWeapon
                case 4:   // Bow
                case 14:  // TwoHandedWeapon
                case 20:  // Attach_Atgeir
                case 22:  // TwoHandedWeaponLeft
                    category = ItemCategory.Weapon; return true;

                case 9:   // Ammo
                case 23:  // AmmoNonEquipable
                    category = ItemCategory.Ammo; return true;

                case 5:   // Shield
                case 6:   // Helmet
                case 7:   // Chest
                case 10:  // Customization
                case 11:  // Legs
                case 12:  // Hands
                case 17:  // Shoulder
                case 18:  // Utility
                case 24:  // Trinket
                    category = ItemCategory.Armor; return true;

                case 15:  // Torch
                case 19:  // Tool
                    category = ItemCategory.Tool; return true;

                case 2:   // Consumable
                    category = ItemCategory.Food; return true;

                case 13:  // Trophy
                    category = ItemCategory.Trophy; return true;

                case 21:  // Fish
                    category = ItemCategory.Fish; return true;
            }

            category = ItemCategory.Other;
            return false;
        }

        /// <summary>
        /// Raw materials, which all share ItemType.Material and so have to be
        /// told apart by name. The first rule that matches wins, so the more
        /// specific bucket has to come first.
        /// </summary>
        public static ItemCategory FromToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return ItemCategory.Other;
            string t = token.ToLowerInvariant();

            // Before Wood, so "beechseeds" is a seed and not a log.
            if (Has(t, "seed", "acorn", "pinecone", "sapling", "cone")) return ItemCategory.Seed;

            // Before Ore, so "elderbark" is not caught by a metal rule.
            if (Has(t, "wood", "log", "bark", "branch", "stake", "beech", "birch"))
                return ItemCategory.Wood;

            // Before Ore, so "leatherscraps" does not land with "ironscrap".
            if (Has(t, "hide", "pelt", "leather", "scale", "carapace", "chitin", "fur"))
                return ItemCategory.Hide;

            if (Has(t, "flax", "linen", "jute", "thread", "cloth", "yarn", "string"))
                return ItemCategory.Fibre;

            // Before Parts, so "eyescream" is food rather than an eye; and
            // before Ore, so "barley" and "barleyflour" are not metal.
            if (Has(t, "berry", "berries", "mushroom", "honey", "meat", "carrot",
                       "turnip", "onion", "barley", "flour", "bread", "mead", "potion",
                       "dandelion", "thistle", "sausage", "stew", "soup", "pie", "jam",
                       "cream", "egg", "milk", "fish"))
                return ItemCategory.Food;

            if (Has(t, "bone", "fang", "tusk", "claw", "tooth", "horn", "eye", "gland",
                       "sinew", "tissue", "mandible", "feather", "jelly", "entrail",
                       "bloodbag", "ooze", "guck", "stinger", "wing", "bilebag",
                       "tail", "sack", "heart", "brain", "neck", "trophy"))
                return ItemCategory.Parts;

            if (Has(t, "core", "crystal", "ruby", "amber", "gem", "pearl", "eitr", "sap",
                       "tear", "wisp", "essence"))
                return ItemCategory.Gem;

            if (Has(t, "coin", "treasure", "necklace", "ring")) return ItemCategory.Valuable;

            // Before Ore, so "thunderstone" and "blackmarble" are not metal.
            if (Has(t, "stone", "flint", "marble", "obsidian", "grausten", "rock",
                       "clay", "salt", "sulfur"))
                return ItemCategory.Stone;

            // "bar" is deliberately NOT a keyword here: it matches "barley".
            if (Has(t, "ore", "metal", "ingot", "bronze", "copper", "tin", "iron", "silver",
                       "flametal", "nail", "chain", "scrap", "steel", "coal"))
                return ItemCategory.Ore;

            return ItemCategory.Other;
        }

        private static bool Has(string haystack, params string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
                if (haystack.IndexOf(needles[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
