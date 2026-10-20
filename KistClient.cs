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

        // Whether the server would currently accept a re-anchor, and why not.
        // The distance rule needs everyone's position, which only the server
        // has, so this is asked for rather than worked out locally.
        public static bool AnchorAllowed;
        public static string AnchorReason = "";
        public static string AnchorCostPrefab = "";
        public static int AnchorCostAmount;

        /// <summary>Raised whenever the mirror changes, so the UI can redraw.</summary>
        public static Action OnStateChanged;

        private static float _nextHello;
        private static bool _helloSent;
        private static float _nextPing;
        private static float _nextAnchorQuery;

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

        public static bool CanDeposit { get { return KistUnlocked && InRange(Main.DepositRadius.Value) && (JournalGameplaySession.Current != null && JournalGameplaySession.Current.AccessEnabled); } }
        public static bool CanTake { get { return KistUnlocked && InRange(Main.TakeRadius.Value) && (JournalGameplaySession.Current != null && JournalGameplaySession.Current.AccessEnabled); } }
        public static bool CanCraft
        {
            get { return JournalGameplaySession.Current != null && JournalGameplaySession.Current.AccessEnabled && Main.CraftFromKist.Value && KistUnlocked && InRange(Main.CraftRadius.Value); }
        }

        public static void Reset()
        {
            JournalStationInputs.Clear();
            JournalGameplaySession.Current?.Dispose();
            JournalGameplaySession.Current = null;
            Hird = null;
            Kist = null;
            Roster.Clear();
            HasPendingInvite = false;
            AnchorAllowed = false;
            AnchorReason = "";
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
            JournalControlRpc.SendRequest(pkg);
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
        public static void QueryAnchor() { Send(NewMessage(Wire.C2S.AnchorQuery)); }

        public static void RenameHird(string name)
        {
            ZPackage pkg = NewMessage(Wire.C2S.Rename);
            pkg.Write(name ?? "");
            Send(pkg);
        }

        /// <summary>
        /// How many of the re-anchor's cost item the player is carrying. The
        /// cost is a SERVER setting, so the name arrives over the wire and is
        /// resolved against the local prefab database here.
        /// </summary>
        public static int AnchorCostHeld()
        {
            if (string.IsNullOrEmpty(AnchorCostPrefab) || AnchorCostAmount <= 0) return int.MaxValue;
            string shared = SharedNameOf(AnchorCostPrefab);
            Player p = Player.m_localPlayer;
            if (shared == null || p == null) return 0;
            return p.GetInventory().CountItems(shared);
        }

        public static string AnchorCostLabel()
        {
            if (string.IsNullOrEmpty(AnchorCostPrefab) || AnchorCostAmount <= 0) return "";
            string shared = SharedNameOf(AnchorCostPrefab);
            string nice = shared != null && Localization.instance != null
                ? Localization.instance.Localize(shared) : AnchorCostPrefab;
            return AnchorCostAmount + " " + nice;
        }

        private static string SharedNameOf(string prefabName)
        {
            if (ObjectDB.instance == null) return null;
            GameObject go = ObjectDB.instance.GetItemPrefab(prefabName);
            if (go == null) return null;
            ItemDrop drop = go.GetComponent<ItemDrop>();
            return drop != null ? drop.m_itemData.m_shared.m_name : null;
        }

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
            var session = JournalGameplaySession.Current;
            return session != null && CanDeposit && session.Deposit(from, item, amount <= 0 && item != null ? item.m_stack : amount);
        }

        internal static Action TakeAction(ItemDrop.ItemData item, int amount)
        {
            var session = JournalGameplaySession.Current;
            if (session != null)
            {
                var id = session.Stock.Identify(item);
                return () => { if (ReferenceEquals(session, JournalGameplaySession.Current) && CanTake) session.Take(id, amount); };
            }
            return () => { };
        }

        /// <summary>
        /// Sends one line of diagnostic text to the server's log.
        ///
        /// Crafting and station decisions are made on the client, so when the
        /// Kist declines to help, the only evidence lands in a log file on the
        /// player's machine. Routing it to the server means whoever is
        /// debugging can read it without asking the player to go hunting.
        /// </summary>
        public static void ReportDiag(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Main.Log.LogInfo("ThorsKist: " + text);
            ZPackage pkg = NewMessage(Wire.C2S.Diag);
            pkg.Write(text);
            Send(pkg);
        }

        // ------------------------------------------------------------------
        // Incoming
        // ------------------------------------------------------------------

        public static void OnMessage(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || (ZNet.instance.IsServer() ? sender != ZNet.GetUID() : ZNet.instance.GetServerPeer() == null || sender != ZNet.instance.GetServerPeer().m_uid)) return;
            try
            {
                int protocol = pkg.ReadInt();
                if (protocol != Wire.Protocol)
                {
                    Message("Thor's Kist: version mismatch with the server.");
                    return;
                }
                Wire.S2C op = (Wire.S2C)pkg.ReadByte();
                if ((op == Wire.S2C.TakeResult
                    || op == Wire.S2C.DepositAck || op == Wire.S2C.DepositRejected)) return;
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
                    case Wire.S2C.AnchorSet:
                        OnAnchorSet(pkg.ReadVector3(), pkg.ReadLong());
                        break;
                    case Wire.S2C.DepositFx:
                        KistFx.DepositAt(pkg.ReadVector3());
                        break;
                    case Wire.S2C.AnchorStatus:
                    {
                        bool ok = pkg.ReadBool();
                        string reason = pkg.ReadString();
                        string costPrefab = pkg.ReadString();
                        int costAmount = pkg.ReadInt();
                        // This arrives every couple of seconds while the Hird tab
                        // is open. Redrawing on every one would throw away the
                        // scroll position for no reason.
                        bool changed = ok != AnchorAllowed || reason != AnchorReason ||
                                       costPrefab != AnchorCostPrefab || costAmount != AnchorCostAmount;
                        AnchorAllowed = ok;
                        AnchorReason = reason;
                        AnchorCostPrefab = costPrefab;
                        AnchorCostAmount = costAmount;
                        if (changed) KistUI.MarkDirty();
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Main.Log.LogError("ThorsKist: bad reply from the server: " + e);
            }
        }

        private static void ReadState(ZPackage pkg)
        {
            _helloSent = true;
            bool hasHird = pkg.ReadBool();
            if (hasHird)
            {
                Hird = Hird.ReadHeader(pkg);
                MinHirdSize = pkg.ReadInt();
                var epoch = Guid.ParseExact(pkg.ReadString(), "N");
                Kist = null;
                JournalWorldOwner.SelectVault(new ThorsKist.Sync.VaultIdentity(ZNet.instance.GetWorldUID().ToString(), Hird.Id, epoch));
                HasPendingInvite = false;
            }
            else
            {
                Hird = null;
                Kist = null;
                JournalWorldOwner.SelectVault(null);
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

        /// <summary>
        /// The Kist has landed somewhere new. Everyone in the hird sees it; only
        /// the member the server named as payer spends the cost, and only after
        /// the move is already a fact.
        /// </summary>
        private static void OnAnchorSet(Vector3 pos, long payerPlayerId)
        {
            KistFx.Anchor(pos);

            Player p = Player.m_localPlayer;
            if (p == null || payerPlayerId == 0L || payerPlayerId != p.GetPlayerID()) return;
            if (string.IsNullOrEmpty(AnchorCostPrefab) || AnchorCostAmount <= 0) return;

            string shared = SharedNameOf(AnchorCostPrefab);
            if (shared == null) return;
            p.GetInventory().RemoveItem(shared, AnchorCostAmount);
            Message("The Kist took " + AnchorCostLabel() + ".");
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

                }
            }

            if (InHird && Time.realtimeSinceStartup >= _nextPing)
            {
                // Cheapest possible heartbeat: the server reads the position out
                // of the message header every message already carries.
                _nextPing = Time.realtimeSinceStartup + 3f;
                Send(NewMessage(Wire.C2S.Ping));
            }

            // Only while someone is actually looking at the button.
            if (InHird && KistUI.WantsAnchorStatus && Time.realtimeSinceStartup >= _nextAnchorQuery)
            {
                _nextAnchorQuery = Time.realtimeSinceStartup + 1.5f;
                QueryAnchor();
            }


        }
    }

}
