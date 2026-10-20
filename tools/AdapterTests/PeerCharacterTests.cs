using System;
using System.Reflection;
using UnityEngine;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private void PeerCharacterIdentity()
        {
            int start = checks;
            const long owner = 7263819, player = 2144103681;
            var zdo = new ZDO { m_uid = new ZDOID(owner, 918273) };
            var positionField = typeof(ZDO).GetField("m_position", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                Check(!JournalDirectRpc.ReadCharacter(owner, null, out _, out _), "Unreplicated character is not authenticated");
                Check(!JournalDirectRpc.ReadCharacter(owner, zdo, out _, out _), "Unowned character is not authenticated");
                // Populate replicated extra data directly: the menu has no
                // ZDOMan session for SetOwnerInternal's local-owner bookkeeping.
                ZDOExtraData.SetOwner(zdo.m_uid, ZDOID.AddUser(owner));
                typeof(ZDO).GetProperty("Owned", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(zdo, true);
                Check(!JournalDirectRpc.ReadCharacter(owner, zdo, out _, out _), "Character without replicated player identity is not authenticated");
                ZDOExtraData.Set(zdo.m_uid, ZDOVars.s_playerID, player);
                var expected = new Vector3(2, 3, 4); positionField.SetValue(zdo, expected);
                Check(JournalDirectRpc.ReadCharacter(owner, zdo, out long actual, out var position)
                    && actual == player && position == expected, "Owned replicated character supplies player identity and position without unused peer PlayerID");
                Check(!JournalDirectRpc.ReadCharacter(owner + 1, zdo, out actual, out position) && actual == 0 && position == Vector3.zero,
                    "Another peer cannot authenticate this character or receive identity outputs");
                Check(!JournalDirectRpc.ReadCharacter(0, zdo, out _, out _), "Zero peer identity is refused");
                foreach (var invalid in new[] { new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0), new Vector3(0, 0, float.NegativeInfinity) })
                {
                    positionField.SetValue(zdo, invalid);
                    Check(!JournalDirectRpc.ReadCharacter(owner, zdo, out _, out _), "Nonfinite character position is refused");
                }
                positionField.SetValue(zdo, expected); ZDOExtraData.SetOwner(zdo.m_uid, ZDOID.AddUser(owner + 1));
                Check(!JournalDirectRpc.ReadCharacter(owner, zdo, out _, out _), "Ownership change invalidates old peer immediately");
            }
            finally { ZDOExtraData.Release(zdo, zdo.m_uid); }
            Logger.LogInfo("M7 PEER CHARACTER PASS " + (checks - start) + " native assertions (replicated identity, owner binding, readiness and finite position)");
        }
    }
}
