using System;
using System.Collections.Generic;

namespace SharedInventory;

internal sealed class Client
{
    private sealed class Pending
    {
        public string Item = "";
        public int Amount;
        public Inventory Inventory = null!;
        public ItemDrop.ItemData? Original;
        public UnityEngine.Vector3 Position;
    }

    private readonly RpcApi api;
    private readonly Dictionary<long, Pending> pending = new Dictionary<long, Pending>();
    private long nextRequest;
    private long listRequest;
    private long lastSnapshot;
    private float nextList;
    private bool refreshAgain;
    private readonly Dictionary<long, Dictionary<string, long>> batches = new Dictionary<long, Dictionary<string, long>>();
    private readonly Dictionary<string, long> latestTotals = new Dictionary<string, long>(StringComparer.Ordinal);
    public Dictionary<string, long> Counts { get; private set; } = new Dictionary<string, long>(StringComparer.Ordinal);
    public Dictionary<string, int> Qualities { get; private set; } = new Dictionary<string, int>();
    public int Quality(string item) => Qualities.TryGetValue(item, out var quality) ? quality : 1;
    public int Version { get; private set; }
    public string Status { get; private set; } = "";
    public bool Busy => pending.Count != 0;
    public bool Listing => listRequest != 0;
#if SHAREDINVENTORY_TESTING
    public int PendingTransfers => pending.Count;
    public int PendingBatches => batches.Count;
    public double LastSnapshotAt { get; private set; }
#endif
    public bool Ready => api.Connected && Player.m_localPlayer != null && !Player.m_localPlayer.IsDead();

    public long Available(string item) => Counts.TryGetValue(item, out var count) ? count : 0;

    public Client(RpcApi api) => this.api = api;

    public void Refresh(bool sharedWindow, bool craftingMenu, bool hammerMenu, float now, bool stationInput = false)
    {
        var active = sharedWindow || craftingMenu || hammerMenu || stationInput;
        if (active && now >= nextList)
        {
            nextList = now + 2;
            List();
        }
    }

    public void List()
    {
        if (!Ready || Listing) return;
        listRequest = ++nextRequest;
        api.List(listRequest);
    }

    private void RefreshAfterFailure()
    {
        if (Listing) refreshAgain = true;
        else List();
    }

    // Gameplay already completed. Deduct locally before dispatch so another
    // immediate action cannot spend the same cached materials again.
    public void Consume(Dictionary<string, long> amounts)
    {
        if (amounts.Count == 0 || !Ready) return;
        var id = ++nextRequest;
        batches.Add(id, amounts);
        foreach (var pair in amounts)
        {
            var total = Math.Max(0, Available(pair.Key) - pair.Value);
            if (total == 0) Counts.Remove(pair.Key);
            else Counts[pair.Key] = total;
        }
        Version++;
        api.Withdrawal(id, amounts);
    }

    public void OnBatch(BatchReply reply)
    {
        if (!batches.Remove(reply.RequestId)) return;
        if (reply.Error.Length != 0)
        {
            Status = "Shared material withdrawal failed: " + reply.Error;
            RefreshAfterFailure(); // Deliberately keep the completed craft/build.
            return;
        }
        foreach (var pair in reply.Totals)
            SetTotal(pair.Key, pair.Value, reply.Qualities[pair.Key], reply.RequestId);
    }

    public void Deposit(ItemDrop.ItemData item)
    {
        if (!Ready || Busy || item.m_dropPrefab == null) return;
        var player = Player.m_localPlayer;
        var inventory = player.GetInventory();
        if (!inventory.ContainsItem(item)) return;
        // Equipped items are unequipped locally before removing them.
        if (item.m_equipped) player.UnequipItem(item, false);
        var original = item.Clone();
        var amount = item.m_stack;
        if (amount <= 0 || !inventory.RemoveItem(item)) return;
        var id = ++nextRequest;
        pending.Add(id, new Pending
        {
            Item = Items.Id(original), Amount = amount, Inventory = inventory, Original = original,
            Position = player.transform.position + UnityEngine.Vector3.up
        });
        Status = "Depositing…";
        api.Deposit(id, Items.Id(original), amount, original.m_quality);
    }

    public void Withdrawal(string itemId, bool stack)
    {
        if (!Ready || Busy) return;
        var item = Items.Default(itemId, Quality(itemId));
        if (item == null)
        {
            Status = "This client does not have the item: " + itemId;
            return;
        }
        if (!Counts.TryGetValue(itemId, out var available) || available <= 0) return;
        var maximum = Math.Max(1, item.m_shared.m_maxStackSize);
        var amount = (int)Math.Min(available, stack ? maximum : 1);
        var inventory = Player.m_localPlayer.GetInventory();
        if (Items.Room(inventory, item) < amount || !inventory.CanAddItem(item, amount))
        {
            Status = "Not enough inventory room.";
            return;
        }
        var id = ++nextRequest;
        pending.Add(id, new Pending { Item = itemId, Amount = amount, Inventory = inventory });
        Status = "Withdrawing…";
        api.Withdrawal(id, itemId, amount);
    }

    public void OnList(ListReply reply)
    {
        if (reply.RequestId != listRequest) return;
        listRequest = 0;
        if (reply.Error.Length != 0) Status = reply.Error;
        else
        {
            // A list includes all earlier requests. Preserve newer authoritative
            // totals and subtract only optimistic requests sent after this list.
            foreach (var pair in latestTotals)
            {
                if (pair.Value <= reply.RequestId) continue;
                reply.Items[pair.Key] = Available(pair.Key);
                reply.Qualities[pair.Key] = Quality(pair.Key);
            }
            foreach (var batch in batches)
            {
                if (batch.Key <= reply.RequestId) continue;
                foreach (var pair in batch.Value)
                {
                    if (latestTotals.TryGetValue(pair.Key, out var newer) && newer > reply.RequestId) continue;
                    if (reply.Items.TryGetValue(pair.Key, out var count))
                        reply.Items[pair.Key] = Math.Max(0, count - pair.Value);
                }
            }
            Qualities = reply.Qualities;
            Counts = reply.Items; // Packet parsing/allocation happened on the worker.
            var empty = new List<string>();
            foreach (var pair in Counts) if (pair.Value <= 0) empty.Add(pair.Key);
            foreach (var item in empty) { Counts.Remove(item); Qualities.Remove(item); }
            lastSnapshot = reply.RequestId;
#if SHAREDINVENTORY_TESTING
            if (api.Testing != null) LastSnapshotAt = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
#endif
            Version++;
        }
        if (refreshAgain) { refreshAgain = false; List(); }
    }

    public void OnDeposit(TransferReply reply)
    {
        var id = reply.RequestId;
        if (!pending.TryGetValue(id, out var transfer)) return;
        pending.Remove(id);
        var error = reply.Error;
        if (error.Length != 0)
        {
            if (transfer.Original != null)
            {
                var added = Ready && Player.m_localPlayer.GetInventory() == transfer.Inventory
                    ? Items.Add(transfer.Inventory, transfer.Original, transfer.Amount) : 0;
                var remainder = transfer.Amount - added;
                // A rejected deposit must not destroy the original item if slots changed in flight.
                if (remainder > 0)
                {
                    var dropped = transfer.Original.Clone();
                    dropped.m_stack = remainder;
                    ItemDrop.DropItem(dropped, remainder,
                        transfer.Position,
                        UnityEngine.Quaternion.identity);
                }
            }
            Status = "Deposit failed: " + error;
            return;
        }
        var item = reply.ItemId;
        var deposited = reply.Amount;
        SetTotal(item, reply.Total, reply.Quality, id);
        Status = "Deposited " + deposited + ".";
    }

    public void OnWithdrawal(TransferReply reply)
    {
        var id = reply.RequestId;
        if (!pending.TryGetValue(id, out var transfer)) return;
        pending.Remove(id);
        var error = reply.Error;
        if (error.Length != 0) { Status = "Withdrawal failed: " + error; RefreshAfterFailure(); return; }
        var itemId = reply.ItemId;
        var amount = checked((int)reply.Amount);
        SetTotal(itemId, reply.Total, reply.Quality, id);
        if (amount < transfer.Amount) RefreshAfterFailure();
        if (amount == 0) { Status = "That item is no longer available."; return; }
        var item = Items.Default(itemId, reply.Quality);
        var added = 0;
        // Capacity may have changed during the RPC. Never add to a dead player's
        // detached inventory; return the grant to the database instead.
        if (item != null && Ready && Player.m_localPlayer.GetInventory() == transfer.Inventory)
        {
            var fitting = (int)Math.Min(amount, Items.Room(transfer.Inventory, item));
            if (fitting > 0) added = Items.Add(transfer.Inventory, item, fitting);
        }
        Status = "Withdrew " + added + ".";
        var unused = amount - added;
        if (unused > 0)
        {
            var returnId = ++nextRequest;
            pending.Add(returnId, new Pending
            {
                Item = itemId, Amount = unused, Inventory = transfer.Inventory
            });
            Status = "Returning " + unused + " that did not fit…";
            // Returns are ordinary deposits. No grant token or server-side cap.
            api.Deposit(returnId, itemId, unused, reply.Quality);
        }
    }

    private void SetTotal(string item, long count, int quality, long requestId)
    {
        if (requestId < lastSnapshot) return;
        if (latestTotals.TryGetValue(item, out var latest) && latest > requestId) return;
        latestTotals[item] = requestId;
        foreach (var batch in batches)
            if (batch.Key > requestId && batch.Value.TryGetValue(item, out var amount))
                count = Math.Max(0, count - amount);
        if (count == 0) { Counts.Remove(item); Qualities.Remove(item); }
        else { Counts[item] = count; Qualities[item] = quality; }
        Version++;
    }
}
