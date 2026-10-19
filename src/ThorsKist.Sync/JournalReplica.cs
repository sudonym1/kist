using System;
using System.Collections.Generic;
using System.IO;

namespace ThorsKist.Sync
{
    public enum ReplicaResult { Applied, Duplicate, Blocked, NeedsSuspension }

    // One entry per call bounds game-thread work. The adapter schedules calls,
    // sends cumulative acknowledgments, and handles self-suspension in M4/M5.
    public sealed class JournalReplica
    {
        private readonly ICharacterInventory inventory;
        private readonly Dictionary<Guid, CharacterEffect> applied = new Dictionary<Guid, CharacterEffect>();
        private HashSet<Guid> settled = new HashSet<Guid>();
        private SessionAddress? address;
        public VaultState? Current { get; private set; }
        public long Sequence { get; private set; }
        public bool NeedsSuspension { get; private set; }
        public JournalReplica(ICharacterInventory inventory) { this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory)); }

        // Only ordinary login supplies a new session and authoritative settled
        // effect IDs. Preserve known effects through checkpoint reset/replay.
        public bool Install(SessionAddress session, JournalSnapshot snapshot, IEnumerable<Guid> settledEffects)
        {
            if (address != null && JournalChecksum.SameAddress(address, session)) throw new InvalidOperationException("Login requires a new session");
            if (address != null && (address.World != session.World || address.Vault != session.Vault || address.Player != session.Player))
                throw new InvalidOperationException("Replica belongs to a single player and vault");
            address = session;
            if (!snapshot.State.Identity.Contains(session) || !JournalReplay.Verify(snapshot)) { NeedsSuspension = true; return false; }
            Current = snapshot.State; Sequence = snapshot.Sequence;
            settled = new HashSet<Guid>(settledEffects); NeedsSuspension = false; return true;
        }
        // Convenience for deterministic hosts. Game adapters use worker-side
        // JournalReplay.Prepare and game-thread ProcessVerified instead.
        public ReplicaResult Process(SessionAddress deliverySession, JournalEntry entry)
        {
            if (NeedsSuspension || address == null || Current == null) return ReplicaResult.NeedsSuspension;
            // Ignore stale deliveries without damaging a replacement session.
            if (!JournalChecksum.SameAddress(address, deliverySession)) return ReplicaResult.Blocked;
            if (!Current.Identity.Matches(entry.Identity) || entry.Schema != JournalChecksum.Schema) return Fail();
            if (entry.Sequence <= Sequence) return ReplicaResult.Duplicate;
            VerifiedJournalUpdate update;
            try { update = JournalReplay.Prepare(Current, Sequence, entry); }
            catch (InvalidDataException) { return Fail(); }
            return ProcessVerified(deliverySession, update);
        }
        public ReplicaResult VerificationFailed(SessionAddress deliverySession)
        {
            if (address == null || !JournalChecksum.SameAddress(address, deliverySession)) return ReplicaResult.Blocked;
            return Fail();
        }
        public ReplicaResult ProcessVerified(SessionAddress deliverySession, VerifiedJournalUpdate update)
        {
            if (NeedsSuspension || address == null || Current == null) return ReplicaResult.NeedsSuspension;
            if (!JournalChecksum.SameAddress(address, deliverySession)) return ReplicaResult.Blocked;
            var entry = update.Entry;
            if (!Current.Identity.Matches(entry.Identity)) return Fail();
            if (entry.Sequence <= Sequence) return ReplicaResult.Duplicate;
            // A prepared update is valid only for its captured replica baseline.
            // Rejoin or another installed update cannot use a stale candidate.
            if (!ReferenceEquals(update.Before, Current) || update.BeforeSequence != Sequence) return ReplicaResult.Blocked;
            if (entry.Kind == JournalEntryKind.Rejected && entry.Actor != null && JournalChecksum.SameAddress(entry.Actor, address))
                inventory.ReleaseRejectedReservation(entry.RequestId);
            var effect = entry.Effect;
            if (effect != null && effect.EffectId != Guid.Empty && JournalChecksum.SameAddress(effect.Recipient, address) && !settled.Contains(effect.EffectId))
            {
                if (effect.OperationId != entry.OperationId || effect.RequestId != entry.RequestId) return Fail();
                if (applied.TryGetValue(effect.EffectId, out var known))
                {
                    if (!JournalChecksum.SameEffect(known, effect)) return Fail();
                }
                else
                {
                    var result = inventory.Apply(effect);
                    if (result != InventoryResult.Applied && result != InventoryResult.AlreadyApplied) return ReplicaResult.Blocked;
                    applied.Add(effect.EffectId, effect);
                }
            }
            Current = update.State; Sequence = entry.Sequence; return ReplicaResult.Applied;
        }
        private ReplicaResult Fail() { NeedsSuspension = true; return ReplicaResult.NeedsSuspension; }
    }
}
