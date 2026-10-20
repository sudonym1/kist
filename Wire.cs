using System.Collections.Generic;
using UnityEngine;

namespace ThorsKist
{
    /// <summary>
    /// Everything that crosses the wire. One routed RPC in each direction, with
    /// the opcode as the first byte of the payload, so adding a message never
    /// means registering another RPC name on both sides.
    /// </summary>
    public static class Wire
    {
        public const string RpcToServer = "ThorsKist_C2S";
        public const string RpcToClient = "ThorsKist_S2C";

        // Bumped whenever the payload layout changes incompatibly. The server
        // refuses clients that do not match rather than half-reading a message.
        public const int Protocol = 5;

        // The on-disk vault format, deliberately SEPARATE from the wire
        // protocol. Adding a message is not a reason to refuse to load someone's
        // stored belongings, and stamping the save file with Protocol would do
        // exactly that the next time a new message is added.
        public const int SaveFormat = 1;

        public enum C2S : byte
        {
            Hello = 0,
            CreateHird = 1,
            Invite = 2,
            AcceptInvite = 3,
            DeclineInvite = 4,
            Kick = 5,
            Leave = 6,
            SetAnchor = 7,
            Deposit = 8,
            Take = 9,
            RequestState = 10,
            ConsumeForCraft = 11,
            Disband = 12,
            Ping = 13,
            AnchorQuery = 14,
            Rename = 15,
            // A line of text the client wants written into the SERVER's log.
            // Station decisions happen client-side, so without this the only
            // record of why the Kist declined sits in a file on the player's
            // own machine.
            Diag = 16,
        }

        public enum S2C : byte
        {
            State = 0,
            Toast = 1,
            DepositAck = 2,
            DepositRejected = 3,
            TakeResult = 4,
            Denied = 5,
            AnchorSet = 6,
            DepositFx = 7,
            AnchorStatus = 8,
        }
    }

    public class HirdMember
    {
        public long PlayerId;
        public string Name = "";

        public HirdMember() { }
        public HirdMember(long id, string name) { PlayerId = id; Name = name ?? ""; }
    }

    public class HirdInvite
    {
        public long PlayerId;
        public string Name = "";
        public long FromPlayerId;
        public string FromName = "";
    }

    /// <summary>
    /// A hird and its Kist. The server owns the authoritative copy; clients hold
    /// a read-only mirror refreshed on every change.
    /// </summary>
    public class Hird
    {
        public string Id = "";
        public string Name = "";
        public long OwnerId;
        public Vector3 Anchor = Vector3.zero;
        public List<HirdMember> Members = new List<HirdMember>();
        public List<HirdInvite> Invites = new List<HirdInvite>();

        public bool IsMember(long playerId)
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].PlayerId == playerId) return true;
            return false;
        }

        public bool IsInvited(long playerId)
        {
            for (int i = 0; i < Invites.Count; i++)
                if (Invites[i].PlayerId == playerId) return true;
            return false;
        }

        public string MemberName(long playerId)
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].PlayerId == playerId) return Members[i].Name;
            return "?";
        }

        public void WriteHeader(ZPackage pkg)
        {
            pkg.Write(Id);
            pkg.Write(Name);
            pkg.Write(OwnerId);
            pkg.Write(Anchor);
            pkg.Write(Members.Count);
            foreach (HirdMember m in Members)
            {
                pkg.Write(m.PlayerId);
                pkg.Write(m.Name);
            }
            pkg.Write(Invites.Count);
            foreach (HirdInvite inv in Invites)
            {
                pkg.Write(inv.PlayerId);
                pkg.Write(inv.Name);
                pkg.Write(inv.FromPlayerId);
                pkg.Write(inv.FromName);
            }
        }

        public static Hird ReadHeader(ZPackage pkg)
        {
            Hird h = new Hird();
            h.Id = pkg.ReadString();
            h.Name = pkg.ReadString();
            h.OwnerId = pkg.ReadLong();
            h.Anchor = pkg.ReadVector3();
            int members = pkg.ReadInt();
            for (int i = 0; i < members; i++)
            {
                long id = pkg.ReadLong();
                string name = pkg.ReadString();
                h.Members.Add(new HirdMember(id, name));
            }
            int invites = pkg.ReadInt();
            for (int i = 0; i < invites; i++)
            {
                HirdInvite inv = new HirdInvite();
                inv.PlayerId = pkg.ReadLong();
                inv.Name = pkg.ReadString();
                inv.FromPlayerId = pkg.ReadLong();
                inv.FromName = pkg.ReadString();
                h.Invites.Add(inv);
            }
            return h;
        }
    }
}
