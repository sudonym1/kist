using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThorsKist
{
    /// <summary>How the Kist list is ordered.</summary>
    public enum SortMode
    {
        Name = 0,
        Quantity = 1,
        Progression = 2,
        Category = 3
    }

    /// <summary>
    /// Ordering for the Kist list.
    ///
    /// Two of the four modes need knowledge the item itself does not carry: how
    /// far into the game something is, and what kind of thing it is. Both are
    /// worked out once, lazily, and cached.
    ///
    /// Progression is DERIVED from the game's own data wherever the game states
    /// it -- which biome a plant or deposit spawns in, which biome a creature
    /// that drops it lives in, and what a recipe is made of -- and only seeded
    /// by hand for the materials none of those cover (dungeon loot, mostly).
    /// Deriving it means mod-added items land in the right place for free.
    ///
    /// Anything the table has no answer for sorts last and is LABELLED as
    /// unknown in the row rather than being quietly guessed at, so a gap is
    /// visible on screen instead of showing up as a wrong-looking order.
    /// </summary>
    public static class KistSort
    {
        public const int UnknownTier = 99;

        private static readonly string[] TierNames =
        {
            "Meadows", "Black Forest", "Swamp", "Mountain",
            "Plains", "Mistlands", "Ashlands", "Deep North"
        };

        // token -> tier. Keyed on m_shared.m_name (a localisation token such as
        // "$item_wood"), because that is the only stable identity a stack pulled
        // out of the vault is guaranteed to still have.
        private static Dictionary<string, int> _tier;
        private static Dictionary<string, ItemCategory> _catCache;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        public static string ModeLabel(SortMode m)
        {
            switch (m)
            {
                case SortMode.Quantity: return "Amount";
                case SortMode.Progression: return "Progress";
                case SortMode.Category: return "Type";
                default: return "A-Z";
            }
        }

        /// <summary>
        /// The dim note shown at the end of a row, so the sort can be checked by
        /// eye. Empty for the modes where the order is self-evident.
        /// </summary>
        public static string RowTag(ItemDrop.ItemData item, SortMode mode)
        {
            if (item == null) return "";
            if (mode == SortMode.Progression)
            {
                int t = TierOf(item);
                return t >= 0 && t < TierNames.Length ? TierNames[t] : "unknown";
            }
            if (mode == SortMode.Category) return KistCategories.NameOf(CategoryOf(item));
            return "";
        }

        /// <summary>
        /// Returns a new list in the requested order. The input is left alone --
        /// it belongs to the client's mirror of the vault.
        /// </summary>
        public static List<ItemDrop.ItemData> Arrange(List<ItemDrop.ItemData> items, SortMode mode)
        {
            List<ItemDrop.ItemData> result = new List<ItemDrop.ItemData>();
            if (items == null) return result;

            // Totals are per ITEM, not per stack: sorting 3 stacks of wood by
            // "amount" has to rank wood by all 300 of it, or the biggest hoard
            // in the Kist scatters down the list in 50s.
            Dictionary<string, int> totals = null;
            if (mode == SortMode.Quantity)
            {
                totals = new Dictionary<string, int>();
                foreach (ItemDrop.ItemData it in items)
                {
                    string k = TokenOf(it);
                    if (k == null) continue;
                    int had;
                    totals[k] = totals.TryGetValue(k, out had) ? had + it.m_stack : it.m_stack;
                }
            }

            List<Key> keys = new List<Key>(items.Count);
            foreach (ItemDrop.ItemData it in items)
            {
                Key k = new Key();
                k.Item = it;
                k.Name = DisplayName(it);
                k.Stack = it.m_stack;
                k.Tier = mode == SortMode.Progression || mode == SortMode.Category ? TierOf(it) : 0;
                k.Cat = mode == SortMode.Progression || mode == SortMode.Category ? (int)CategoryOf(it) : 0;
                if (totals != null)
                {
                    string t = TokenOf(it);
                    int sum;
                    k.Total = t != null && totals.TryGetValue(t, out sum) ? sum : it.m_stack;
                }
                keys.Add(k);
            }

            keys.Sort(delegate (Key a, Key b)
            {
                int c;
                switch (mode)
                {
                    case SortMode.Quantity:
                        c = b.Total.CompareTo(a.Total);
                        if (c != 0) return c;
                        c = ByName(a, b);
                        if (c != 0) return c;
                        return b.Stack.CompareTo(a.Stack);

                    case SortMode.Progression:
                        c = a.Tier.CompareTo(b.Tier);
                        if (c != 0) return c;
                        c = a.Cat.CompareTo(b.Cat);
                        if (c != 0) return c;
                        return ByName(a, b);

                    case SortMode.Category:
                        c = a.Cat.CompareTo(b.Cat);
                        if (c != 0) return c;
                        c = a.Tier.CompareTo(b.Tier);
                        if (c != 0) return c;
                        return ByName(a, b);

                    default:
                        return ByName(a, b);
                }
            });

            foreach (Key k in keys) result.Add(k.Item);
            return result;
        }

        private struct Key
        {
            public ItemDrop.ItemData Item;
            public string Name;
            public int Tier;
            public int Cat;
            public int Total;
            public int Stack;
        }

        private static int ByName(Key a, Key b)
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            if (c != 0) return c;
            // Quality last, so a q1 and a q3 sword never swap places between
            // redraws -- an unstable order makes the take buttons move under
            // the cursor.
            int aq = a.Item != null ? a.Item.m_quality : 0;
            int bq = b.Item != null ? b.Item.m_quality : 0;
            return aq.CompareTo(bq);
        }

        public static string DisplayName(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return "";
            return Localization.instance != null
                ? Localization.instance.Localize(item.m_shared.m_name)
                : item.m_shared.m_name;
        }

        private static string TokenOf(ItemDrop.ItemData item)
        {
            return item != null && item.m_shared != null ? item.m_shared.m_name : null;
        }

        // ------------------------------------------------------------------
        // Category
        // ------------------------------------------------------------------

        /// <summary>
        /// Equipment is classified from the game's own ItemType, which is exact.
        /// Raw materials all share ItemType.Material, so those fall through to
        /// keyword matching on the localisation token -- "$item_copperore" and
        /// the like. That is a heuristic, which is why the category is printed
        /// in the row: a wrong bucket is then something you can see and say so
        /// about, rather than a silently odd order.
        /// </summary>
        public static ItemCategory CategoryOf(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return ItemCategory.Other;
            string token = item.m_shared.m_name;
            if (string.IsNullOrEmpty(token)) return ItemCategory.Other;

            if (_catCache == null) _catCache = new Dictionary<string, ItemCategory>();
            ItemCategory cached;
            if (_catCache.TryGetValue(token, out cached)) return cached;

            ItemCategory cat = Classify(item, token);
            _catCache[token] = cat;
            return cat;
        }

        private static ItemCategory Classify(ItemDrop.ItemData item, string token)
        {
            ItemCategory byType;
            if (KistCategories.FromItemType((int)item.m_shared.m_itemType, out byType)) return byType;
            return KistCategories.FromToken(token);
        }

        // ------------------------------------------------------------------
        // Progression
        // ------------------------------------------------------------------

        public static int TierOf(ItemDrop.ItemData item)
        {
            string token = TokenOf(item);
            if (string.IsNullOrEmpty(token)) return UnknownTier;
            EnsureTiers();
            int t;
            return _tier != null && _tier.TryGetValue(token, out t) ? t : UnknownTier;
        }

        /// <summary>Drops the cached tables. Used when the world changes.</summary>
        public static void Reset()
        {
            _tier = null;
            _catCache = null;
        }

        private static void EnsureTiers()
        {
            if (_tier != null) return;

            // Built from the game's own data, so it needs that data to exist.
            // Bailing WITHOUT caching is the point: an empty table cached before
            // the world loaded would mark every item unknown forever, and look
            // exactly like a broken tier table.
            if (ObjectDB.instance == null) return;

            _tier = new Dictionary<string, int>();

            // Each pass is separately guarded. A game update that renames one of
            // these should cost that pass and nothing else -- the worst outcome
            // is items reported as unknown, which is visible and harmless, not a
            // window that throws while drawing.
            int seeded = Guarded("seed", SeedPass);
            int vegetation = Guarded("vegetation", VegetationPass);
            int creatures = Guarded("creatures", CreaturePass);
            int recipes = Guarded("recipes", RecipePass);

            Main.Log.LogInfo("ThorsKist: progression table built -- " + _tier.Count + " items (" +
                             seeded + " seeded, " + vegetation + " from world growth, " +
                             creatures + " from creature drops, " + recipes + " from recipes).");
        }

        private static int Guarded(string what, Func<int> pass)
        {
            try { return pass(); }
            catch (Exception e)
            {
                Main.Log.LogWarning("ThorsKist: progression pass '" + what + "' failed, items it would " +
                                    "have covered will sort as unknown. " + e.Message);
                return 0;
            }
        }

        /// <summary>Records a tier, keeping the EARLIEST one seen: an item you can
        /// get in the Black Forest is a Black Forest item even if the Plains drop
        /// it too.</summary>
        private static bool Note(string token, int tier)
        {
            if (string.IsNullOrEmpty(token) || tier < 0 || tier >= TierNames.Length) return false;
            int had;
            if (_tier.TryGetValue(token, out had))
            {
                if (tier >= had) return false;
                _tier[token] = tier;
                return false;   // refined, not new
            }
            _tier[token] = tier;
            return true;
        }

        // ---- Pass 1: hand-seeded ------------------------------------------

        /// <summary>
        /// The materials the game does not place in the world itself -- creature
        /// drops the spawn tables miss, and dungeon loot. Every prefab name here
        /// was checked against the 43,346 the game actually ships; a name that
        /// stops resolving is logged rather than silently doing nothing.
        /// </summary>
        private static readonly string[][] Seeds =
        {
            // Meadows
            new[] { "Wood", "Stone", "Flint", "Resin", "Feathers", "LeatherScraps", "DeerHide",
                    "Dandelion", "Mushroom", "Raspberry", "Honey", "NeckTail", "DeerMeat", "Coal" },
            // Black Forest
            new[] { "RoundLog", "FineWood", "CopperOre", "Copper", "TinOre", "Tin", "Bronze",
                    "SurtlingCore", "GreydwarfEye", "BoneFragments", "Thistle", "Blueberries",
                    "Ruby", "TrollHide", "Carrot", "CarrotSeeds", "Acorn", "BeechSeeds", "PineCone" },
            // Swamp
            new[] { "IronScrap", "Iron", "ElderBark", "Guck", "Chain", "WitheredBone", "Entrails",
                    "Bloodbag", "Ooze", "AncientSeed", "Root", "Turnip", "TurnipSeeds", "Amber",
                    "AmberPearl", "Ironpit" },
            // Mountain
            new[] { "SilverOre", "Silver", "Obsidian", "WolfPelt", "WolfFang", "FreezeGland",
                    "Crystal", "Onion", "OnionSeeds", "JuteRed", "DragonEgg", "DragonTear" },
            // Plains
            new[] { "BlackMetalScrap", "BlackMetal", "Flax", "Barley", "LinenThread", "BarleyFlour",
                    "Needle", "Tar", "LoxPelt", "Cloudberry", "RoyalJelly" },
            // Mistlands
            new[] { "BlackMarble", "YggdrasilWood", "Eitr", "Sap", "Softtissue", "BlackCore", "Wisp",
                    "Carapace", "MechanicalSpring", "ScaleHide", "Mandible", "JuteBlue",
                    "Thunderstone", "GiantBloodSack", "MushroomJotunPuffs", "MushroomMagecap" },
            // Ashlands
            new[] { "Flametal", "FlametalNew", "FlametalOreNew", "AskHide", "CharredBone",
                    "MoltenCore", "ProustitePowder", "CelestialFeather", "MorgenSinew", "Grausten",
                    "Blackwood", "Sulfur", "Vineberry", "BonemawTooth", "CharcoalResin" }
            // Deep North is deliberately absent: its materials are left to the
            // derived passes below rather than guessed at from memory.
        };

        private static int SeedPass()
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null) return 0;

            int added = 0;
            List<string> missing = null;
            for (int tier = 0; tier < Seeds.Length; tier++)
            {
                foreach (string prefabName in Seeds[tier])
                {
                    GameObject go = db.GetItemPrefab(prefabName);
                    string token = TokenOf(go);
                    if (token == null)
                    {
                        if (missing == null) missing = new List<string>();
                        missing.Add(prefabName);
                        continue;
                    }
                    if (Note(token, tier)) added++;
                }
            }

            if (missing != null)
            {
                Main.Log.LogInfo("ThorsKist: " + missing.Count + " seeded prefab names no longer resolve (" +
                                 string.Join(", ", missing.ToArray()) + "). They will sort as unknown.");
            }
            return added;
        }

        // ---- Pass 2: what grows where -------------------------------------

        /// <summary>
        /// ZoneSystem's vegetation list says which biome every plant, deposit and
        /// pickable spawns in. Walking what each one yields gives real tiers for
        /// berries, flint, ore deposits, black marble and anything a mod plants.
        /// </summary>
        private static int VegetationPass()
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null || zs.m_vegetation == null) return 0;

            int added = 0;
            foreach (ZoneSystem.ZoneVegetation v in zs.m_vegetation)
            {
                if (v == null || v.m_prefab == null) continue;
                int tier = BiomeTier(v.m_biome);
                if (tier < 0) continue;
                added += HarvestFrom(v.m_prefab, tier);
            }
            return added;
        }

        /// <summary>Every item a prefab hands over when picked or broken.</summary>
        private static int HarvestFrom(GameObject prefab, int tier)
        {
            int added = 0;

            Pickable[] pickables = prefab.GetComponentsInChildren<Pickable>(true);
            foreach (Pickable p in pickables)
                if (p != null && Note(TokenOf(p.m_itemPrefab), tier)) added++;

            DropOnDestroyed[] destroyed = prefab.GetComponentsInChildren<DropOnDestroyed>(true);
            foreach (DropOnDestroyed d in destroyed)
                if (d != null) added += FromTable(d.m_dropWhenDestroyed, tier);

            MineRock[] rocks = prefab.GetComponentsInChildren<MineRock>(true);
            foreach (MineRock r in rocks)
                if (r != null) added += FromTable(r.m_dropItems, tier);

            MineRock5[] rocks5 = prefab.GetComponentsInChildren<MineRock5>(true);
            foreach (MineRock5 r in rocks5)
                if (r != null) added += FromTable(r.m_dropItems, tier);

            return added;
        }

        private static int FromTable(DropTable table, int tier)
        {
            if (table == null || table.m_drops == null) return 0;
            int added = 0;
            foreach (DropTable.DropData d in table.m_drops)
                if (Note(TokenOf(d.m_item), tier)) added++;
            return added;
        }

        // ---- Pass 3: what lives where -------------------------------------

        /// <summary>
        /// Spawn lists pair a creature with the biome it appears in, and each
        /// creature's CharacterDrop says what it leaves behind. That covers
        /// hides, meat, glands and trophies -- including biomes this mod has
        /// never heard of.
        ///
        /// The lists are reached through the SpawnSystem components in the scene,
        /// NOT by searching for SpawnSystemList directly: those live on prefab
        /// assets, and a scene search would never see one -- it would return
        /// nothing and look like a biome with no creatures in it. SpawnSystem's
        /// own m_instances list is private on the shipped assembly whatever the
        /// publicized build used for compiling says, so that is out too.
        /// </summary>
        private static int CreaturePass()
        {
            // FindObjectsByType is what Unity's own deprecation notice points at,
            // but this game's Unity build does not actually ship it -- the name
            // occurs only inside that notice's text.
#pragma warning disable CS0618
            SpawnSystem[] systems = UnityEngine.Object.FindObjectsOfType<SpawnSystem>(true);
#pragma warning restore CS0618
            if (systems == null) return 0;

            List<SpawnSystemList> lists = new List<SpawnSystemList>();
            foreach (SpawnSystem s in systems)
            {
                if (s == null || s.m_spawnLists == null) continue;
                foreach (SpawnSystemList l in s.m_spawnLists)
                    if (l != null && !lists.Contains(l)) lists.Add(l);
            }

            int added = 0;
            foreach (SpawnSystemList list in lists)
            {
                if (list == null || list.m_spawners == null) continue;
                foreach (SpawnSystem.SpawnData spawn in list.m_spawners)
                {
                    if (spawn == null || spawn.m_prefab == null) continue;
                    int tier = BiomeTier(spawn.m_biome);
                    if (tier < 0) continue;

                    CharacterDrop[] drops = spawn.m_prefab.GetComponentsInChildren<CharacterDrop>(true);
                    foreach (CharacterDrop cd in drops)
                    {
                        if (cd == null || cd.m_drops == null) continue;
                        foreach (CharacterDrop.Drop d in cd.m_drops)
                            if (Note(TokenOf(d.m_prefab), tier)) added++;
                    }
                }
            }
            return added;
        }

        // ---- Pass 4: what things are made of ------------------------------

        /// <summary>
        /// A crafted item is as late as its latest ingredient, and at least as
        /// late as the station that makes it. Run to a fixed point so a chain --
        /// ore to bar to sword -- settles no matter what order the recipes are
        /// in. This is what places every weapon, tool and piece of armour, and
        /// it does the same for a mod's items without knowing anything of them.
        /// </summary>
        private static int RecipePass()
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null || db.m_recipes == null) return 0;

            int added = 0;
            for (int round = 0; round < 8; round++)
            {
                bool changed = false;
                foreach (Recipe recipe in db.m_recipes)
                {
                    if (recipe == null || !recipe.m_enabled || recipe.m_item == null) continue;
                    string token = TokenOf(recipe.m_item.gameObject);
                    if (token == null) continue;

                    int tier = StationTier(recipe.m_craftingStation);
                    bool known = tier >= 0;

                    if (recipe.m_resources != null)
                    {
                        foreach (Piece.Requirement req in recipe.m_resources)
                        {
                            if (req == null || req.m_resItem == null) continue;
                            string res = TokenOf(req.m_resItem.gameObject);
                            int rt;
                            if (res == null || !_tier.TryGetValue(res, out rt)) continue;
                            if (rt > tier) tier = rt;
                            known = true;
                        }
                    }

                    if (!known || tier < 0) continue;

                    int had;
                    if (_tier.TryGetValue(token, out had))
                    {
                        // Only ever pull a craftable EARLIER, never later: a
                        // seeded or world-derived tier is first-hand knowledge
                        // and a recipe is an inference.
                        if (tier >= had) continue;
                    }
                    else added++;

                    _tier[token] = tier;
                    changed = true;
                }
                if (!changed) break;
            }
            return added;
        }

        /// <summary>
        /// The floor a station puts under everything it makes. Matched on the
        /// prefab name so a station this mod has not heard of simply contributes
        /// nothing rather than a wrong number.
        /// </summary>
        private static int StationTier(CraftingStation station)
        {
            if (station == null) return -1;
            string n = station.name != null ? station.name.ToLowerInvariant() : "";
            if (n.Contains("blackforge")) return 5;
            if (n.Contains("magetable") || n.Contains("galdr")) return 5;
            if (n.Contains("artisan")) return 4;
            if (n.Contains("forge")) return 1;
            if (n.Contains("stonecutter")) return 2;
            if (n.Contains("workbench") || n.Contains("cauldron") || n.Contains("preptable")) return 0;
            return -1;
        }

        // ---- Shared helpers ------------------------------------------------

        private static string TokenOf(GameObject go)
        {
            if (go == null) return null;
            ItemDrop drop = go.GetComponent<ItemDrop>();
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) return null;
            return drop.m_itemData.m_shared.m_name;
        }

        /// <summary>
        /// Biome is a bit MASK -- a plant can grow in several. The earliest one
        /// set is the answer, because that is where you first get the item.
        /// </summary>
        private static int BiomeTier(Heightmap.Biome biome)
        {
            int best = -1;
            foreach (KeyValuePair<Heightmap.Biome, int> pair in BiomeOrder)
            {
                if ((biome & pair.Key) == 0) continue;
                if (best < 0 || pair.Value < best) best = pair.Value;
            }
            return best;
        }

        private static readonly KeyValuePair<Heightmap.Biome, int>[] BiomeOrder =
        {
            new KeyValuePair<Heightmap.Biome, int>(Heightmap.Biome.Meadows, 0),
            new KeyValuePair<Heightmap.Biome, int>(Heightmap.Biome.BlackForest, 1),
            new KeyValuePair<Heightmap.Biome, int>(Heightmap.Biome.Ocean, 1),
            new KeyValuePair<Heightmap.Biome, int>(Heightmap.Biome.Swamp, 2),
            new KeyValuePair<Heightmap.Biome, int>(Heightmap.Biome.Mountain, 3),
            new KeyValuePair<Heightmap.Biome, int>(Heightmap.Biome.Plains, 4),
            new KeyValuePair<Heightmap.Biome, int>(Heightmap.Biome.Mistlands, 5),
            new KeyValuePair<Heightmap.Biome, int>(Heightmap.Biome.AshLands, 6),
            new KeyValuePair<Heightmap.Biome, int>(Heightmap.Biome.DeepNorth, 7)
        };
    }
}
