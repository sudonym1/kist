using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    internal sealed partial class JournalGameplaySession
    {
        private sealed class BuildPayment
        {
            internal ItemCommand Command;
            internal string Name;
            internal int Quality;
        }
        private readonly Dictionary<Guid, BuildPayment> buildPayments = new Dictionary<Guid, BuildPayment>();
        internal int PendingBuildPayments => buildPayments.Count;
        private int PendingBuildStack(Guid id) => buildPayments.Values.Where(p => p.Command.StackId == id).Sum(p => p.Command.Item.Quantity);
        private int PendingBuildCount(string name, int quality) => buildPayments.Values.Where(p => p.Name == name && (quality < 0 || p.Quality == quality)).Sum(p => p.Command.Item.Quantity);

        // Called only when paying an already successful vanilla placement.
        // All planning is in memory; neither the click nor placement awaits IO.
        internal bool PayBuild(IReadOnlyList<(string Name, int Quality, int Quantity)> needs)
        {
            if (!AccessEnabled || Procuring || JournalReservationGuard.Busy(inventory)) return false;
            using var timing = JournalDiagnostics.Measure("unity.hammer.submit", expected: JournalTimingThread.Main);
            var planned = new List<BuildPayment>();
            var held = new Dictionary<Guid, int>();
            foreach (var need in needs)
            {
                int remaining = need.Quantity;
                foreach (var row in Stock.Rows.Where(r => r.Item != null && r.Item.m_shared.m_name == need.Name && (need.Quality < 0 || r.Item.m_quality == need.Quality)))
                {
                    held.TryGetValue(row.Stack.Id, out int already);
                    int take = Math.Min(remaining, row.Stack.Item.Quantity - PendingBuildStack(row.Stack.Id) - already);
                    if (take <= 0) continue;
                    var item = row.Stack.Item;
                    planned.Add(new BuildPayment { Name = need.Name, Quality = row.Item.m_quality,
                        Command = new ItemCommand(Client.Address, Guid.NewGuid(), row.Stack.Id, ItemCommandKind.Withdraw,
                            new ItemRecord(item.Prefab, take, item.FormatVersion, item.CopyPayload()), ItemCommandPurpose.Craft) });
                    held[row.Stack.Id] = already + take; remaining -= take;
                    if (remaining == 0) break;
                }
                if (remaining != 0) return false;
            }
            if (planned.Count > 48 - buildPayments.Count) return false;
            foreach (var payment in planned)
            {
                buildPayments.Add(payment.Command.RequestId, payment);
                character.ExpectBuildPayment(payment.Command.RequestId, payment.Command.Item);
            }
            foreach (var payment in planned)
                Observe(Task.Run(() => Client.ExecuteAsync(payment.Command)));
            return true;
        }
    }

    internal static class JournalBuilding
    {
        private static Player placedBy;
        private static Piece.Requirement[] placedResources;
        private static JournalGameplaySession placedSession;
        private static int placedFrame = -1;
        internal static void Placed(Player player, Piece piece, bool success)
        {
            placedBy = null; placedResources = null; placedSession = null;
            if (!success || player != Player.m_localPlayer || player.NoCostCheat() || !KistClient.CanCraft) return;
            placedBy = player; placedResources = piece.m_resources;
            placedSession = JournalGameplaySession.Current; placedFrame = Time.frameCount;
        }
        internal static void Consume(Player player, Piece.Requirement[] resources, int quality, int itemQuality, int multiplier)
        {
            if (!ReferenceEquals(player, placedBy) || !ReferenceEquals(resources, placedResources) || placedFrame != Time.frameCount) return;
            var session = placedSession;
            // One-use match to vanilla's immediate post-placement payment.
            placedBy = null; placedResources = null; placedSession = null;
            if (session == null || !ReferenceEquals(session, JournalGameplaySession.Current) || player.NoCostCheat()) return;
            var inventory = player.GetInventory();
            var station = player.GetCurrentCraftingStation();
            var amounts = new Dictionary<string, int>();
            foreach (var req in resources)
            {
                if (req.m_resItem == null || (station != null ? station.m_upgrader != req.m_upgraderResource : req.m_upgraderResource)) continue;
                int amount = req.GetAmount(quality) * multiplier; if (amount <= 0) continue;
                string name = req.m_resItem.m_itemData.m_shared.m_name;
                amounts.TryGetValue(name, out int total); amounts[name] = checked(total + amount);
            }
            var needs = new List<(string Name, int Quality, int Quantity)>();
            foreach (var amount in amounts)
            {
                int local = inventory.GetAllItems().Where(i => i.m_shared.m_name == amount.Key && (itemQuality < 0 || i.m_quality == itemQuality)
                    && i.m_worldLevel >= Game.m_worldLevel && !JournalReservationGuard.Blocks(i)).Sum(i => i.m_stack);
                if (amount.Value > local) needs.Add((amount.Key, itemQuality, amount.Value - local));
            }
            if (needs.Count != 0 && !session.PayBuild(needs))
                Main.Log.LogWarning("Kist hammer payment unavailable after optimistic placement");
            // Vanilla still consumes its local share immediately. Journal-only
            // materials are settled later, without adding them to inventory.
        }
    }

    [HarmonyPatch]
    internal static class Patches_JournalBuilding
    {
        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        [HarmonyPostfix]
        private static void Placed(Player __instance, Piece piece, bool __result) => JournalBuilding.Placed(__instance, piece, __result);
        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
        [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
        private static void Consume(Player __instance, Piece.Requirement[] requirements, int qualityLevel, int itemQuality, int multiplier)
            => JournalBuilding.Consume(__instance, requirements, qualityLevel, itemQuality, multiplier);
    }
}
