using System;
using System.IO;

namespace ThorsKist.Sync
{
    public sealed class JournalRequest
    {
        public JournalMessage Message { get; }
        public Guid RequestId { get; }
        public VaultIdentity? LoginVault { get; }
        public ItemCommand? Command { get; }
        public SessionAddress? Address { get; }
        public long Sequence { get; }
        private JournalRequest(JournalFrame frame, VaultIdentity? vault = null, ItemCommand? command = null, SessionAddress? address = null, long sequence = 0)
        { Message = frame.Message; RequestId = frame.Correlation; LoginVault = vault; Command = command; Address = address; Sequence = sequence; }

        public static JournalRequest Decode(JournalFrame frame, JournalPeerIdentity current, JournalPeerBinding binding)
        {
            if (!binding.IsCurrent(current)) throw new InvalidDataException("Stale peer");
            if (frame.Message == JournalMessage.Login)
            {
                var vault = JournalWire.DecodeLogin(frame.CopyBody());
                if (!current.Vault.Matches(vault)) throw new InvalidDataException("Wrong login vault");
                return new JournalRequest(frame, vault: vault);
            }
            if (frame.Message == JournalMessage.Command)
            {
                var command = JournalWire.DecodeCommand(frame.CopyBody());
                if (frame.Correlation != command.RequestId || !binding.Authorizes(current, command.Address))
                    throw new InvalidDataException("Unbound command");
                return new JournalRequest(frame, command: command, address: command.Address);
            }
            if (frame.Message != JournalMessage.Acknowledge && frame.Message != JournalMessage.Logout && frame.Message != JournalMessage.Suspend)
                throw new InvalidDataException("Invalid client message direction");
            var position = JournalWire.DecodePosition(frame.CopyBody());
            if (!binding.Authorizes(current, position.Address) || (frame.Message != JournalMessage.Acknowledge && position.Sequence != 0))
                throw new InvalidDataException("Unbound session control");
            return new JournalRequest(frame, address: position.Address, sequence: position.Sequence);
        }
    }
}
