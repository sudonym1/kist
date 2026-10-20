using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace ThorsKist
{
    /// <summary>
    /// The authority. Runs on whoever owns the world -- a dedicated server, or
    /// the host in a listen game. Clients never mutate a Kist themselves; they
    /// ask, and what comes back is the truth.
    /// </summary>
    public static class KistServer
    {
        private class Vault
        {
            public Hird Hird;
        }

        private static readonly List<Vault> Vaults = new List<Vault>();
        private static readonly Dictionary<long, long> PeerByPlayer = new Dictionary<long, long>();
        // Character identities are checked against the actual authenticated
        // direct peer and its owned character ZDO before entering this map.
        private static readonly Dictionary<long, string> NameByPlayer = new Dictionary<long, string>();
        // Positions are captured from the authenticated character ZDO (or the
        // local listen-host character), never from the claimed wire header.
        private static readonly Dictionary<long, Vector3> PosByPlayer = new Dictionary<long, Vector3>();
        private static readonly Dictionary<long, float> PosSeenAt = new Dictionary<long, float>();
        private static bool _loaded;
        private static bool _dirty;

        public static bool IsServer
        {
            get { return ZNet.instance != null && ZNet.instance.IsServer(); }
        }

        // ------------------------------------------------------------------
        // Persistence
        // ------------------------------------------------------------------

        internal static bool Ready => _loaded && JournalWorldOwner.Ready;
        public static void EnsureLoaded() { JournalWorldOwner.EnsureStarted(); }
        internal static void InstallHeaders(IEnumerable<byte[]> headers)
        {
            Vaults.Clear();
            foreach (var header in headers)
            {
                var hird = Hird.ReadHeader(new ZPackage(header));
                Vaults.Add(new Vault { Hird = hird });
            }
            _loaded = true;
        }
        internal static byte[][] Headers()
        {
            var result = new List<byte[]>();
            foreach (var v in Vaults) { var p = new ZPackage(); v.Hird.WriteHeader(p); result.Add(p.GetArray()); }
            return result.ToArray();
        }
        private static void MarkDirty() { _dirty = true; }
        public static void Tick() { JournalWorldOwner.Tick(); }
        public static void FlushNow() { }
        public static void Reset()
        {
            JournalWorldOwner.Stop();
            Vaults.Clear(); PeerByPlayer.Clear(); NameByPlayer.Clear(); PosByPlayer.Clear(); PosSeenAt.Clear();
            _loaded = false; _dirty = false;
        }

        // ------------------------------------------------------------------
        // Lookup
        // ------------------------------------------------------------------

        private static Vault VaultOf(long playerId)
        {
            foreach (Vault v in Vaults)
                if (v.Hird.IsMember(playerId)) return v;
            return null;
        }

        private static Vault VaultById(string hirdId)
        {
            foreach (Vault v in Vaults)
                if (v.Hird.Id == hirdId) return v;
            return null;
        }

        private static Vault VaultWithInviteFor(long playerId)
        {
            foreach (Vault v in Vaults)
                if (v.Hird.IsInvited(playerId)) return v;
            return null;
        }

        // ------------------------------------------------------------------
        // Receive
        // ------------------------------------------------------------------

        public static void OnMessage(long senderPeer, ZPackage pkg)
        {
            if (!IsServer) return;
            EnsureLoaded();
            if (!Ready || JournalWorldOwner.MetadataBusy || pkg.Size() > 64 * 1024)
            {
                JournalConnectionDiagnostics.State("server-" + senderPeer, "control waiting peer=" + senderPeer
                    + " authorityReady=" + Ready + " metadataBusy=" + JournalWorldOwner.MetadataBusy + " bytes=" + pkg.Size());
                return;
            }
            var replies = new List<Action>();
            pendingReplies = replies;
            try
            {
                int protocol = pkg.ReadInt();
                if (protocol != Wire.Protocol)
                {
                    Deny(senderPeer, "Your Thor's Kist version does not match the server's.");
                    return;
                }
                Wire.C2S op = (Wire.C2S)pkg.ReadByte();
                long playerId = pkg.ReadLong();
                string playerName = pkg.ReadString();
                Vector3 pos = pkg.ReadVector3();
                if (!JournalWorldOwner.Authenticate(senderPeer, playerId, out pos))
                {
                    if (op == Wire.C2S.Hello || op == Wire.C2S.RequestState)
                        JournalConnectionDiagnostics.State("server-" + senderPeer, "control identity waiting: "
                            + JournalConnectionDiagnostics.Peer(ZNet.instance.GetPeer(senderPeer), playerId));
                    return;
                }
                playerName = senderPeer == ZNet.GetUID() ? Player.m_localPlayer.GetPlayerName() : ZNet.instance.GetPeer(senderPeer).m_playerName;
                if (op == Wire.C2S.Deposit || op == Wire.C2S.Take || op == Wire.C2S.ConsumeForCraft)
                { Deny(senderPeer, "Journal item session required."); return; }

                if (playerId != 0L)
                {
                    PeerByPlayer[playerId] = senderPeer;
                    if (!string.IsNullOrEmpty(playerName)) NameByPlayer[playerId] = playerName;
                    // Every message carries the sender's position, so the
                    // heartbeat is really just the cheapest of them.
                    PosByPlayer[playerId] = pos;
                    PosSeenAt[playerId] = Time.realtimeSinceStartup;
                }

                switch (op)
                {
                    case Wire.C2S.Hello:
                        JournalConnectionDiagnostics.State("server-" + senderPeer, "hello authenticated peer=" + senderPeer
                            + " character=" + playerId + " name=" + playerName + " hird=" + (VaultOf(playerId)?.Hird.Id ?? "none"));
                        // A new arrival changes who everyone else can invite,
                        // so the whole population gets a fresh roster, not just
                        // the player who just said hello.
                        BroadcastRoster();
                        SendState(playerId);
                        break;
                    case Wire.C2S.RequestState:
                        SendState(playerId);
                        break;
                    case Wire.C2S.CreateHird:
                        CreateHird(senderPeer, playerId, playerName, pos, pkg.ReadString());
                        break;
                    case Wire.C2S.Invite:
                        Invite(senderPeer, playerId, playerName, pkg.ReadLong(), pkg.ReadString());
                        break;
                    case Wire.C2S.AcceptInvite:
                        AcceptInvite(senderPeer, playerId, playerName);
                        break;
                    case Wire.C2S.DeclineInvite:
                        DeclineInvite(playerId);
                        break;
                    case Wire.C2S.Kick:
                        Kick(senderPeer, playerId, pkg.ReadLong());
                        break;
                    case Wire.C2S.Leave:
                        Leave(playerId);
                        break;
                    case Wire.C2S.Rename:
                        Rename(senderPeer, playerId, pkg.ReadString());
                        break;
                    case Wire.C2S.Disband:
                        Disband(senderPeer, playerId);
                        break;
                    case Wire.C2S.Ping:
                        break;   // the header above already did the work
                    case Wire.C2S.AnchorQuery:
                        SendAnchorStatus(senderPeer, playerId, pos);
                        break;
                    case Wire.C2S.SetAnchor:
                        SetAnchor(senderPeer, playerId, pos);
                        break;
                    case Wire.C2S.Deposit:
                        break;
                    case Wire.C2S.Take:
                        break;
                    case Wire.C2S.Diag:
                        // Info, not Warning. These only arrive when a station
                        // genuinely could not take something, which is worth a
                        // line but is not the server's problem to act on.
                        Main.Log.LogInfo("ThorsKist [" + playerName + "] " + pkg.ReadString());
                        break;

                    case Wire.C2S.ConsumeForCraft:
                        break;
                }
            }
            catch (Exception e)
            {
                replies.Clear(); Main.Log.LogError("ThorsKist: bad message from peer " + senderPeer + ": " + e);
            }
            finally
            {
                pendingReplies = null;
                if (_dirty) { _dirty = false; JournalWorldOwner.CommitHeaders(Headers(), replies); }
                else foreach (var reply in replies) reply();
            }
        }

        // ------------------------------------------------------------------
        // Hird management
        // ------------------------------------------------------------------

        private static void CreateHird(long peer, long playerId, string playerName, Vector3 pos, string name)
        {
            if (VaultOf(playerId) != null)
            {
                Deny(peer, "You already belong to a hird.");
                return;
            }
            if (string.IsNullOrEmpty(name)) name = playerName + "'s hird";
            if (name.Length > 40) name = name.Substring(0, 40);

            Hird h = new Hird
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                OwnerId = playerId,
                Anchor = pos,
            };
            h.Members.Add(new HirdMember(playerId, playerName));
            Vault created = new Vault { Hird = h };
            Vaults.Add(created);
            MarkDirty();
            AnnounceAnchor(created, 0L);   // founding is free
            Toast(peer, "Hird \"" + name + "\" founded. Invite at least " +
                        (Main.MinHirdSize.Value - 1) + " more to unlock the Kist.");
            SendState(playerId);
        }

        private static void Invite(long peer, long playerId, string playerName, long targetId, string targetName)
        {
            Vault v = VaultOf(playerId);
            if (v == null) { Deny(peer, "You are not in a hird."); return; }
            if (v.Hird.OwnerId != playerId) { Deny(peer, "Only the hird's founder may invite."); return; }
            if (v.Hird.IsMember(targetId)) { Deny(peer, targetName + " is already in your hird."); return; }
            if (VaultOf(targetId) != null) { Deny(peer, targetName + " already belongs to another hird."); return; }
            if (v.Hird.IsInvited(targetId)) { Deny(peer, targetName + " has already been invited."); return; }

            v.Hird.Invites.Add(new HirdInvite
            {
                PlayerId = targetId,
                Name = targetName,
                FromPlayerId = playerId,
                FromName = playerName,
            });
            MarkDirty();
            Toast(peer, "Invited " + targetName + ".");
            SendStateToHird(v);
        }

        private static void AcceptInvite(long peer, long playerId, string playerName)
        {
            Vault v = VaultWithInviteFor(playerId);
            if (v == null) { Deny(peer, "That invitation is no longer open."); return; }
            if (VaultOf(playerId) != null) { Deny(peer, "You already belong to a hird."); return; }

            v.Hird.Invites.RemoveAll(i => i.PlayerId == playerId);
            v.Hird.Members.Add(new HirdMember(playerId, playerName));
            MarkDirty();
            SendStateToHird(v);
            ToastHird(v, playerName + " has joined the hird.");
        }

        private static void DeclineInvite(long playerId)
        {
            Vault v = VaultWithInviteFor(playerId);
            if (v == null) return;
            v.Hird.Invites.RemoveAll(i => i.PlayerId == playerId);
            MarkDirty();
            SendStateToHird(v);
            SendState(playerId);
        }

        private static void Kick(long peer, long playerId, long targetId)
        {
            Vault v = VaultOf(playerId);
            if (v == null) { Deny(peer, "You are not in a hird."); return; }
            if (v.Hird.OwnerId != playerId) { Deny(peer, "Only the hird's founder may remove members."); return; }
            if (targetId == playerId) { Deny(peer, "Disband the hird instead."); return; }

            string name = v.Hird.MemberName(targetId);
            v.Hird.Members.RemoveAll(m => m.PlayerId == targetId);
            v.Hird.Invites.RemoveAll(i => i.PlayerId == targetId);
            MarkDirty();
            ToastHird(v, name + " was removed from the hird.");
            SendStateToHird(v);
            SendState(targetId);
        }

        private static void Leave(long playerId)
        {
            Vault v = VaultOf(playerId);
            if (v == null) return;
            if (v.Hird.Members.Count == 1) { Deny(PeerByPlayer[playerId], "Disband the empty hird before leaving."); return; }
            if (v.Hird.OwnerId == playerId)
            {
                // The founder leaving hands the hird to whoever joined next
                // rather than stranding everyone else's belongings.
                v.Hird.Members.RemoveAll(m => m.PlayerId == playerId);
                if (v.Hird.Members.Count > 0) v.Hird.OwnerId = v.Hird.Members[0].PlayerId;
            }
            else
            {
                v.Hird.Members.RemoveAll(m => m.PlayerId == playerId);
            }
            MarkDirty();
            SendStateToHird(v);
            SendState(playerId);
        }

        /// <summary>
        /// Renames the hird. Only the name changes -- the hird's id is a GUID
        /// and everything else keys off that, so a rename cannot orphan a vault
        /// or a membership.
        /// </summary>
        private static void Rename(long peer, long playerId, string name)
        {
            Vault v = VaultOf(playerId);
            if (v == null) { Deny(peer, "You are not in a hird."); return; }
            if (v.Hird.OwnerId != playerId) { Deny(peer, "Only the hird's founder may rename it."); return; }

            name = (name ?? "").Trim();
            if (name.Length == 0) { Deny(peer, "A hird needs a name."); return; }
            if (name.Length > 40) name = name.Substring(0, 40);
            if (name == v.Hird.Name) return;

            string was = v.Hird.Name;
            v.Hird.Name = name;
            MarkDirty();
            ToastHird(v, "\"" + was + "\" is now \"" + name + "\".");
            SendStateToHird(v);
        }

        private static void Disband(long peer, long playerId)
        {
            Vault v = VaultOf(playerId);
            if (v == null) return;
            if (v.Hird.OwnerId != playerId) { Deny(peer, "Only the hird's founder may disband it."); return; }
            JournalWorldOwner.Retire(v.Hird.Id, () =>
            {
                var members = new List<HirdMember>(v.Hird.Members);
                Vaults.Remove(v);
                foreach (var member in members) SendState(member.PlayerId);
            });
        }

        /// <summary>
        /// Whether the hird may re-anchor its Kist at <paramref name="at"/>.
        ///
        /// Moving the vault should mean the hird moved, not that one member
        /// walked off with everything. So every member who is online has to be
        /// standing there, and enough of them have to be online that the rule
        /// cannot be satisfied alone. Members who are offline cannot be asked to
        /// attend, so they are not counted -- the minimum-online check is what
        /// stops that becoming a loophole.
        /// </summary>
        private static bool MayReanchor(Vault v, Vector3 at, out string why)
        {
            why = null;
            int online = 0;
            foreach (HirdMember m in v.Hird.Members)
            {
                if (!IsConnected(m.PlayerId)) continue;
                online++;

                float seen;
                Vector3 where;
                if (!PosByPlayer.TryGetValue(m.PlayerId, out where) ||
                    !PosSeenAt.TryGetValue(m.PlayerId, out seen) ||
                    Time.realtimeSinceStartup - seen > 20f)
                {
                    why = "Still waiting to hear where " + m.Name + " is.";
                    return false;
                }

                float d = Vector3.Distance(where, at);
                if (d > Main.AnchorRadius.Value)
                {
                    why = m.Name + " is " + Mathf.RoundToInt(d) + "m away. Everyone must be within " +
                          Mathf.RoundToInt(Main.AnchorRadius.Value) + "m.";
                    return false;
                }
            }

            if (online < Main.MinHirdSize.Value)
            {
                why = "The hird must be gathered: " + online + " of " + Main.MinHirdSize.Value + " online.";
                return false;
            }
            return true;
        }

        private static void SendAnchorStatus(long peer, long playerId, Vector3 at)
        {
            Vault v = VaultOf(playerId);
            ZPackage pkg = NewReply(Wire.S2C.AnchorStatus);
            if (v == null || v.Hird.OwnerId != playerId)
            {
                pkg.Write(false);
                pkg.Write(v == null ? "You are not in a hird." : "Only the hird's founder may move the Kist.");
            }
            else
            {
                string why;
                bool ok = MayReanchor(v, at, out why);
                pkg.Write(ok);
                pkg.Write(ok ? "" : why);
            }
            // The cost lives in the host's config, so the client cannot know it
            // without being told.
            pkg.Write(Main.AnchorCostPrefab.Value ?? "");
            pkg.Write(Main.AnchorCostAmount.Value);
            SendToPeer(peer, pkg);
        }

        private static void SetAnchor(long peer, long playerId, Vector3 pos)
        {
            Vault v = VaultOf(playerId);
            if (v == null) { Deny(peer, "You are not in a hird."); return; }
            if (v.Hird.OwnerId != playerId) { Deny(peer, "Only the hird's founder may move the Kist."); return; }

            string why;
            if (!MayReanchor(v, pos, out why)) { Deny(peer, why); return; }

            v.Hird.Anchor = pos;
            MarkDirty();
            ToastHird(v, "The Kist has been anchored to a new location.");
            // playerId is named as the payer, so only that client spends the
            // cost -- everyone else just watches it land.
            AnnounceAnchor(v, playerId);
            SendStateToHird(v);
        }

        // ------------------------------------------------------------------
        // Items
        // ------------------------------------------------------------------

        private static ZPackage NewReply(Wire.S2C op)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(Wire.Protocol);
            pkg.Write((byte)op);
            return pkg;
        }

        private static List<Action> pendingReplies;
        private static void SendToPeer(long peer, ZPackage pkg)
        {
            if (pendingReplies != null) { var bytes = pkg.GetArray(); pendingReplies.Add(() => SendToPeer(peer, new ZPackage(bytes))); return; }
            JournalControlRpc.SendReply(peer, pkg);
        }

        private static void SendToPlayer(long playerId, ZPackage pkg)
        {
            long peer;
            if (PeerByPlayer.TryGetValue(playerId, out peer)) SendToPeer(peer, pkg);
        }

        private static void Deny(long peer, string message)
        {
            ZPackage pkg = NewReply(Wire.S2C.Denied);
            pkg.Write(message);
            SendToPeer(peer, pkg);
        }

        private static void Toast(long peer, string message)
        {
            ZPackage pkg = NewReply(Wire.S2C.Toast);
            pkg.Write(message);
            SendToPeer(peer, pkg);
        }

        private static void ToastHird(Vault v, string message)
        {
            foreach (HirdMember m in v.Hird.Members)
            {
                ZPackage pkg = NewReply(Wire.S2C.Toast);
                pkg.Write(message);
                SendToPlayer(m.PlayerId, pkg);
            }
        }

        /// <summary>
        /// Tells the hird a Kist anchor was planted, so everyone nearby sees and
        /// hears it land rather than only the person who pressed the button.
        /// </summary>
        private static void AnnounceAnchor(Vault v, long payerPlayerId)
        {
            foreach (HirdMember m in v.Hird.Members)
            {
                ZPackage pkg = NewReply(Wire.S2C.AnchorSet);
                pkg.Write(v.Hird.Anchor);
                pkg.Write(payerPlayerId);
                SendToPlayer(m.PlayerId, pkg);
            }
        }

        /// <summary>
        /// Shows a member's deposit to the rest of the hird. The depositor has
        /// already played it locally so their own click stays instant; sending
        /// it back to them too would double the sound.
        /// </summary>
        private static void AnnounceDeposit(Vault v, long byPlayerId, Vector3 at)
        {
            foreach (HirdMember m in v.Hird.Members)
            {
                if (m.PlayerId == byPlayerId) continue;
                ZPackage pkg = NewReply(Wire.S2C.DepositFx);
                pkg.Write(at);
                SendToPlayer(m.PlayerId, pkg);
            }
        }

        private static void Ack(long peer, int token)
        {
            ZPackage pkg = NewReply(Wire.S2C.DepositAck);
            pkg.Write(token);
            SendToPeer(peer, pkg);
        }

        private static void Reject(long peer, int token, string why)
        {
            ZPackage pkg = NewReply(Wire.S2C.DepositRejected);
            pkg.Write(token);
            pkg.Write(why ?? "");
            SendToPeer(peer, pkg);
        }

        private static void SendState(long playerId)
        {
            Vault v = VaultOf(playerId);
            ZPackage pkg = NewReply(Wire.S2C.State);
            if (v == null)
            {
                pkg.Write(false);
                // A player with no hird still needs to see invitations waiting
                // for them, or accepting one would be impossible.
                Vault inviting = VaultWithInviteFor(playerId);
                if (inviting != null)
                {
                    pkg.Write(true);
                    pkg.Write(inviting.Hird.Name);
                    HirdInvite mine = inviting.Hird.Invites.Find(i => i.PlayerId == playerId);
                    pkg.Write(mine != null ? mine.FromName : "");
                }
                else pkg.Write(false);
            }
            else
            {
                pkg.Write(true);
                v.Hird.WriteHeader(pkg);
                pkg.Write(Main.MinHirdSize.Value);
                var identity = JournalWorldOwner.Identity(v.Hird.Id);
                // New hirds publish only after the metadata transaction commits.
                pkg.Write(identity != null ? identity.Epoch.ToString("N") : JournalWorldOwner.PendingEpoch(v.Hird.Id).ToString("N"));
            }
            WriteRoster(pkg, playerId);
            SendToPlayer(playerId, pkg);
        }

        /// <summary>
        /// Who the caller could invite: characters currently connected, known to
        /// this mod, and not already spoken for by a hird.
        /// </summary>
        private static void WriteRoster(ZPackage pkg, long forPlayer)
        {
            List<KeyValuePair<long, string>> free = new List<KeyValuePair<long, string>>();
            foreach (KeyValuePair<long, string> kv in NameByPlayer)
            {
                if (kv.Key == forPlayer) continue;
                if (!IsConnected(kv.Key)) continue;
                if (VaultOf(kv.Key) != null) continue;
                free.Add(kv);
            }
            pkg.Write(free.Count);
            foreach (KeyValuePair<long, string> kv in free)
            {
                pkg.Write(kv.Key);
                pkg.Write(kv.Value);
            }
        }

        private static bool IsConnected(long playerId)
        {
            long peer;
            if (!PeerByPlayer.TryGetValue(playerId, out peer)) return false;
            if (ZNet.instance == null) return false;
            // The host's own character has no ZNetPeer, but it is plainly here.
            // ZNet.GetUID() and ZRoutedRpc's own id are both ZDOMan.GetSessionID(),
            // and unlike ZRoutedRpc.m_id / GetPeer it is public on the real
            // game assembly rather than only on the publicized reference.
            if (peer == ZNet.GetUID()) return true;
            return ZNet.instance.GetPeer(peer) != null;
        }

        private static void SendStateToHird(Vault v)
        {
            foreach (HirdMember m in v.Hird.Members) SendState(m.PlayerId);
            foreach (HirdInvite i in v.Hird.Invites) SendState(i.PlayerId);
        }

        /// <summary>
        /// Everyone online gets a fresh roster when the population changes, so
        /// the invite list in an open Kist window does not go stale.
        /// </summary>
        public static void BroadcastRoster()
        {
            List<long> ids = new List<long>(NameByPlayer.Keys);
            foreach (long id in ids)
                if (IsConnected(id)) SendState(id);
        }
    }
}
