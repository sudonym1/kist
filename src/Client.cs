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
    public Dictionary<string, long> Counts { get; private set; } = new Dictionary<string, long>(StringComparer.Ordinal);
    public int Version { get; private set; }
    public string Status { get; private set; } = "";
    public bool Busy => pending.Count != 0;
    public bool Listing => listRequest != 0;
    public bool Ready => api.Connected && Player.m_localPlayer != null && !Player.m_localPlayer.IsDead();

    public Client(RpcApi api) => this.api = api;

    public void List()
    {
        if (!Ready || Listing) return;
        listRequest = ++nextRequest;
        api.List(listRequest);
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
        api.Deposit(id, Items.Id(original), amount);
    }

    public void Withdrawal(string itemId, bool stack)
    {
        if (!Ready || Busy) return;
        var item = Items.Default(itemId);
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
        if (reply.Error.Length != 0) { Status = reply.Error; return; }
        Counts = reply.Items; // Parsing and allocation already happened on the worker.
        Version++;
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
        SetTotal(item, reply.Total);
        Status = "Deposited " + deposited + ".";
    }

    public void OnWithdrawal(TransferReply reply)
    {
        var id = reply.RequestId;
        if (!pending.TryGetValue(id, out var transfer)) return;
        pending.Remove(id);
        var error = reply.Error;
        if (error.Length != 0) { Status = "Withdrawal failed: " + error; return; }
        var itemId = reply.ItemId;
        var amount = checked((int)reply.Amount);
        SetTotal(itemId, reply.Total);
        if (amount == 0) { Status = "That item is no longer available."; return; }
        var item = Items.Default(itemId);
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
            api.Deposit(returnId, itemId, unused);
        }
    }

    private void SetTotal(string item, long count)
    {
        if (count == 0) Counts.Remove(item);
        else Counts[item] = count;
        Version++;
    }
}
