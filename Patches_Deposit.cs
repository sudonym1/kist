using HarmonyLib;
using UnityEngine;

namespace ThorsKist
{
    /// <summary>
    /// The deposit gesture: hold the modifier, left-click an item.
    ///
    /// Every inventory click -- mouse, gamepad and touch alike -- funnels through
    /// InventoryGui.OnSelectedItem, which is why the hook sits there rather than
    /// on a mouse handler. Right-click is not used: vanilla already spends it on
    /// equip/consume, and taking it would break muscle memory.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
    public static class Patches_Deposit
    {
        // ___m_dragItem is Harmony's field injection. InventoryGui.m_dragItem is
        // private on the real game assembly -- reading it through the publicized
        // reference compiles but throws FieldAccessException at runtime.
        [HarmonyPrefix]
        public static bool Prefix(InventoryGrid grid, ItemDrop.ItemData item, ItemDrop.ItemData ___m_dragItem)
        {
            if (!Main.ModEnable.Value) return true;
            if (item == null) return true;
            if (!Main.DepositModifier.Value.IsPressed()) return true;

            // Mid-drag the click means "drop what I am holding"; hijacking it
            // would make the held item vanish into the vault unasked.
            if (___m_dragItem != null) return true;

            Player player = Player.m_localPlayer;
            if (player == null) return true;

            Inventory playerInv = player.GetInventory();
            if (grid == null || grid.GetInventory() != playerInv) return true;

            if (!KistClient.InHird)
            {
                KistClient.Message("You have no hird. Press " + Main.OpenKistKey.Value.MainKey + " to found one.");
                return false;
            }
            if (!KistClient.KistUnlocked)
            {
                KistClient.Message("The Kist stays shut until the hird has " + KistClient.MinHirdSize + " members.");
                return false;
            }
            if (!KistClient.InRange(Main.DepositRadius.Value))
            {
                KistClient.Message("Too far from the Kist (" +
                                   Mathf.RoundToInt(KistClient.DistanceToAnchor) + "m of " +
                                   Mathf.RoundToInt(Main.DepositRadius.Value) + "m).");
                return false;
            }
            if (item.m_shared.m_questItem)
            {
                KistClient.Message("$msg_cantstoreitem");
                return false;
            }
            if (item.m_equipped)
            {
                player.UnequipItem(item, false);
            }

            if (KistClient.Deposit(playerInv, item, item.m_stack))
            {
                KistFx.Deposit();
            }
            return false;
        }
    }
}
