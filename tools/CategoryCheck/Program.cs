using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ThorsKist;

namespace CategoryCheck
{
    /// <summary>
    /// Runs the mod's real category rules, outside the game.
    ///
    /// The rules are keyword matching over item names, and keyword matching goes
    /// wrong in ways that compile, run, and produce a plausible-looking list --
    /// "leatherscraps" filed with iron scrap, "barleyflour" filed as metal
    /// because of "bar". Nothing about that fails until somebody stares at the
    /// window and notices. So the traps are pinned here, and CI runs them.
    ///
    /// Also checks that the ItemType numbers the classifier is written against
    /// still match the ones the game ships, since those are hard-coded.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string refDir = args.Length > 0
                ? args[0]
                : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".refs");

            int failures = 0;
            failures += CheckItemTypeNumbers(Path.GetFullPath(Path.Combine(refDir, "assembly_valheim.dll")));
            failures += CheckTokens();

            Console.WriteLine();
            if (failures > 0)
            {
                Console.WriteLine(failures + " category check(s) FAILED.");
                return 1;
            }
            Console.WriteLine("All category checks passed.");
            return 0;
        }

        // ------------------------------------------------------------------

        private static int CheckItemTypeNumbers(string assemblyPath)
        {
            Console.WriteLine("-- ItemType numbering --");
            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine("  FAIL  " + assemblyPath + " not found; cannot check the numbers the " +
                                  "classifier hard-codes. Refusing to pass on an unchecked assumption.");
                return 1;
            }

            Dictionary<string, int> actual;
            try
            {
                actual = ReadItemTypeEnum(assemblyPath);
            }
            catch (Exception e)
            {
                Console.WriteLine("  FAIL  could not read ItemDrop.ItemData.ItemType: " + e.Message);
                return 1;
            }

            if (actual.Count == 0)
            {
                Console.WriteLine("  FAIL  ItemDrop.ItemData.ItemType has no members; the type moved or " +
                                  "was renamed.");
                return 1;
            }

            int bad = 0;
            foreach (string expected in KistCategories.ExpectedItemTypes)
            {
                string[] parts = expected.Split('=');
                string name = parts[0];
                int want = int.Parse(parts[1]);

                int got;
                if (!actual.TryGetValue(name, out got))
                {
                    Console.WriteLine("  FAIL  " + name + " no longer exists in ItemType.");
                    bad++;
                }
                else if (got != want)
                {
                    Console.WriteLine("  FAIL  " + name + " is " + got + ", classifier assumes " + want + ".");
                    bad++;
                }
            }

            if (bad == 0)
                Console.WriteLine("  ok    " + KistCategories.ExpectedItemTypes.Length +
                                  " ItemType value(s) match the shipped assembly.");
            return bad;
        }

        private static Dictionary<string, int> ReadItemTypeEnum(string assemblyPath)
        {
            List<string> paths = new List<string> { assemblyPath };
            paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(assemblyPath), "*.dll"));
            paths.AddRange(Directory.GetFiles(
                Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll"));

            var resolver = new System.Reflection.PathAssemblyResolver(paths.Distinct().ToArray());
            using (var mlc = new MetadataLoadContext(resolver))
            {
                Assembly asm = mlc.LoadFromAssemblyPath(assemblyPath);
                Type itemDrop = asm.GetType("ItemDrop");
                Type itemData = itemDrop?.GetNestedType("ItemData");
                Type itemType = itemData?.GetNestedType("ItemType");
                if (itemType == null)
                    throw new Exception("ItemDrop.ItemData.ItemType not found");

                Dictionary<string, int> map = new Dictionary<string, int>();
                foreach (FieldInfo f in itemType.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    object raw = f.GetRawConstantValue();
                    if (raw != null) map[f.Name] = Convert.ToInt32(raw);
                }
                return map;
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Every entry here is a rule that would otherwise be one keyword away
        /// from breaking silently. The marked ones are real traps that the first
        /// draft of the classifier got wrong.
        /// </summary>
        private static readonly string[][] Cases =
        {
            // --- traps ---
            new[] { "$item_leatherscraps", "Hides" },   // contains "scrap"
            new[] { "$item_barleyflour",   "Food" },   // contains "bar"
            new[] { "$item_barley",        "Food" },   // contains "bar"
            new[] { "$item_eyescream",     "Food" },   // contains "eye"
            new[] { "$item_elderbark",     "Wood" },   // "bark", not a metal
            new[] { "$item_beechseeds",    "Seeds" },  // "beech" is also a wood word
            new[] { "$item_blackmarble",   "Stone" },  // "marble" before any metal rule
            new[] { "$item_thunderstone",  "Stone" },
            new[] { "$item_carrotseeds",   "Seeds" },
            new[] { "$item_ironscrap",     "Ore & Metal" },
            new[] { "$item_blackmetalscrap", "Ore & Metal" },

            // --- wood ---
            new[] { "$item_wood",          "Wood" },
            new[] { "$item_finewood",      "Wood" },
            new[] { "$item_roundlog",      "Wood" },
            new[] { "$item_yggdrasilwood", "Wood" },

            // --- ore and metal ---
            new[] { "$item_copperore",     "Ore & Metal" },
            new[] { "$item_tin",           "Ore & Metal" },
            new[] { "$item_bronze",        "Ore & Metal" },
            new[] { "$item_iron",          "Ore & Metal" },
            new[] { "$item_silver",        "Ore & Metal" },
            new[] { "$item_flametal",      "Ore & Metal" },
            new[] { "$item_chain",         "Ore & Metal" },
            new[] { "$item_ironnails",     "Ore & Metal" },
            new[] { "$item_coal",          "Ore & Metal" },

            // --- stone ---
            new[] { "$item_stone",         "Stone" },
            new[] { "$item_flint",         "Stone" },
            new[] { "$item_obsidian",      "Stone" },
            new[] { "$item_grausten",      "Stone" },

            // --- hides ---
            new[] { "$item_deerhide",      "Hides" },
            new[] { "$item_wolfpelt",      "Hides" },
            new[] { "$item_loxpelt",       "Hides" },
            new[] { "$item_carapace",      "Hides" },
            new[] { "$item_askhide",       "Hides" },
            new[] { "$item_scalehide",     "Hides" },

            // --- parts ---
            new[] { "$item_greydwarfeye",  "Parts" },
            new[] { "$item_wolffang",      "Parts" },
            new[] { "$item_bonefragments", "Parts" },
            new[] { "$item_freezegland",   "Parts" },
            new[] { "$item_entrails",      "Parts" },
            new[] { "$item_softtissue",    "Parts" },
            new[] { "$item_morgensinew",   "Parts" },
            new[] { "$item_royaljelly",    "Parts" },

            // --- fibre ---
            new[] { "$item_flax",          "Fibre" },
            new[] { "$item_linenthread",   "Fibre" },
            new[] { "$item_jutered",       "Fibre" },

            // --- gems and cores ---
            new[] { "$item_surtlingcore",  "Gems & Cores" },
            new[] { "$item_blackcore",     "Gems & Cores" },
            new[] { "$item_ruby",          "Gems & Cores" },
            new[] { "$item_crystal",       "Gems & Cores" },
            new[] { "$item_eitr",          "Gems & Cores" },
            new[] { "$item_dragontear",    "Gems & Cores" },
            new[] { "$item_wisp",          "Gems & Cores" },

            // --- food ---
            new[] { "$item_raspberry",     "Food" },
            new[] { "$item_blueberries",   "Food" },
            new[] { "$item_mushroom",      "Food" },
            new[] { "$item_honey",         "Food" },
            new[] { "$item_carrot",        "Food" },
            new[] { "$item_turnip",        "Food" },
            new[] { "$item_thistle",       "Food" },
            new[] { "$item_dandelion",     "Food" },

            // --- seeds ---
            new[] { "$item_turnipseeds",   "Seeds" },
            new[] { "$item_onionseeds",    "Seeds" },
            new[] { "$item_acorn",         "Seeds" },
            new[] { "$item_pinecone",      "Seeds" },

            // --- nothing sensible to say ---
            new[] { "$item_tar",           "Other" },
            new[] { "$item_resin",         "Other" }
        };

        private static int CheckTokens()
        {
            Console.WriteLine();
            Console.WriteLine("-- material classification --");

            int bad = 0;
            foreach (string[] c in Cases)
            {
                string got = KistCategories.NameOf(KistCategories.FromToken(c[0]));
                if (got == c[1]) continue;
                Console.WriteLine("  FAIL  " + c[0] + " -> " + got + ", expected " + c[1]);
                bad++;
            }

            if (bad == 0)
                Console.WriteLine("  ok    " + Cases.Length + " material name(s) classified as expected.");

            // A test that passes no matter what the code does is worse than no
            // test. Prove the table can actually fail.
            if (KistCategories.FromToken("$item_zzznotathing") != ItemCategory.Other)
            {
                Console.WriteLine("  FAIL  an unknown name did not fall through to Other; the rules are " +
                                  "matching everything and this check proves nothing.");
                bad++;
            }

            // Equipment must never reach the keyword rules: a "Bronze sword" is
            // a weapon, not ore.
            ItemCategory sword;
            if (!KistCategories.FromItemType(3, out sword) || sword != ItemCategory.Weapon)
            {
                Console.WriteLine("  FAIL  OneHandedWeapon did not classify as a weapon.");
                bad++;
            }
            if (KistCategories.FromToken("$item_swordbronze") != ItemCategory.Ore)
            {
                Console.WriteLine("  FAIL  the keyword rules stopped filing a bronze sword as ore, so the " +
                                  "ItemType check above is no longer load-bearing and this test is stale.");
                bad++;
            }

            return bad;
        }
    }
}
