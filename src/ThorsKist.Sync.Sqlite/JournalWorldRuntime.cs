using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThorsKist.Sync;

namespace ThorsKist.Sync.Sqlite
{
    public sealed class JournalWorldVault
    {
        private readonly byte[] header;
        private readonly HashSet<long> members;
        public VaultIdentity Identity { get; }
        public int Capacity { get; }
        internal JournalWorldVault(VaultIdentity identity, int capacity, byte[] header, IEnumerable<long> members)
        {
            Identity = identity; Capacity = capacity; this.header = (byte[])header.Clone(); this.members = new HashSet<long>(members);
            if (this.members.Count == 0 || this.members.Count > 10000 || this.members.Contains(0)) throw new InvalidDataException("Invalid catalog membership");
        }
        public byte[] CopyHeader() => (byte[])header.Clone();
        public bool IsMember(long character) => members.Contains(character);
    }

    // Persistent world lifetime; the native owner supplies its audited metadata
    // decoder and captured account/character/range facts. Vault writers open
    // lazily, recovering and settling old sessions before accepting any login.
    public sealed class JournalWorldRuntime
    {
        private readonly object sync = new object();
        private readonly SemaphoreSlim metadata = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, JournalWorldVault> vaults;
        private readonly Dictionary<string, Task<JournalVaultRuntime>> writers = new Dictionary<string, Task<JournalVaultRuntime>>(StringComparer.Ordinal);
        private readonly Dictionary<JournalPeerBinding, JournalPeerIdentity> peers = new Dictionary<JournalPeerBinding, JournalPeerIdentity>();
        private readonly Dictionary<string, JournalWorldVault> changing = new Dictionary<string, JournalWorldVault>(StringComparer.Ordinal);
        private readonly HashSet<string> retiring = new HashSet<string>(StringComparer.Ordinal);
        private readonly string path;
        private readonly JournalWorldCatalog catalog;
        private readonly IReplicationClock clock;
        private readonly Func<byte[], IReadOnlyCollection<long>> decodeMembers;
        private readonly JournalTransferBudget transfers = new JournalTransferBudget();
        private readonly int maximumDepth;
        private Task? stopping;
        private Exception? failure;
        public Exception? Failure { get { lock (sync) return failure ?? writers.Values.Where(t => t.Status == TaskStatus.RanToCompletion).Select(t => t.Result.Failure).FirstOrDefault(e => e != null); } }
        public IReadOnlyList<JournalWorldVault> Vaults { get { lock (sync) return Array.AsReadOnly(vaults.Values.ToArray()); } }
        private JournalWorldRuntime(string path, JournalWorldCatalog catalog, IEnumerable<JournalWorldVault> vaults,
            IReplicationClock clock, int maximumDepth, Func<byte[], IReadOnlyCollection<long>> decodeMembers)
        {
            this.path = path; this.catalog = catalog; this.vaults = vaults.ToDictionary(v => v.Identity.Vault, StringComparer.Ordinal);
            this.clock = clock; this.maximumDepth = maximumDepth; this.decodeMembers = decodeMembers;
        }
        public static async Task<JournalWorldRuntime> OpenAsync(string databasePath, string world, string? legacyPath,
            Func<byte[], IReadOnlyList<LegacyVaultImport>> decodeLegacy, Func<byte[], IReadOnlyCollection<long>> decodeMembers,
            IReplicationClock clock, int maximumDepth)
        {
            if (maximumDepth <= 0 || clock == null || decodeMembers == null) throw new ArgumentException("World runtime configuration required");
            var path = Path.GetFullPath(databasePath);
            var catalog = await JournalWorldCatalog.OpenAsync(path, world, legacyPath, decodeLegacy).ConfigureAwait(false);
            var rows = await catalog.ReadAsync().ConfigureAwait(false);
            var vaults = rows.Select(row => new JournalWorldVault(row.Identity, row.State.Current.Capacity, row.CopyMetadata(), decodeMembers(row.CopyMetadata()))).ToArray();
            return new JournalWorldRuntime(path, catalog, vaults, clock, maximumDepth, decodeMembers);
        }
        private void RequireRunning()
        {
            if (Failure != null) throw new InvalidOperationException("World journal authority failed closed", Failure);
            if (stopping != null) throw new ObjectDisposedException(nameof(JournalWorldRuntime));
        }
        private Task<JournalVaultRuntime> Writer(VaultIdentity identity)
        {
            lock (sync)
            {
                RequireRunning();
                if (!vaults.TryGetValue(identity.Vault, out var vault) || !identity.Matches(vault.Identity)) throw new InvalidDataException("Unknown or retired vault epoch");
                if (!writers.TryGetValue(identity.Vault, out var writer))
                {
                    writer = Task.Run(async () =>
                    {
                        try
                        {
                            return await JournalVaultRuntime.OpenAsync(new SqliteJournalStorage(path, identity),
                                new VaultState(identity, vault.Capacity, Array.Empty<StackRecord>()), clock, maximumDepth, transfers).ConfigureAwait(false);
                        }
                        catch (Exception error) { lock (sync) failure = error; throw; }
                    });
                    writers.Add(identity.Vault, writer);
                }
                return writer;
            }
        }
        public async Task ReceiveAsync(JournalPeerIdentity peer, JournalPeerBinding binding, JournalSendQueue outgoing,
            JournalFrame frame, CancellationToken token = default)
        {
            lock (sync)
            {
                RequireRunning();
                if (!vaults.TryGetValue(peer.Vault.Vault, out var vault) || !vault.Identity.Matches(peer.Vault)
                    || retiring.Contains(peer.Vault.Vault) || !vault.IsMember(peer.Character)
                    || (changing.TryGetValue(peer.Vault.Vault, out var pending) && !pending.IsMember(peer.Character))
                    || !binding.IsCurrent(peer)) throw new InvalidDataException("Character is not a current catalog member");
                if (!peers.ContainsKey(binding) && peers.Count >= 128) throw new IOException("World journal connection limit reached");
                peers[binding] = peer;
            }
            try { await (await Writer(peer.Vault).ConfigureAwait(false)).ReceiveAsync(peer, binding, outgoing, frame, token).ConfigureAwait(false); }
            catch { lock (sync) peers.Remove(binding); throw; }
        }
        public async Task DisconnectAsync(JournalPeerBinding binding)
        {
            Task<JournalVaultRuntime>? writer = null;
            lock (sync)
            {
                if (peers.TryGetValue(binding, out var peer)) { peers.Remove(binding); writers.TryGetValue(peer.Vault.Vault, out writer); }
            }
            if (writer != null) await (await writer.ConfigureAwait(false)).DisconnectAsync(binding).ConfigureAwait(false);
        }
        public async Task<JournalMachineState> ReadAsync(VaultIdentity identity)
            => await (await Writer(identity).ConfigureAwait(false)).ReadAsync().ConfigureAwait(false);

        public async Task<JournalWorldVault> CreateAsync(VaultState initial, byte[] header)
        {
            var immutable = (byte[])header.Clone();
            var next = new JournalWorldVault(initial.Identity, initial.Capacity, immutable, decodeMembers(immutable));
            await metadata.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (sync) { RequireRunning(); if (vaults.ContainsKey(initial.Identity.Vault)) throw new InvalidOperationException("Hird already exists"); }
                await catalog.CreateAsync(initial, immutable).ConfigureAwait(false);
                lock (sync) vaults.Add(initial.Identity.Vault, next);
                return next;
            }
            catch (Exception error) when (!(error is ArgumentException || error is InvalidOperationException)) { lock (sync) failure = error; throw; }
            finally { metadata.Release(); }
        }
        public async Task<bool> UpdateMetadataAsync(VaultIdentity identity, byte[] expected, byte[] header)
        {
            var before = (byte[])expected.Clone(); var after = (byte[])header.Clone();
            await metadata.WaitAsync().ConfigureAwait(false);
            try
            {
                JournalWorldVault previous; JournalPeerBinding[] departed;
                lock (sync)
                {
                    RequireRunning();
                    if (!vaults.TryGetValue(identity.Vault, out previous!) || !previous.Identity.Matches(identity)) throw new InvalidDataException("Unknown vault");
                    if (!previous.CopyHeader().SequenceEqual(before)) return false;
                }
                var next = new JournalWorldVault(identity, previous.Capacity, after, decodeMembers(after));
                lock (sync)
                {
                    changing[identity.Vault] = next;
                    departed = peers.Where(p => p.Value.Vault.Matches(identity) && !next.IsMember(p.Value.Character)).Select(p => p.Key).ToArray();
                }
                // Settle departures before publishing membership. A failed
                // metadata commit cannot expose an unprotected old session.
                foreach (var binding in departed) { binding.Close(); await DisconnectAsync(binding).ConfigureAwait(false); }
                if (!await catalog.UpdateMetadataAsync(identity, before, after).ConfigureAwait(false))
                    throw new IOException("Catalog changed outside its world owner; recovery required");
                lock (sync) vaults[identity.Vault] = next;
                return true;
            }
            catch (Exception error) { lock (sync) failure = error; throw; }
            finally { lock (sync) changing.Remove(identity.Vault); metadata.Release(); }
        }
        public async Task RetireEmptyAsync(VaultIdentity identity)
        {
            await metadata.WaitAsync().ConfigureAwait(false);
            try
            {
                JournalPeerBinding[] connected;
                lock (sync) { RequireRunning(); retiring.Add(identity.Vault); connected = peers.Where(p => p.Value.Vault.Matches(identity)).Select(p => p.Key).ToArray(); }
                foreach (var binding in connected) { binding.Close(); await DisconnectAsync(binding).ConfigureAwait(false); }
                var runtime = await Writer(identity).ConfigureAwait(false);
                var state = await runtime.ReadAsync().ConfigureAwait(false);
                if (state.Current.Stacks.Count != 0) throw new InvalidOperationException("Empty the Kist before disbanding");
                await catalog.RetireEmptyAsync(identity).ConfigureAwait(false);
                await runtime.StopAsync().ConfigureAwait(false);
                lock (sync) { vaults.Remove(identity.Vault); writers.Remove(identity.Vault); }
            }
            catch (Exception error) when (!(error is InvalidOperationException)) { lock (sync) failure = error; throw; }
            finally { lock (sync) retiring.Remove(identity.Vault); metadata.Release(); }
        }
        public Task StopAsync()
        {
            lock (sync)
            {
                if (stopping != null) return stopping;
                stopping = Task.Run(async () =>
                {
                    await metadata.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        Task<JournalVaultRuntime>[] active;
                        lock (sync) { active = writers.Values.ToArray(); peers.Clear(); }
                        var errors = new List<Exception>();
                        foreach (var task in active)
                            try { await (await task.ConfigureAwait(false)).StopAsync().ConfigureAwait(false); }
                            catch (Exception error) { errors.Add(error); }
                        if (errors.Count != 0) throw new AggregateException("World journal teardown failed", errors);
                    }
                    finally { metadata.Release(); }
                });
                return stopping;
            }
        }
    }
}
