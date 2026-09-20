using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace ThorsKist
{
    /// <summary>
    /// Notices when another mod has taken over a game method Thor's Kist
    /// depends on, and says so instead of failing quietly.
    ///
    /// The Kist works by widening two vanilla answers -- "how many have I got?"
    /// (Inventory.CountItems) and "take this many" (Inventory.RemoveItem) --
    /// while the game is asking them about a craft. That only holds while the
    /// game is the one asking. A Harmony prefix that returns false skips the
    /// original method AND every prefix queued behind it, so a mod which
    /// replaces Player.ConsumeResources outright removes the Kist from the
    /// transaction: vanilla still counts the Kist's stock toward the recipe,
    /// but the deduction never runs and the materials are never spent.
    ///
    /// This deliberately knows nothing about any particular mod, by name or by
    /// type. It asks Harmony who else has patched the seams and reports only
    /// the patches that CAN displace the original -- prefixes that return bool,
    /// and transpilers, which rewrite the body the Kist is riding on. A mod
    /// that merely adds a postfix is harmless and stays quiet.
    /// </summary>
    public static class KistConflicts
    {
        /// <summary>What is at stake if the other mod's patch does take over.</summary>
        public enum Stake
        {
            /// <summary>Wrong numbers on screen. Worth a log line, nothing more.</summary>
            Display,
            /// <summary>The Alt-click deposit gesture may not reach the Kist.</summary>
            Deposit,
            /// <summary>The Kist can be counted toward a cost and never charged for it.</summary>
            Payment
        }

        public sealed class Conflict
        {
            public string Mod;          // friendly name if we can find one, else the Harmony id
            public string Method;       // the seam that was taken over
            public bool Replaces;       // true = skipping prefix, false = transpiler
            public Stake Stake;

            public string Key { get { return Mod + "|" + Method; } }
        }

        private static readonly List<Conflict> _found = new List<Conflict>();
        private static readonly HashSet<string> _seen = new HashSet<string>();
        private static int _scans;
        private static bool _sawPlayer;
        private static bool _told;

        /// <summary>Anything worth interrupting the player for.</summary>
        public static bool AnyWorthTelling
        {
            get
            {
                foreach (Conflict c in _found) if (c.Stake != Stake.Display) return true;
                return false;
            }
        }

        public static IList<Conflict> All { get { return _found; } }

        public static void Reset()
        {
            // Only the "have we nagged yet" state resets on disconnect. The
            // findings themselves are about the installed mod set, which does
            // not change while the game is running.
            _told = false;
        }

        /// <summary>
        /// Two passes. The first runs on our first frame, by which point every
        /// plugin has had its Awake and the overwhelming majority have called
        /// PatchAll. The second runs the first time a player exists, which
        /// catches mods that wait for a world before patching.
        /// </summary>
        public static void Tick()
        {
            if (_scans == 0) { _scans++; Scan(); return; }
            if (!_sawPlayer && Player.m_localPlayer != null) { _sawPlayer = true; Scan(); }

            if (_told || !AnyWorthTelling || Player.m_localPlayer == null) return;
            _told = true;
            KistDialog.Show("THOR'S KIST - SHARED HOOKS", Report());
        }

        /// <summary>
        /// The seams to watch: every inventory or crafting method Thor's Kist
        /// actually patched, asked of Harmony itself.
        ///
        /// Deliberately derived rather than listed. A hand-kept list of method
        /// names would silently watch nothing the moment one was misspelled or
        /// a patch moved, and the check would still report a clean bill of
        /// health -- exactly the kind of test that cannot see its own bug. This
        /// way a seam added later is watched the day it is added.
        /// </summary>
        private static IEnumerable<MethodBase> Seams()
        {
            Harmony h = Main.HarmonyInstance;
            if (h != null)
            {
                IEnumerable<MethodBase> ours;
                try { ours = h.GetPatchedMethods(); }
                catch (Exception e)
                {
                    Main.Log.LogWarning("ThorsKist: could not list our own patches: " + e.Message);
                    ours = new MethodBase[0];
                }

                foreach (MethodBase m in ours)
                {
                    if (m == null || m.DeclaringType == null) continue;
                    // Only the inventory and crafting surface matters here. Our
                    // input and map patches share no ground with another mod's
                    // crafting logic.
                    Type t = m.DeclaringType;
                    if (t == typeof(Inventory) || t == typeof(Player) || t == typeof(InventoryGui))
                        yield return m;
                }
            }

            // DoCrafting is not one of ours, but a mod that replaces it never
            // reaches ConsumeResources at all -- the Kist is cut out without a
            // single patch of ours being touched, so it is worth watching too.
            MethodBase crafting = null;
            try { crafting = AccessTools.Method(typeof(InventoryGui), "DoCrafting"); }
            catch (Exception e) { Main.Log.LogWarning("ThorsKist: " + e.Message); }
            if (crafting != null) yield return crafting;
            else Main.Log.LogWarning("ThorsKist: InventoryGui.DoCrafting not found -- not watching it for conflicts.");
        }

        /// <summary>
        /// What losing a given seam would actually cost.
        ///
        /// Getting this wrong is worse than not warning at all. OnSelectedItem
        /// sat under Payment at first, so two mods that only share the deposit
        /// click -- and cannot touch the payment path -- were reported as though
        /// they handed out free materials. That sent a real evening chasing a
        /// bug that did not exist. A warning is only useful if its severity is
        /// the true one.
        /// </summary>
        private static Stake StakeOf(MethodBase m)
        {
            switch (m.Name)
            {
                // The payment path: counted toward a cost, never charged for it.
                case "ConsumeResources":
                case "CountItems":
                case "RemoveItem":
                case "HaveItem":
                case "GetItem":
                case "DoCrafting":
                    return Stake.Payment;

                // The Alt-click deposit gesture. Losing it means the click does
                // nothing -- visible immediately, and it costs nothing.
                case "OnSelectedItem":
                    return Stake.Deposit;
            }
            return Stake.Display;
        }

        private static void Scan()
        {
            int watched = 0;
            foreach (MethodBase target in Seams())
            {
                watched++;
                string name = (target.DeclaringType != null ? target.DeclaringType.Name + "." : "") + target.Name;

                Patches info;
                try { info = Harmony.GetPatchInfo(target); }
                catch (Exception e)
                {
                    Main.Log.LogWarning("ThorsKist: could not read patches on " + name + ": " + e.Message);
                    continue;
                }
                if (info == null) continue;

                Stake stake = StakeOf(target);

                // A prefix only displaces the original if it can return false.
                // A void prefix -- even one paired with a finalizer, which is
                // the polite way to wrap a method -- runs alongside ours and is
                // no threat, so it is never reported.
                foreach (Patch p in info.Prefixes)
                {
                    if (p.owner == Main.GUID) continue;
                    MethodInfo m = p.PatchMethod;
                    if (m == null || m.ReturnType != typeof(bool)) continue;
                    Note(p, name, stake, true);
                }

                // A transpiler rewrites the very body we are riding on, and can
                // remove the call we depend on without any prefix at all.
                foreach (Patch p in info.Transpilers)
                {
                    if (p.owner == Main.GUID) continue;
                    Note(p, name, stake, false);
                }
            }

            // Say how much was actually inspected. A scan that quietly watched
            // nothing would otherwise look identical to a clean result.
            Main.Log.LogInfo("ThorsKist: conflict scan looked at " + watched + " crafting seam(s), found " +
                             _found.Count + " conflict(s).");
        }

        private static void Note(Patch p, string method, Stake stake, bool replaces)
        {
            Conflict c = new Conflict
            {
                Mod = ModName(p),
                Method = method,
                Replaces = replaces,
                Stake = stake
            };
            if (!_seen.Add(c.Key)) return;
            _found.Add(c);

            // "may" throughout, and deliberately. Whether one of these prefixes
            // actually takes over is decided at run time, per call -- most are
            // conditional and decline far more often than they fire. Reporting
            // a possibility as a certainty is how the last one of these sent
            // somebody hunting a bug that was not there.
            string line = "ThorsKist: " + c.Mod + (replaces ? " may take over " : " rewrites ") + method;
            switch (stake)
            {
                case Stake.Payment:
                    Main.Log.LogWarning(line + " -- if it does, the Kist can be counted toward a cost and never charged for it.");
                    break;
                case Stake.Deposit:
                    Main.Log.LogWarning(line + " -- if it does, an Alt-click deposit may not reach the Kist. Crafting is unaffected.");
                    break;
                default:
                    Main.Log.LogInfo(line + " -- Kist amounts on screen may be wrong; payment is unaffected.");
                    break;
            }
        }

        /// <summary>
        /// A Harmony id is whatever the other mod chose, so try the plugin
        /// registry first and fall back to the assembly the patch lives in,
        /// which is always something a player can recognise on disk.
        /// </summary>
        private static string ModName(Patch p)
        {
            string owner = p.owner;
            try
            {
                foreach (KeyValuePair<string, BepInEx.PluginInfo> kv in Chainloader.PluginInfos)
                {
                    if (kv.Value == null || kv.Value.Metadata == null) continue;
                    if (kv.Key == owner || kv.Value.Metadata.GUID == owner)
                        return kv.Value.Metadata.Name;
                }
            }
            catch { /* registry shape is not worth a crash in a diagnostic */ }

            try
            {
                if (p.PatchMethod != null && p.PatchMethod.DeclaringType != null)
                {
                    string asm = p.PatchMethod.DeclaringType.Assembly.GetName().Name;
                    if (!string.IsNullOrEmpty(asm)) return asm;
                }
            }
            catch { }

            return string.IsNullOrEmpty(owner) ? "another mod" : owner;
        }

        /// <summary>
        /// The player-facing text. Says what each overlap could cost and no
        /// more -- a conditional patch may never fire, so nothing here claims
        /// anything has already gone wrong.
        /// </summary>
        public static string Report()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Another mod hooks the same game code Thor's Kist uses.\n");
            sb.Append("<i>This is a heads-up, not a fault. It may never affect you.</i>\n");

            Section(sb, Stake.Payment, "Could affect crafting",
                "If it takes over, the Kist may be counted toward a recipe and never charged for it. " +
                "Setting <b>craftFromKist = false</b> stops the Kist offering materials it cannot charge for.");

            Section(sb, Stake.Deposit, "Could affect depositing",
                "If it takes over, an Alt-click may not reach the Kist. Crafting is unaffected, and the " +
                "Kist window still deposits normally.");

            sb.Append("\nThe log line <b>\"Kist paid ...\"</b> appears whenever the vault actually pays, ");
            sb.Append("so you can always tell a real problem from materials simply coming from elsewhere.");
            return sb.ToString();
        }

        private static void Section(StringBuilder sb, Stake stake, string heading, string consequence)
        {
            bool any = false;
            foreach (Conflict c in _found)
            {
                if (c.Stake != stake) continue;
                if (!any) { sb.Append("\n<b>").Append(heading).Append("</b>\n"); any = true; }
                sb.Append("   ").Append(c.Mod).Append(' ')
                  .Append(c.Replaces ? "may take over " : "rewrites ")
                  .Append(c.Method).Append('\n');
            }
            if (any) sb.Append("   ").Append(consequence).Append('\n');
        }
    }
}
