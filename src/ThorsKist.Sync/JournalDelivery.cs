using System;
using System.Collections.Generic;
using System.IO;

namespace ThorsKist.Sync
{
    public static class JournalDelivery
    {
        // Count alone is insufficient: two maximum-sized item effects do not fit
        // in one frame. Produce a bounded prefix without changing any entry.
        public static (byte[] Body, int Count) EncodePrefix(SessionAddress address, IReadOnlyList<JournalEntry> entries)
        {
            var prefix = new List<JournalEntry>(); byte[] body = JournalWire.EncodeEntries(address, prefix);
            foreach (var entry in entries)
            {
                if (prefix.Count == JournalWire.MaximumEntries) break;
                prefix.Add(entry);
                try { body = JournalWire.EncodeEntries(address, prefix); }
                catch (InvalidDataException) { if (prefix.Count == 1) throw; prefix.RemoveAt(prefix.Count - 1); break; }
            }
            return (body, prefix.Count);
        }
        // Validate worst-case accepted entry before admission, so a command can
        // never commit stock that this protocol is unable to deliver.
        public static void ValidateCommand(ItemCommand command)
        {
            JournalWire.EncodeCommand(command);
            var operation = Guid.NewGuid();
            var effect = new CharacterEffect(Guid.NewGuid(), operation, command.RequestId, command.Address,
                command.Kind == ItemCommandKind.Deposit ? CharacterEffectKind.RemoveReserved : CharacterEffectKind.Add, command.Item);
            var v = new VaultIdentity(command.Address.World, command.Address.Vault, command.Address.Epoch);
            var entry = new JournalEntry(v, 1, operation, command.RequestId,
                command.Kind == ItemCommandKind.Deposit ? JournalEntryKind.Deposit : JournalEntryKind.Withdraw,
                command.Address, new StackRecord(command.StackId, 0, command.Item), null, effect, new string('0', 64));
            JournalWire.EncodeEntries(command.Address, new[] { entry }, true);
        }
    }
}
