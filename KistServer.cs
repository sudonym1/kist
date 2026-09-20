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
            public Inventory Inventory;
        }

        private static readonly List<Vault> Vaults = new List<Vault>();
        private static readonly Dictionary<long, long> PeerByPlayer = new Dictionary<long, long>();
        // Player ids are the only stable handle on a character, but nothing in
        // ZNet's roster carries one -- so the server learns them from the Hello
        // each client sends, and that map is what the invite list is built from.
        private static readonly Dictionary<long, string> NameByPlayer = new Dictionary<long, string>();
        private static bool _loaded;
        private static bool _dirty;
        private static float _nextSave;

        public static bool IsServer
        {
            get { return ZNet.instance != null && ZNet.instance.IsServer(); }
        }

        // ------------------------------------------------------------------
        // Persistence
        // ------------------------------------------------------------------

        private static string SaveDir
        {
            get { return Path.Combine(Paths.ConfigPath, "ThorsKist"); }
        }

        private static string SavePath
        {
            get
            {
                long uid = ZNet.instance != null ? ZNet.instance.GetWorldUID() : 0L;
                return Path.Combine(SaveDir, "kist_" + uid + ".dat");
            }
        }

        public static void EnsureLoaded()
        {
            if (_loaded || !IsServer) return;
            // Items cannot be rebuilt before the prefab database exists, and
            // loading early would silently drop every stack in the vault.
            if (ObjectDB.instance == null) return;
            _loaded = true;

            try
            {
                string path = SavePath;
                if (!File.Exists(path))
                {
                    Main.Log.LogInfo("ThorsKist: no vault file yet at " + path);
                    return;
                }
                byte[] bytes = File.ReadAllBytes(path);
                ZPackage pkg = new ZPackage(bytes);
                int version = pkg.ReadInt();
                if (version != Wire.SaveFormat)
                {
                    Main.Log.LogError("ThorsKist: vault file is protocol " + version +
                                      ", this build speaks " + Wire.SaveFormat + ". Refusing to load it.");
                    return;
                }
                int count = pkg.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    Hird hird = Hird.ReadHeader(pkg);
                    ZPackage inv = pkg.ReadPackage();
                    Vault v = new Vault { Hird = hird, Inventory = NewInventory(hird) };
                    v.Inventory.Load(inv);
                    Vaults.Add(v);
                }
                Main.Log.LogInfo("ThorsKist: loaded " + Vaults.Count + " hird(s) from " + path);
            }
            catch (Exception e)
            {
                Main.Log.LogError("ThorsKist: could not load the vault file: " + e);
            }
        }

        private static Inventory NewInventory(Hird hird)
        {
            return new Inventory("Kist_" + hird.Id, null, 8, Mathf.Clamp(Main.KistRows.Value, 4, 256));
        }

        private static void Save()
        {
            if (!IsServer) return;
            try
            {
                Directory.CreateDirectory(SaveDir);
                ZPackage pkg = new ZPackage();
                pkg.Write(Wire.SaveFormat);
                pkg.Write(Vaults.Count);
                foreach (Vault v in Vaults)
                {
                    v.Hird.WriteHeader(pkg);
                    ZPackage inv = new ZPackage();
                    v.Inventory.Save(inv);
                    pkg.Write(inv);
                }
                string path = SavePath;
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, pkg.GetArray());
                // Replace atomically: a half-written vault would cost people
                // everything they own.
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _dirty = false;
            }
            catch (Exception e)
            {
                Main.Log.LogError("ThorsKist: could not save the vault file: " + e);
            }
        }

        private static void MarkDirty()
        {
            _dirty = true;
            // Coalesce bursts (a player emptying their pack) into one write.
            _nextSave = Time.realtimeSinceStartup + 2f;
        }

        public static void Tick()
        {
            if (!IsServer) return;
            EnsureLoaded();
            if (_dirty && Time.realtimeSinceStartup >= _nextSave) Save();
        }

        public static void FlushNow()
        {
            if (_dirty) Save();
        }

        /// <summary>
        /// Drops every vault from memory. Called when the world goes away, so a
        /// host who quits to the menu and loads a different world does not carry
        /// the last world's hirds -- or write them into the new world's file.
        /// </summary>
        public static void Reset()
        {
            FlushNow();
            Vaults.Clear();
            PeerByPlayer.Clear();
            NameByPlayer.Clear();
            _loaded = false;
            _dirty = false;
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

                if (playerId != 0L)
                {
                    PeerByPlayer[playerId] = senderPeer;
                    if (!string.IsNullOrEmpty(playerName)) NameByPlayer[playerId] = playerName;
                }

                switch (op)
                {
                    case Wire.C2S.Hello:
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
                    case Wire.C2S.Disband:
                        Disband(senderPeer, playerId);
                        break;
                    case Wire.C2S.SetAnchor:
                        SetAnchor(senderPeer, playerId, pos);
                        break;
                    case Wire.C2S.Deposit:
                        Deposit(senderPeer, playerId, playerName, pos, pkg.ReadInt(), pkg.ReadPackage());
                        break;
                    case Wire.C2S.Take:
                        Take(senderPeer, playerId, pos, pkg.ReadInt(), pkg.ReadVector2i(), pkg.ReadString(), pkg.ReadInt());
                        break;
                    case Wire.C2S.ConsumeForCraft:
                        ConsumeForCraft(senderPeer, playerId, pos, pkg);
                        break;
                }
            }
            catch (Exception e)
            {
                Main.Log.LogError("ThorsKist: bad message from peer " + senderPeer + ": " + e);
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
            Vault created = new Vault { Hird = h, Inventory = NewInventory(h) };
            Vaults.Add(created);
            MarkDirty();
            AnnounceAnchor(created);
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

        private static void Disband(long peer, long playerId)
        {
            Vault v = VaultOf(playerId);
            if (v == null) return;
            if (v.Hird.OwnerId != playerId) { Deny(peer, "Only the hird's founder may disband it."); return; }
            if (v.Inventory.NrOfItems() > 0)
            {
                Deny(peer, "Empty the Kist before disbanding -- " + v.Inventory.NrOfItems() +
                           " stack(s) would be destroyed.");
                return;
            }
            List<HirdMember> members = new List<HirdMember>(v.Hird.Members);
            Vaults.Remove(v);
            MarkDirty();
            foreach (HirdMember m in members) SendState(m.PlayerId);
        }

        private static void SetAnchor(long peer, long playerId, Vector3 pos)
        {
            Vault v = VaultOf(playerId);
            if (v == null) { Deny(peer, "You are not in a hird."); return; }
            if (v.Hird.OwnerId != playerId) { Deny(peer, "Only the hird's founder may move the Kist."); return; }
            v.Hird.Anchor = pos;
            MarkDirty();
            ToastHird(v, "The Kist has been anchored to a new location.");
            AnnounceAnchor(v);
            SendStateToHird(v);
        }

        // ------------------------------------------------------------------
        // Items
        // ------------------------------------------------------------------

        private static bool Usable(Vault v, Vector3 pos, float radius, out string why)
        {
            why = null;
            if (v.Hird.Members.Count < Main.MinHirdSize.Value)
            {
                why = "The Kist stays shut until the hird has " + Main.MinHirdSize.Value + " members.";
                return false;
            }
            if (Main.UnlimitedRange.Value) return true;
            float d = Vector3.Distance(pos, v.Hird.Anchor);
            if (d > radius)
            {
                why = "Too far from the Kist (" + Mathf.RoundToInt(d) + "m of " + Mathf.RoundToInt(radius) + "m).";
                return false;
            }
            return true;
        }

        private static void Deposit(long peer, long playerId, string playerName, Vector3 pos, int token, ZPackage items)
        {
            Vault v = VaultOf(playerId);
            if (v == null) { Reject(peer, token, "You are not in a hird."); return; }

            string why;
            if (!Usable(v, pos, Main.DepositRadius.Value, out why)) { Reject(peer, token, why); return; }

            List<ItemDrop.ItemData> incoming = ItemWire.ReadItems(items);
            if (incoming.Count == 0) { Reject(peer, token, "Nothing to store."); return; }

            foreach (ItemDrop.ItemData item in incoming)
            {
                if (!v.Inventory.CanAddItem(item, item.m_stack))
                {
                    Reject(peer, token, "The Kist is full.");
                    return;
                }
            }
            foreach (ItemDrop.ItemData item in incoming)
                v.Inventory.AddItem(item);

            MarkDirty();
            Ack(peer, token);
            SendStateToHird(v);
        }

        private static void Take(long peer, long playerId, Vector3 pos, int token, Vector2i slot, string expectName, int amount)
        {
            Vault v = VaultOf(playerId);
            if (v == null) { Reject(peer, token, "You are not in a hird."); return; }

            string why;
            if (!Usable(v, pos, Main.TakeRadius.Value, out why)) { Reject(peer, token, why); return; }

            // Addressed by grid position, not by list index: positions survive a
            // save/load round trip, and list order does not.
            ItemDrop.ItemData item = v.Inventory.GetItemAt(slot.x, slot.y);
            if (item == null) { Reject(peer, token, "That item is already gone."); return; }
            // Two people can reach for the same stack at once; the name check
            // turns a race into a harmless refusal instead of the wrong item.
            if (item.m_shared.m_name != expectName) { Reject(peer, token, "That item is already gone."); return; }

            if (amount <= 0 || amount > item.m_stack) amount = item.m_stack;

            ItemDrop.ItemData sent = item.Clone();
            sent.m_stack = amount;

            if (amount >= item.m_stack) v.Inventory.RemoveItem(item);
            else item.m_stack -= amount;

            MarkDirty();

            ZPackage reply = NewReply(Wire.S2C.TakeResult);
            reply.Write(token);
            ZPackage payload = new ZPackage();
            ItemWire.WriteItem(payload, sent);
            reply.Write(payload);
            SendToPeer(peer, reply);
            SendStateToHird(v);
        }

        private static void ConsumeForCraft(long peer, long playerId, Vector3 pos, ZPackage pkg)
        {
            Vault v = VaultOf(playerId);
            if (v == null) return;

            string why;
            if (!Usable(v, pos, Main.CraftRadius.Value, out why)) return;

            int count = pkg.ReadInt();
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                string name = pkg.ReadString();
                int amount = pkg.ReadInt();
                int quality = pkg.ReadInt();
                if (amount <= 0) continue;
                int have = v.Inventory.CountItems(name, quality);
                if (have < amount)
                {
                    // The client counted a stack that has since been spent.
                    // Take what is there and tell them; the recipe is already
                    // crafted on their end, so refusing outright helps nobody.
                    Main.Log.LogWarning("ThorsKist: craft consume short by " + (amount - have) + " x " + name);
                    amount = have;
                }
                if (amount > 0)
                {
                    v.Inventory.RemoveItem(name, amount, quality);
                    changed = true;
                }
            }
            if (changed)
            {
                MarkDirty();
                SendStateToHird(v);
            }
        }

        // ------------------------------------------------------------------
        // Send
        // ------------------------------------------------------------------

        private static ZPackage NewReply(Wire.S2C op)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(Wire.Protocol);
            pkg.Write((byte)op);
            return pkg;
        }

        private static void SendToPeer(long peer, ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, Wire.RpcToClient, pkg);
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
        private static void AnnounceAnchor(Vault v)
        {
            foreach (HirdMember m in v.Hird.Members)
            {
                ZPackage pkg = NewReply(Wire.S2C.AnchorSet);
                pkg.Write(v.Hird.Anchor);
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
                ZPackage inv = new ZPackage();
                v.Inventory.Save(inv);
                pkg.Write(inv);
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
