using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThorsKist
{
    /// <summary>
    /// The local mirror. Holds whatever the server last said is true, and is the
    /// only thing the UI and the crafting patches ever read -- so a stale mirror
    /// shows a stale Kist rather than letting a client invent one.
    /// </summary>
    public static class KistClient
    {
        public static Hird Hird;                 // null when the player has no hird
        public static Inventory Kist;            // null when the player has no hird
        public static int MinHirdSize = 2;

        public static bool HasPendingInvite;
        public static string PendingInviteHird = "";
        public static string PendingInviteFrom = "";

        public class RosterEntry
        {
            public long PlayerId;
            public string Name = "";
        }

        public static readonly List<RosterEntry> Roster = new List<RosterEntry>();

        /// <summary>Raised whenever the mirror changes, so the UI can redraw.</summary>
        public static Action OnStateChanged;

        private class PendingDeposit
        {
            public int Token;
            public List<ItemDrop.ItemData> Items;
            public float Deadline;
        }

        private static readonly List<PendingDeposit> Pending = new List<PendingDeposit>();
        private static int _nextToken = 1;
        private static float _nextHello;
        private static bool _helloSent;

        public static bool InHird { get { return Hird != null; } }

        public static bool KistUnlocked
        {
            get { return Hird != null && Hird.Members.Count >= MinHirdSize; }
        }

        public static bool IsOwner
        {
            get
            {
                Player p = Player.m_localPlayer;
                return Hird != null && p != null && Hird.OwnerId == p.GetPlayerID();
            }
        }

        public static float DistanceToAnchor
        {
            get
            {
                Player p = Player.m_localPlayer;
                if (Hird == null || p == null) return float.MaxValue;
                return Vector3.Distance(p.transform.position, Hird.Anchor);
            }
        }

        public static bool InRange(float radius)
        {
            if (Main.UnlimitedRange.Value) return true;
            return DistanceToAnchor <= radius;
        }

        public static bool CanDeposit { get { return KistUnlocked && InRange(Main.DepositRadius.Value); } }
        public static bool CanTake { get { return KistUnlocked && InRange(Main.TakeRadius.Value); } }
        public static bool CanCraft
        {
            get { return Main.CraftFromKist.Value && KistUnlocked && InRange(Main.CraftRadius.Value); }
        }

        public static void Reset()
        {
            Hird = null;
            Kist = null;
            Roster.Clear();
            Pending.Clear();
            HasPendingInvite = false;
            _helloSent = false;
            _nextHello = 0f;
        }

        // ------------------------------------------------------------------
        // Outgoing
        // ------------------------------------------------------------------

        private static ZPackage NewMessage(Wire.C2S op)
        {
            Player p = Player.m_localPlayer;
            ZPackage pkg = new ZPackage();
            pkg.Write(Wire.Protocol);
            pkg.Write((byte)op);
            pkg.Write(p != null ? p.GetPlayerID() : 0L);
            pkg.Write(p != null ? p.GetPlayerName() : "");
            pkg.Write(p != null ? p.transform.position : Vector3.zero);
            return pkg;
        }

        private static void Send(ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null) return;
            // The overload without a target peer routes to the server itself --
            // it calls GetServerPeerID() internally, which is private on the
            // real game assembly even though the publicized reference says
            // otherwise. Calling it directly throws MethodAccessException.
            ZRoutedRpc.instance.InvokeRoutedRPC(Wire.RpcToServer, pkg);
        }

        public static void RequestState()
        {
            Send(NewMessage(Wire.C2S.RequestState));
        }

        public static void CreateHird(string name)
        {
            ZPackage pkg = NewMessage(Wire.C2S.CreateHird);
            pkg.Write(name ?? "");
            Send(pkg);
        }

        public static void Invite(long playerId, string name)
        {
            ZPackage pkg = NewMessage(Wire.C2S.Invite);
            pkg.Write(playerId);
            pkg.Write(name ?? "");
            Send(pkg);
        }

        public static void AcceptInvite() { Send(NewMessage(Wire.C2S.AcceptInvite)); }
        public static void DeclineInvite() { Send(NewMessage(Wire.C2S.DeclineInvite)); }
        public static void Leave() { Send(NewMessage(Wire.C2S.Leave)); }
        public static void Disband() { Send(NewMessage(Wire.C2S.Disband)); }
        public static void SetAnchorHere() { Send(NewMessage(Wire.C2S.SetAnchor)); }

        public static void Kick(long playerId)
        {
            ZPackage pkg = NewMessage(Wire.C2S.Kick);
            pkg.Write(playerId);
            Send(pkg);
        }

        /// <summary>
        /// Sends an item to the vault. The item leaves the player's inventory
        /// immediately so the click feels instant; if the server refuses, the
        /// item comes straight back. Nothing is destroyed on either path.
        /// </summary>
        public static bool Deposit(Inventory from, ItemDrop.ItemData item, int amount)
        {
            if (item == null || from == null) return false;
            if (amount <= 0 || amount > item.m_stack) amount = item.m_stack;

            ItemDrop.ItemData sent = item.Clone();
            sent.m_stack = amount;

            // Handles both the whole stack and a partial one, and raises
            // Changed() itself -- Inventory.Changed is private on the real game
            // assembly, so calling it from here would throw at runtime.
            from.RemoveItem(item, amount);

            int token = _nextToken++;
            Pending.Add(new PendingDeposit
            {
                Token = token,
                Items = new List<ItemDrop.ItemData> { sent },
                Deadline = Time.realtimeSinceStartup + 15f,
            });

            ZPackage pkg = NewMessage(Wire.C2S.Deposit);
            pkg.Write(token);
            ZPackage payload = new ZPackage();
            ItemWire.WriteItems(payload, new List<ItemDrop.ItemData> { sent });
            pkg.Write(payload);
            Send(pkg);
            return true;
        }

        public static void Take(Vector2i slot, string expectName, int amount)
        {
            ZPackage pkg = NewMessage(Wire.C2S.Take);
            pkg.Write(_nextToken++);
            pkg.Write(slot);
            pkg.Write(expectName ?? "");
            pkg.Write(amount);
            Send(pkg);
        }

        /// <summary>
        /// Tells the server which materials a craft drew out of the Kist. The
        /// client has already spent them against its mirror; this is what makes
        /// that real.
        /// </summary>
        public static void ReportCraftConsume(List<CraftDraw> draws)
        {
            if (draws == null || draws.Count == 0) return;
            ZPackage pkg = NewMessage(Wire.C2S.ConsumeForCraft);
            pkg.Write(draws.Count);
            foreach (CraftDraw d in draws)
            {
                pkg.Write(d.Name);
                pkg.Write(d.Amount);
                pkg.Write(d.Quality);
            }
            Send(pkg);
        }

        // ------------------------------------------------------------------
        // Incoming
        // ------------------------------------------------------------------

        public static void OnMessage(long sender, ZPackage pkg)
        {
            try
            {
                int protocol = pkg.ReadInt();
                if (protocol != Wire.Protocol)
                {
                    Message("Thor's Kist: version mismatch with the server.");
                    return;
                }
                Wire.S2C op = (Wire.S2C)pkg.ReadByte();
                switch (op)
                {
                    case Wire.S2C.State:
                        ReadState(pkg);
                        break;
                    case Wire.S2C.Toast:
                        Message(pkg.ReadString());
                        break;
                    case Wire.S2C.Denied:
                        Message(pkg.ReadString());
                        break;
                    case Wire.S2C.DepositAck:
                        ClearPending(pkg.ReadInt());
                        break;
                    case Wire.S2C.DepositRejected:
                        RollBack(pkg.ReadInt(), pkg.ReadString());
                        break;
                    case Wire.S2C.AnchorSet:
                        KistFx.Anchor(pkg.ReadVector3());
                        break;
                    case Wire.S2C.TakeResult:
                        ReceiveTake(pkg.ReadInt(), pkg.ReadPackage());
                        break;
                }
            }
            catch (Exception e)
            {
                Main.Log.LogError("ThorsKist: bad reply from the server: " + e);
            }
        }

        private static void ReadState(ZPackage pkg)
        {
            bool hasHird = pkg.ReadBool();
            if (hasHird)
            {
                Hird = Hird.ReadHeader(pkg);
                MinHirdSize = pkg.ReadInt();
                ZPackage inv = pkg.ReadPackage();
                if (Kist == null) Kist = new Inventory("Kist", null, 8, 256);
                Kist.Load(inv);
                HasPendingInvite = false;
            }
            else
            {
                Hird = null;
                Kist = null;
                HasPendingInvite = pkg.ReadBool();
                if (HasPendingInvite)
                {
                    PendingInviteHird = pkg.ReadString();
                    PendingInviteFrom = pkg.ReadString();
                }
            }

            Roster.Clear();
            int roster = pkg.ReadInt();
            for (int i = 0; i < roster; i++)
            {
                RosterEntry e = new RosterEntry();
                e.PlayerId = pkg.ReadLong();
                e.Name = pkg.ReadString();
                Roster.Add(e);
            }

            if (OnStateChanged != null) OnStateChanged();
        }

        private static void ClearPending(int token)
        {
            Pending.RemoveAll(p => p.Token == token);
        }

        private static void RollBack(int token, string why)
        {
            PendingDeposit p = Pending.Find(x => x.Token == token);
            Pending.RemoveAll(x => x.Token == token);
            if (p != null) GiveBack(p.Items);
            if (!string.IsNullOrEmpty(why)) Message(why);
        }

        private static void ReceiveTake(int token, ZPackage payload)
        {
            List<ItemDrop.ItemData> items = ItemWire.ReadItems(payload);
            if (items.Count == 0) return;
            GiveBack(items);
            // Only a real take gets the pickup sound. A rollback also lands in
            // GiveBack, and celebrating a refused deposit would be a lie.
            KistFx.Take();
        }

        /// <summary>
        /// Puts items into the player's hands, or on the ground at their feet if
        /// there is no room. An item that cannot be carried is never discarded.
        /// </summary>
        private static void GiveBack(List<ItemDrop.ItemData> items)
        {
            Player p = Player.m_localPlayer;
            if (p == null) return;
            Inventory inv = p.GetInventory();
            foreach (ItemDrop.ItemData item in items)
            {
                if (inv.CanAddItem(item, item.m_stack))
                {
                    inv.AddItem(item);
                }
                else
                {
                    DropAtFeet(p, item);
                    Message("$msg_yourinventoryisfull");
                }
            }
            // AddItem raises Changed() itself; Inventory.Changed is private on
            // the real assembly, so calling it here would throw.
        }

        private static void DropAtFeet(Player p, ItemDrop.ItemData item)
        {
            // Vanilla's own drop path: it instantiates the right prefab, writes
            // the item into the ZDO and starts the pickup timer. Building the
            // drop by hand would need ItemDrop.Save(), which is private on the
            // real game assembly.
            Vector3 pos = p.transform.position + p.transform.forward * 1.5f + Vector3.up;
            ItemDrop.DropItem(item, item.m_stack, pos, Quaternion.identity);
        }

        public static void Message(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, text);
            else
                Main.Log.LogInfo("ThorsKist: " + text);
        }

        // ------------------------------------------------------------------
        // Tick
        // ------------------------------------------------------------------

        public static void Tick()
        {
            KistServer.Tick();

            if (ZNet.instance == null || Player.m_localPlayer == null)
            {
                if (_helloSent) Reset();
                return;
            }

            if (!_helloSent && Time.realtimeSinceStartup >= _nextHello)
            {
                _nextHello = Time.realtimeSinceStartup + 2f;
                if (ZRoutedRpc.instance != null && Player.m_localPlayer.GetPlayerID() != 0L)
                {
                    Send(NewMessage(Wire.C2S.Hello));
                    _helloSent = true;
                }
            }

            // A deposit whose answer never arrives must not swallow the item.
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Time.realtimeSinceStartup < Pending[i].Deadline) continue;
                List<ItemDrop.ItemData> items = Pending[i].Items;
                Pending.RemoveAt(i);
                GiveBack(items);
                Message("The Kist did not answer. Your items were returned.");
            }
        }
    }

    public class CraftDraw
    {
        public string Name;
        public int Amount;
        public int Quality = -1;
    }
}
