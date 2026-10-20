using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    // Only the native test fixture's network view is intercepted. Production
    // station selection/capacity/payment code and real inventory methods run.
    [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.InvokeRPC), new[] { typeof(string), typeof(object[]) })]
    internal static class StationInputTransportProbe
    {
        internal static ZNetView View;
        internal static readonly List<(string Method, object[] Args)> Calls = new List<(string, object[])>();
        [HarmonyPrefix] private static bool Prefix(ZNetView __instance, string method, object[] parameters)
        {
            if (!ReferenceEquals(__instance, View)) return true;
            Calls.Add((method, parameters)); return false;
        }
    }
    [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.IsOwner))]
    internal static class StationInputOwnerProbe
    {
        internal static bool Owned;
        internal static bool Mutate = true;
        [HarmonyPrefix] private static bool Prefix(ZNetView __instance, ref bool __result)
        {
            if (!ReferenceEquals(__instance, StationInputTransportProbe.View)) return true;
            __result = Owned; return false;
        }
    }
    [HarmonyPatch]
    internal static class StationInputOwnerUpdateProbe
    {
        private static bool Update(Smelter station, bool fuel)
        {
            var view = (ZNetView)AccessTools.Field(typeof(Smelter), "m_nview").GetValue(station);
            if (!ReferenceEquals(view, StationInputTransportProbe.View)) return true;
            if (StationInputOwnerProbe.Mutate)
            {
                var zdo = view.GetZDO();
                if (fuel) ZDOExtraData.Set(zdo.m_uid, ZDOVars.s_fuel, zdo.GetFloat(ZDOVars.s_fuel) + 1f);
                else ZDOExtraData.Set(zdo.m_uid, ZDOVars.s_queued, zdo.GetInt(ZDOVars.s_queued) + 1);
            }
            return false;
        }
        [HarmonyPatch(typeof(Smelter), "RPC_AddOre")]
        [HarmonyPrefix] private static bool Ore(Smelter __instance) => Update(__instance, false);
        [HarmonyPatch(typeof(Smelter), "RPC_AddFuel")]
        [HarmonyPrefix] private static bool Fuel(Smelter __instance) => Update(__instance, true);
    }
    public sealed partial class AdapterTests
    {
        private IEnumerator ImmediateGameplay(Inventory inventory)
        {
            int start = checks;
            string name = Wood(1).m_shared.m_name;
            var objects = new List<GameObject>(); var zdos = new List<ZDO>();
            // CraftContext intentionally rejects destroyed/fake Unity players.
            // Use a real inactive component, with Awake/world setup suppressed.
            var playerObject = new GameObject("immediate crafting fixture"); playerObject.SetActive(false); objects.Add(playerObject);
            var player = playerObject.AddComponent<Player>();
            AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, inventory);
            var previous = Player.m_localPlayer;
            Func<Type, Component> station = type =>
            {
                var go = new GameObject("immediate station fixture"); go.SetActive(false); objects.Add(go);
                var view = go.AddComponent<ZNetView>(); var component = go.AddComponent(type);
                var zdo = new ZDO { m_uid = new ZDOID(823741, (uint)(50000 + zdos.Count)) }; zdos.Add(zdo);
                AccessTools.Field(typeof(ZDO), "m_prefab").SetValue(zdo, 0);
                ZDOExtraData.SetOwner(zdo.m_uid, ZDOID.AddUser(823741));
                AccessTools.Property(typeof(ZDO), "Owned").SetValue(zdo, true);
                AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
                AccessTools.Field(type, "m_nview").SetValue(component, view);
                StationInputTransportProbe.View = view; return component;
            };
            try
            {
                Player.m_localPlayer = player;
                inventory.GetAllItems().Clear(); var local = Wood(2); inventory.GetAllItems().Add(local);
                int stock = cutoverSession.Count(name); long sequence = cutoverSession.Client.Sequence;
                var resources = new[] { new Piece.Requirement { m_resItem = ObjectDB.instance.GetItemPrefab("Wood").GetComponent<ItemDrop>(), m_amount = 5 } };
                JournalMaterials.NativeCraft = true;
                CraftContext.Push();
                try
                {
                    Check(inventory.CountItems(name) == stock + 2, "Craft requirement queries see local plus cached Kist stock immediately");
                    player.ConsumeResources(resources, 0);
                }
                finally { CraftContext.Pop(); JournalMaterials.NativeCraft = false; }
                Check(inventory.GetAllItems().Count == 0 && cutoverSession.Count(name) == stock - 3
                    && cutoverSession.Client.Sequence == sequence, "Craft consumes two local ingredients and queues only three Kist ingredients without waiting");
                JournalMaterials.NativeCraft = true; CraftContext.Push();
                try
                {
                    Check(inventory.GetItem(name, 1) != null && inventory.GetAllItems().Count == 0,
                        "Single-ingredient selection can inspect Kist metadata without inserting it into the pack");
                    inventory.RemoveItem(name, 2, 1);
                }
                finally { CraftContext.Pop(); JournalMaterials.NativeCraft = false; }
                Check(cutoverSession.Count(name) == stock - 5 && inventory.GetAllItems().Count == 0,
                    "Single-ingredient direct RemoveItem pays Kist asynchronously without inventory delivery");
                inventory.RemoveItem(name, 10);
                Check(cutoverSession.Count(name) == stock - 5, "Unrelated inventory removal outside DoCrafting cannot charge Kist");
                var wood = ObjectDB.instance.GetItemPrefab("Wood").GetComponent<ItemDrop>();
                var smelter = (Smelter)station(typeof(Smelter)); smelter.m_fuelItem = wood;
                smelter.m_conversion.Add(new Smelter.ItemConversion { m_from = wood, m_to = wood });
                var addOre = AccessTools.Method(typeof(Smelter), "OnAddOre");
                var args = new object[] { null, player, null };
                ZDOExtraData.Set(zdos.Last().m_uid, ZDOVars.s_queued, smelter.m_maxOre);
                Check(JournalMaterials.Station(smelter, addOre, args, out bool handled) && !handled && StationInputTransportProbe.Calls.Count == 0,
                    "Full station never inserts or queues payment");
                ZDOExtraData.Set(zdos.Last().m_uid, ZDOVars.s_queued, 0);
                inventory.GetAllItems().Add(Wood(1));
                Check(JournalMaterials.Station(smelter, addOre, args, out handled) && !handled && StationInputTransportProbe.Calls.Count == 0,
                    "Station preserves vanilla local-item preference");
                inventory.GetAllItems().Clear();
                var filler = ObjectDB.instance.GetItemPrefab("Stone");
                for (int y = 0; y < inventory.GetHeight(); y++) for (int x = 0; x < inventory.GetWidth(); x++)
                { var item = filler.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab = filler; item.m_stack = 50; item.m_gridPos = new Vector2i(x, y); inventory.GetAllItems().Add(item); }
                int pack = inventory.GetAllItems().Sum(i => i.m_stack);
                Check(!JournalMaterials.Station(smelter, addOre, args, out handled) && handled
                    && cutoverSession.Count(name) == stock - 5, "Full-pack kiln input dispatches immediately without charging before owner insertion");
                var request = new ZPackage(((ZPackage)StationInputTransportProbe.Calls.Last().Args[0]).GetArray());
                var id = Guid.ParseExact(request.ReadString(), "N");
                Check(StationInputTransportProbe.Calls.Last().Method == JournalStationInputs.Request && !request.ReadBool() && request.ReadString() == "Wood",
                    "Station input request carries exact selected prefab without an inventory item");
                JournalStationInputs.Complete(StationInputTransportProbe.View, 823742, id, true);
                Check(cutoverSession.Count(name) == stock - 5, "Another peer cannot authorize station payment");
                JournalStationInputs.Complete(StationInputTransportProbe.View, 823741, id, true);
                JournalStationInputs.Complete(StationInputTransportProbe.View, 823741, id, true);
                Check(cutoverSession.Count(name) == stock - 6, "Successful station insertion starts one payment; duplicate reply cannot charge twice");
                var addFuel = AccessTools.Method(typeof(Smelter), "OnAddFuel");
                Check(!JournalMaterials.Station(smelter, addFuel, args, out handled) && handled, "Smelter fuel dispatches with a full pack");
                request = new ZPackage(((ZPackage)StationInputTransportProbe.Calls.Last().Args[0]).GetArray()); id = Guid.ParseExact(request.ReadString(), "N");
                Check(request.ReadBool() && request.ReadString() == "Wood", "Fuel request preserves selected input");
                JournalStationInputs.Complete(StationInputTransportProbe.View, 823741, id, false);
                Check(cutoverSession.Count(name) == stock - 6, "Owner refusal never charges Kist");
                JournalMaterials.Station(smelter, addFuel, args, out handled);
                request = new ZPackage(((ZPackage)StationInputTransportProbe.Calls.Last().Args[0]).GetArray()); id = Guid.ParseExact(request.ReadString(), "N");
                JournalStationInputs.Complete(StationInputTransportProbe.View, 823741, id, true);
                StationInputOwnerProbe.Owned = true;
                ZDOExtraData.Set(zdos.Last().m_uid, ZDOVars.s_queued, smelter.m_maxOre);
                Check(!JournalStationInputs.Apply(smelter, false, "Wood", false), "Owner rechecks station room before consuming");
                ZDOExtraData.Set(zdos.Last().m_uid, ZDOVars.s_queued, 0);
                Check(JournalStationInputs.Apply(smelter, false, "Wood", false) && zdos.Last().GetInt(ZDOVars.s_queued) == 1,
                    "Owner confirms insertion only after station quantity increases");
                StationInputOwnerProbe.Mutate = false;
                Check(!JournalStationInputs.Apply(smelter, false, "Wood", false), "Native insertion with no station state change cannot authorize payment");
                StationInputOwnerProbe.Mutate = true;
                Check(!JournalStationInputs.Apply(smelter, false, "Stone", false), "Owner refuses unsupported input");
                ZDOExtraData.Set(zdos.Last().m_uid, ZDOVars.s_fuel, (float)smelter.m_maxFuel);
                Check(!JournalStationInputs.Apply(smelter, true, "Wood", false), "Owner refuses full fuel tank");
                StationInputOwnerProbe.Owned = false;
                var cooking = (CookingStation)station(typeof(CookingStation));
                Check(JournalStationInputs.Insert(cooking, false, Wood(1)), "Cooking input uses the same immediate request path");
                Check(JournalStationInputs.Insert(cooking, true, Wood(1)), "Cooking fuel uses the same immediate request path");
                var fermenter = station(typeof(Fermenter));
                Check(JournalStationInputs.Insert(fermenter, false, Wood(1)), "Fermenter input uses the same immediate request path");
                var fire = station(typeof(Fireplace));
                Check(JournalStationInputs.Insert(fire, true, Wood(1)), "Fireplace fuel uses the same immediate request path");
                AccessTools.Field(typeof(Fireplace), "m_nview").SetValue(fire, null);
                Check(!JournalStationInputs.Insert(fire, true, Wood(1)), "Invalid station view refuses insertion");
                Check(cutoverSession.Client.Sequence == sequence && cutoverSession.PendingImmediatePayments >= 4,
                    "Multiple crafting and station actions return before journal replies and share pending stock holds");
                yield return CutoverDrive(() => cutoverSession.PendingImmediatePayments == 0 && cutoverSession.Client.Sequence > sequence);
                Check(inventory.GetAllItems().Sum(i => i.m_stack) == pack && inventory.GetAllItems().All(i => i.m_dropPrefab.name == "Stone"),
                    "Verified crafting/station payments never add ingredients or invoke inventory consumption after the action");
                Check(cutoverSession.Count(name) == stock - 7, "Normal background settlement charges exactly five craft ingredients and two station inputs");
                var read = cutoverWorld.ReadAsync(cutoverPeer.Vault); while (!read.IsCompleted) yield return null;
                Check(read.Result.Results.Where(r => r.Command.Purpose == ItemCommandPurpose.Station).Any(), "Station payments retain station range/purpose authorization");
                var deleteStack = cutoverSession.Stock.Rows.First(r => r.Item != null && r.Item.m_shared.m_name == name).Stack;
                int beforeDelete = cutoverSession.Count(name);
                Check(cutoverSession.Delete(deleteStack.Id) && !cutoverSession.Delete(deleteStack.Id),
                    "Delete queues the whole selected stack and blocks a repeated click while pending");
                Check(cutoverSession.Count(name) == beforeDelete - deleteStack.Item.Quantity,
                    "Pending deletion cannot be spent by crafting or station payments");
                yield return CutoverDrive(() => cutoverSession.PendingImmediatePayments == 0 && !cutoverSession.Stock.Find(deleteStack.Id, out _));
                Check(inventory.GetAllItems().Sum(i => i.m_stack) == pack && inventory.GetAllItems().All(i => i.m_dropPrefab.name == "Stone"),
                    "Delete commits a withdrawal with a full pack without delivering the removed stack");
                read = cutoverWorld.ReadAsync(cutoverPeer.Vault); while (!read.IsCompleted) yield return null;
                Check(read.Result.Results.Any(r => r.Command.StackId == deleteStack.Id && r.Command.Kind == ItemCommandKind.Withdraw
                    && r.Command.Purpose == ItemCommandPurpose.Inventory && r.Command.Item.Quantity == deleteStack.Item.Quantity
                    && r.Status == CommandStatus.Accepted), "Delete follows the ordinary whole-stack inventory withdrawal path");
                CutoverLog("M7 IMMEDIATE GAMEPLAY PASS " + (checks - start) + " native checks; craft Kist=5 local=2, station Kist=2; no inventory bounce; station transport captured, capacity/inventory/payment code native");
            }
            finally
            {
                Player.m_localPlayer = previous; JournalMaterials.NativeCraft = false; JournalStationInputs.Clear(); StationInputTransportProbe.View = null; StationInputOwnerProbe.Owned = false; StationInputOwnerProbe.Mutate = true;
                foreach (var zdo in zdos) ZDOExtraData.Release(zdo, zdo.m_uid);
                foreach (var go in objects) UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
