using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ThorsKist.Sync;

namespace ThorsKist
{
    internal static class JournalStationInputs
    {
        internal const string Request = "ThorsKist_StationInput_v1";
        private const string Reply = "ThorsKist_StationAdded_v1";
        private sealed class Pending
        {
            internal ZNetView View;
            internal Component Station;
            internal bool Fuel;
            internal JournalGameplaySession Session;
            internal string Name;
            internal float Deadline;
        }
        private static readonly Dictionary<Guid, Pending> pending = new Dictionary<Guid, Pending>();
        private static ZNetView View(Component station) => AccessTools.Field(station.GetType(), "m_nview")?.GetValue(station) as ZNetView;
        internal static void Clear() => pending.Clear();
        internal static void Register(Component station)
        {
            var view = View(station); if (view == null || !view.IsValid()) return;
            view.Register<ZPackage>(Request, (sender, package) =>
            {
                if (!view.IsValid() || !view.IsOwner() || package.Size() > 512) return;
                try
                {
                    var reader = new ZPackage(package.GetArray());
                    var id = Guid.ParseExact(reader.ReadString(), "N"); bool fuel = reader.ReadBool();
                    string prefab = reader.ReadString(); bool cheated = reader.ReadBool();
                    if (reader.GetPos() != reader.Size()) return;
                    bool added = Apply(station, fuel, prefab, cheated);
                    var reply = new ZPackage(); reply.Write(id.ToString("N")); reply.Write(added);
                    view.InvokeRPC(sender, Reply, reply);
                }
                catch (Exception error) { Main.Log.LogWarning("Kist station insertion not confirmed: " + error.Message); }
            });
            view.Register<ZPackage>(Reply, (sender, package) =>
            {
                if (package.Size() > 128) return;
                var reader = new ZPackage(package.GetArray());
                try { var id = Guid.ParseExact(reader.ReadString(), "N"); bool added = reader.ReadBool();
                    if (reader.GetPos() == reader.Size()) Complete(view, sender, id, added); }
                catch (Exception error) { Main.Log.LogWarning("Invalid Kist station reply: " + error.Message); }
            });
        }
        internal static void Complete(ZNetView view, long sender, Guid id, bool added)
        {
            if (view == null || !view.IsValid() || sender != view.GetZDO().GetOwner()
                || !pending.TryGetValue(id, out var input) || !ReferenceEquals(input.View, view)) return;
            pending.Remove(id);
            if (added && input.Deadline >= Time.realtimeSinceStartup && ReferenceEquals(input.Session, JournalGameplaySession.Current))
            { Pay(input.Session, input.Name); Feedback(input.Station, input.Fuel); }
        }
        private static void Pay(JournalGameplaySession session, string name)
        {
            // The station has already consumed the input. A refused payment
            // leaves it there, just like an optimistic hammer placement.
            if (!session.PayStation(name)) Main.Log.LogWarning("Kist station payment unavailable after optimistic insertion");
        }
        private static void Feedback(Component station, bool fuel)
        {
            if (station is Smelter smelter && !fuel)
            {
                AccessTools.Field(typeof(Smelter), "m_addedOreTime").SetValue(smelter, Time.time);
                if (smelter.m_addOreAnimationDuration > 0)
                    AccessTools.Method(typeof(Smelter), "SetAnimation").Invoke(smelter, new object[] { true });
            }
            if (station is CookingStation cooking && !fuel && cooking.m_skill != Skills.SkillType.None && Player.m_localPlayer != null)
                Player.m_localPlayer.RaiseSkill(cooking.m_skill, 0.4f);
        }
        internal static bool Insert(Component station, bool fuel, ItemDrop.ItemData item)
        {
            using var timing = JournalDiagnostics.Measure("unity.station.insert", expected: JournalTimingThread.Main);
            var view = View(station); var session = JournalGameplaySession.Current;
            if (view == null || !view.IsValid() || item?.m_dropPrefab == null || session == null || !session.AccessEnabled) return false;
            if (!view.HasOwner()) view.ClaimOwnership();
            if (view.IsOwner())
            {
                if (!Apply(station, fuel, item.m_dropPrefab.name, item.m_cheated)) return false;
                Pay(session, item.m_shared.m_name); Feedback(station, fuel); return true;
            }
            foreach (var stale in pending.Where(p => p.Value.Deadline < Time.realtimeSinceStartup || !ReferenceEquals(p.Value.Session, session)).Select(p => p.Key).ToArray()) pending.Remove(stale);
            if (pending.Count >= 48) return false;
            var id = Guid.NewGuid(); pending.Add(id, new Pending { View = view, Station = station, Fuel = fuel, Session = session, Name = item.m_shared.m_name, Deadline = Time.realtimeSinceStartup + 31f });
            var request = new ZPackage(); request.Write(id.ToString("N")); request.Write(fuel); request.Write(item.m_dropPrefab.name); request.Write(item.m_cheated);
            try { view.InvokeRPC(Request, request); }
            catch { pending.Remove(id); throw; }
            return true;
        }
        internal static bool Apply(Component station, bool fuel, string prefab, bool cheated)
        {
            var view = View(station); if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            var zdo = view.GetZDO(); string rpc; object[] args;
            float beforeFuel = zdo.GetFloat(ZDOVars.s_fuel); int slot = -1, beforeQueue = zdo.GetInt(ZDOVars.s_queued);
            if (fuel)
            {
                var input = AccessTools.Field(station.GetType(), "m_fuelItem")?.GetValue(station) as ItemDrop;
                if (input == null || input.gameObject.name != prefab) return false;
                float limit = station is Smelter sm ? sm.m_maxFuel : station is CookingStation cs ? cs.m_maxFuel : station is Fireplace fire ? fire.m_maxFuel : 0;
                if (station is Fireplace f && (!f.m_canRefill || f.m_infiniteFuel || Mathf.CeilToInt(beforeFuel) >= limit)) return false;
                if (beforeFuel > limit - 1) return false;
                rpc = "RPC_AddFuel"; args = new object[] { view.GetZDO().GetOwner() };
            }
            else if (station is Smelter smelter)
            {
                if (beforeQueue >= smelter.m_maxOre || !(bool)AccessTools.Method(typeof(Smelter), "IsItemAllowed", new[] { typeof(string) }).Invoke(station, new object[] { prefab })) return false;
                rpc = "RPC_AddOre"; args = new object[] { view.GetZDO().GetOwner(), prefab, cheated };
            }
            else if (station is CookingStation cooking)
            {
                if (!(bool)AccessTools.Method(typeof(CookingStation), "IsItemAllowed", new[] { typeof(string) }).Invoke(station, new object[] { prefab })
                    || cooking.m_requireFire && !(bool)AccessTools.Method(typeof(CookingStation), "IsFireLit").Invoke(station, null)) return false;
                slot = (int)AccessTools.Method(typeof(CookingStation), "GetFreeSlot").Invoke(station, null); if (slot < 0) return false;
                rpc = "RPC_AddItem"; args = new object[] { view.GetZDO().GetOwner(), prefab, cheated };
            }
            else if (station is Fermenter)
            {
                int hash = prefab.GetStableHashCode();
                if (zdo.GetInt(ZDOVars.s_content) != 0 || !(bool)AccessTools.Method(typeof(Fermenter), "IsItemAllowed", new[] { typeof(int) }).Invoke(station, new object[] { hash })) return false;
                rpc = "RPC_AddItem"; args = new object[] { view.GetZDO().GetOwner(), hash, cheated };
            }
            else return false;
            AccessTools.Method(station.GetType(), rpc).Invoke(station, args);
            return fuel ? zdo.GetFloat(ZDOVars.s_fuel) > beforeFuel
                : station is Smelter ? zdo.GetInt(ZDOVars.s_queued) == beforeQueue + 1
                : station is CookingStation ? zdo.GetString("slot" + slot) == prefab
                : zdo.GetInt(ZDOVars.s_content) == prefab.GetStableHashCode();
        }
    }
    [HarmonyPatch]
    internal static class Patches_JournalStationInputs
    {
        [HarmonyPatch(typeof(Smelter), "Awake")]
        [HarmonyPostfix] private static void SmelterReady(Smelter __instance) => JournalStationInputs.Register(__instance);
        [HarmonyPatch(typeof(CookingStation), "Awake")]
        [HarmonyPostfix] private static void CookingReady(CookingStation __instance) => JournalStationInputs.Register(__instance);
        [HarmonyPatch(typeof(Fireplace), "Awake")]
        [HarmonyPostfix] private static void FireplaceReady(Fireplace __instance) => JournalStationInputs.Register(__instance);
        [HarmonyPatch(typeof(Fermenter), "Awake")]
        [HarmonyPostfix] private static void FermenterReady(Fermenter __instance) => JournalStationInputs.Register(__instance);
    }
}
