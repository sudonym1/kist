using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace SharedInventory;

internal static class ChestConstruction
{
    private const string RefundableKey = "local.sharedinventory.essencePaid";
    private const string RefundedKey = "local.sharedinventory.essenceReturned";
    private static readonly AccessTools.FieldRef<Player, GameObject> ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
    private static readonly AccessTools.FieldRef<Player, Player.PlacementStatus> status = AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");
    private static readonly Action<Player, bool> setGhostValid = AccessTools.MethodDelegate<Action<Player, bool>>(AccessTools.Method(typeof(Player), "SetPlacementGhostValid"));
    private static readonly HashSet<Piece> removals = new HashSet<Piece>();
    private static Placement? placing;

    private sealed class Placement
    {
        internal EssenceBinding Binding;
        internal bool Paid;
        internal SharedChest? Chest;
    }

    internal static long World => ZNet.instance != null && ZNet.instance.GetWorld() != null ? ZNet.instance.GetWorldUID() : 0;
    private static bool IsChest(Piece piece) => piece != null && piece.GetComponent<SharedChest>() != null;
    private static bool Free(Player player, Piece piece) => player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey());
    internal static Vector3 Target(Player player)
    {
        var preview = ghost(player);
        return preview != null && preview.activeInHierarchy && preview.GetComponent<SharedChest>() != null ? preview.transform.position : player.transform.position;
    }

    // Called at the real game's placement boundary, after vanilla geometry,
    // support, ward and location checks, using the final ghost coordinates.
    internal static bool Place(Player player, Piece piece, Vector3 pos, Quaternion rot, bool doAttack, bool cheated)
    {
        if (!IsChest(piece)) { player.PlacePiece(piece, pos, rot, doAttack, cheated); return true; }
        var inventory = player.GetInventory();
        var free = Free(player, piece);
        var selected = free ? null : EssenceSelection.Select(inventory, World, pos);
        if (!free && selected == null)
        {
            player.Message(MessageHud.MessageType.Center, "Carry an Einherjar Essence or a Dimmed Essence bound to this area.");
            return false;
        }
        var binding = selected != null && Items.Id(selected) == ContentIds.DimmedEssence && EssenceBinding.TryRead(selected, out var original)
            ? original : new EssenceBinding(World, pos);
        var payment = selected?.Clone();
        if (payment != null) payment.m_stack = 1;
        if (selected != null && !inventory.RemoveItem(selected, 1)) return false;
        var previous = placing;
        var placement = new Placement { Binding = binding, Paid = !free };
        placing = placement;
        try
        {
            player.PlacePiece(piece, pos, rot, doAttack, cheated);
            return placement.Chest != null;
        }
        finally
        {
            placing = previous;
            // If construction never reached IPlaced, restore the exact payment,
            // including its binding. A completed chest keeps its payment.
            if (placement.Chest == null && payment != null && !inventory.AddItem(payment) && payment.m_stack > 0)
                ItemDrop.DropItem(payment, payment.m_stack, player.transform.position + Vector3.up, Quaternion.identity);
        }
    }

    internal static void OnPlaced(SharedChest chest)
    {
        var view = chest.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner()) return;
        var binding = placing?.Binding ?? new EssenceBinding(World, chest.transform.position);
        var record = view.GetZDO();
        record.Set(EssenceBinding.Key, binding.Encode());
        // Direct/admin placement must not mint a recoverable material.
        record.Set(RefundableKey, placing?.Paid == true);
        if (placing != null) placing.Chest = chest;
    }

    private static void Refund(Piece piece)
    {
        var view = piece.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner()) return;
        var record = view.GetZDO();
        if (!record.GetBool(RefundableKey, true) || record.GetBool(RefundedKey)) return;
        var encoded = record.GetString(EssenceBinding.Key, "");
        // Legacy chests have no binding: their existing site becomes the origin.
        if (encoded.Length == 0) encoded = new EssenceBinding(World, piece.transform.position).Encode();
        if (!EssenceBinding.TryRead(encoded, out var binding) || binding.World != World) return;
        var item = Items.Default(ContentIds.DimmedEssence);
        if (item == null) throw new InvalidOperationException("Dimmed Einherjar Essence is not registered.");
        item.m_customData[EssenceBinding.Key] = encoded;
        record.Set(EssenceBinding.Key, encoded);
        record.Set(RefundedKey, true);
        ItemDrop.DropItem(item, 1, piece.transform.position + Vector3.up, Quaternion.identity);
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
    private static class Requirements
    {
        private static bool Prefix(Player __instance, Piece piece, Player.RequirementMode mode, ref bool __result)
        {
            if (!IsChest(piece)) return true;
            __result = mode == Player.RequirementMode.IsKnown
                ? __instance.IsMaterialKnown("$item_einherjaressence") || __instance.IsMaterialKnown("$item_dimmedeinherjaressence")
                : Free(__instance, piece) || EssenceSelection.Select(__instance.GetInventory(), World, Target(__instance)) != null;
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
    private static class Preview
    {
        private static void Postfix(Player __instance)
        {
            var preview = ghost(__instance);
            if (preview == null || !preview.activeInHierarchy || preview.GetComponent<SharedChest>() == null ||
                status(__instance) != Player.PlacementStatus.Valid || Free(__instance, preview.GetComponent<Piece>())) return;
            if (EssenceSelection.Select(__instance.GetInventory(), World, preview.transform.position) != null) return;
            status(__instance) = Player.PlacementStatus.Invalid;
            setGhostValid(__instance, false);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
    private static class PlacementHook
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var place = AccessTools.Method(typeof(Player), nameof(Player.PlacePiece));
            var count = 0;
            for (var i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(place)) continue;
                // Vanilla returns true immediately after its void PlacePiece.
                // Return our actual result instead, so denial cannot spend tools
                // or report successful placement. Fail loudly if that IL changes.
                if (i + 1 >= code.Count || code[i + 1].opcode != OpCodes.Ldc_I4_1)
                    throw new InvalidOperationException("Einherjar chest placement return hook changed.");
                code[i].opcode = OpCodes.Call;
                code[i].operand = AccessTools.Method(typeof(ChestConstruction), nameof(Place));
                code.RemoveAt(i + 1);
                count++;
            }
            if (count != 1) throw new InvalidOperationException("Einherjar chest placement hook was not found exactly once.");
            return code;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
    private static class Payment
    {
        private static bool Prefix(Piece.Requirement[] requirements) => Content.Chest == null ||
            !ReferenceEquals(requirements, Content.Chest.GetComponent<Piece>().m_resources);
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
    private static class RequirementDisplay
    {
        private static void Prefix(Player player, ref Piece.Requirement req, out ItemDrop.ItemData? __state)
        {
            __state = null;
            if (Content.Chest == null || !ReferenceEquals(req, Content.Chest.GetComponent<Piece>().m_resources[0])) return;
            __state = EssenceSelection.Select(player.GetInventory(), World, Target(player));
            if (__state != null && Items.Id(__state) == ContentIds.DimmedEssence)
                req = new Piece.Requirement { m_resItem = Content.DimmedEssence!.GetComponent<ItemDrop>(), m_amount = 1, m_recover = false };
        }
        private static void Postfix(Transform elementRoot, ItemDrop.ItemData? __state)
        {
            if (__state != null && Items.Id(__state) == ContentIds.DimmedEssence)
                elementRoot.GetComponent<UITooltip>().m_text = __state.GetTooltip();
        }
    }

    [HarmonyPatch(typeof(WearNTear), "RPC_Remove")]
    private static class HammerRemoval
    {
        private static void Prefix(WearNTear __instance, bool blockDrop, out Piece? __state)
        {
            __state = null;
            var view = __instance.GetComponent<ZNetView>();
            if (blockDrop || __instance.GetComponent<SharedChest>() == null || view == null || !view.IsValid() || !view.IsOwner()) return;
            var piece = __instance.GetComponent<Piece>();
            if (removals.Add(piece)) __state = piece;
        }
        private static void Finalizer(Piece? __state) { if (!ReferenceEquals(__state, null)) removals.Remove(__state!); }
    }

    [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
    private static class ResourceDrop
    {
        private static void Postfix(Piece __instance) { if (removals.Contains(__instance)) Refund(__instance); }
    }

    internal static string BindingText(ItemDrop.ItemData item)
    {
        if (!EssenceBinding.TryRead(item, out var binding)) return "This essence has no valid bound site and cannot rebuild a chest.";
        return binding.Describe(World, Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (Vector3?)null);
    }

    [HarmonyPatch]
    private static class Tooltip
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip),
            new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) });
        private static void Prefix(ItemDrop.ItemData item)
        {
            if (item.m_dropPrefab != null && !ContentIds.CanShare(Items.Id(item))) item.m_shared.m_teleportable = Content.EssencePortalsAllowed;
        }
        private static void Postfix(ItemDrop.ItemData item, ref string __result)
        {
            if (item.m_dropPrefab != null && Items.Id(item) == ContentIds.DimmedEssence) __result += "\n\n" + BindingText(item);
        }
    }

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.GetHoverText))]
    private static class DroppedTooltip
    {
        private static void Postfix(ItemDrop __instance, ref string __result)
        {
            if (__instance.m_itemData.m_dropPrefab != null && Items.Id(__instance.m_itemData) == ContentIds.DimmedEssence)
                __result += "\n" + BindingText(__instance.m_itemData);
        }
    }
}
