using System;
using System.Threading;
using System.Threading.Tasks;

namespace ThorsKist.Sync
{
    // Opaque vanilla payload preserves fields (and unknown prefabs) until the M5
    // field audit defines the canonical schema. Never expose mutable byte arrays.
    public sealed class ItemRecord
    {
        private readonly byte[] payload;
        public string Prefab { get; }
        public int Quantity { get; }
        public int FormatVersion { get; }
        public ItemRecord(string prefab, int quantity, int formatVersion, byte[] payload)
        {
            if (string.IsNullOrWhiteSpace(prefab)) throw new ArgumentException("Prefab required", nameof(prefab));
            if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            if (formatVersion <= 0) throw new ArgumentOutOfRangeException(nameof(formatVersion));
            Prefab = prefab; Quantity = quantity; FormatVersion = formatVersion;
            this.payload = (byte[])(payload ?? throw new ArgumentNullException(nameof(payload))).Clone();
        }
        public byte[] CopyPayload() => (byte[])payload.Clone();
    }

    public sealed class SessionAddress
    {
        public string World { get; }
        public string Vault { get; }
        public Guid Epoch { get; }
        public string Player { get; }
        public Guid Session { get; }
        public SessionAddress(string world, string vault, Guid epoch, string player, Guid session)
        {
            if (string.IsNullOrWhiteSpace(world) || string.IsNullOrWhiteSpace(vault) || string.IsNullOrWhiteSpace(player))
                throw new ArgumentException("World, vault and authenticated player required");
            if (epoch == Guid.Empty || session == Guid.Empty) throw new ArgumentException("UUID required");
            World = world; Vault = vault; Epoch = epoch; Player = player; Session = session;
        }
    }
    public enum ItemCommandKind { Deposit, Withdraw }
    public enum ItemCommandPurpose { Inventory, Craft, Station }
    public sealed class ItemCommand
    {
        public SessionAddress Address { get; }
        public Guid RequestId { get; }
        public Guid StackId { get; }
        public ItemCommandKind Kind { get; }
        public ItemCommandPurpose Purpose { get; }
        public ItemRecord Item { get; }
        // Exactly one stack and one exact quantity; multi-stack actions split before admission.
        public ItemCommand(SessionAddress address, Guid requestId, Guid stackId, ItemCommandKind kind, ItemRecord item, ItemCommandPurpose purpose = ItemCommandPurpose.Inventory)
        {
            if (requestId == Guid.Empty || stackId == Guid.Empty) throw new ArgumentException("UUID required");
            if (!Enum.IsDefined(typeof(ItemCommandKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!Enum.IsDefined(typeof(ItemCommandPurpose), purpose) || (kind == ItemCommandKind.Deposit && purpose != ItemCommandPurpose.Inventory))
                throw new ArgumentException("Invalid item action purpose", nameof(purpose));
            Address = address ?? throw new ArgumentNullException(nameof(address));
            Item = item ?? throw new ArgumentNullException(nameof(item));
            RequestId = requestId; StackId = stackId; Kind = kind; Purpose = purpose;
        }
    }
    public enum CharacterEffectKind { Add, RemoveReserved }
    public sealed class CharacterEffect
    {
        public Guid EffectId { get; }
        public Guid OperationId { get; }
        public Guid RequestId { get; }
        public SessionAddress Recipient { get; }
        public CharacterEffectKind Kind { get; }
        public ItemRecord Item { get; }
        public CharacterEffect(Guid effectId, Guid operationId, Guid requestId, SessionAddress recipient,
            CharacterEffectKind kind, ItemRecord item)
        {
            if (effectId == Guid.Empty || operationId == Guid.Empty || requestId == Guid.Empty) throw new ArgumentException("UUID required");
            if (!Enum.IsDefined(typeof(CharacterEffectKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            EffectId = effectId; OperationId = operationId; RequestId = requestId;
            Recipient = recipient ?? throw new ArgumentNullException(nameof(recipient));
            Kind = kind; Item = item ?? throw new ArgumentNullException(nameof(item));
        }
    }
    public interface IClock { DateTimeOffset UtcNow { get; } Task Delay(TimeSpan delay, CancellationToken cancellationToken); }
    // Called from worker code; transport adapter marshals RPC invocation as needed.
    public interface ICommandTransport { Task SendAsync(ItemCommand command, CancellationToken cancellationToken); }
    public interface ICommandHandler { Task HandleAsync(ItemCommand command, CancellationToken cancellationToken); }
    // SqliteJournalStorage atomically persists this typed candidate image and
    // verifies recovered current rows against checkpoint replay before use.
    public interface IJournalStorage : IDisposable
    {
        Task<JournalMachineState?> ReadStateAsync(CancellationToken cancellationToken);
        Task CommitAsync(StorageCommit commit, CancellationToken cancellationToken);
    }
    public sealed class StorageCommit
    {
        public long ExpectedHead { get; }
        public long? ExpectedReplicationRevision { get; }
        public JournalMachineState State { get; }
        // Only JournalWriter can attest a transition from its recovered,
        // immutable baseline. Public snapshot submissions remain untrusted.
        public JournalMachineState? PreviousState { get; }
        internal StorageCommit(JournalMachineState previous, JournalMachineState state)
            : this(previous.Head, state, previous.Replication.Revision) { PreviousState = previous; }
        public StorageCommit(long expectedHead, JournalMachineState state, long? expectedReplicationRevision = null)
        {
            if (expectedHead < 0) throw new ArgumentOutOfRangeException(nameof(expectedHead));
            ExpectedReplicationRevision = expectedReplicationRevision;
            ExpectedHead = expectedHead; State = state ?? throw new ArgumentNullException(nameof(state));
            if (state.Head < expectedHead) throw new ArgumentException("Commit cannot move head backwards", nameof(state));
        }
    }
    public enum InventoryResult { Applied, AlreadyApplied, MissingReservation, InventoryChanged, NoCapacity, Failed }
    // All inventory methods run on the game thread. Only verified journal effects
    // may call Apply; a command response must never authorize character removal.
    public interface ICharacterInventory
    {
        // Adapter owns the in-memory mapping from this opaque local ID to an
        // exact inventory object. It must never resolve it by slot or item name.
        bool TryReserve(Guid requestId, Guid localStackId, int quantity);
        InventoryResult Apply(CharacterEffect verifiedEffect);
        bool ReleaseRejectedReservation(Guid requestId);
    }
}
