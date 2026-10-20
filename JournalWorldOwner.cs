using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;
using UnityEngine;

namespace ThorsKist
{
    // Owns one world. Legacy files are import sources only; no item Inventory is
    // constructed on the server and no legacy writer exists after this boundary.
    internal static class JournalWorldOwner
    {
        private static ZNet world;
        private static Task<JournalWorldRuntime> opening;
        internal static JournalWorldRuntime Runtime;
        private static Task metadata;
        private static Task shutdown = Task.CompletedTask;
        private static List<Action> committedReplies;
        private static Exception failure;
        private static readonly SystemReplicationClock clock = new SystemReplicationClock();
        private static readonly Dictionary<string, Guid> epochs = new Dictionary<string, Guid>();
        private static JournalSendQueue localRequests, localReplies;
        private static JournalPeerBinding localBinding;
        private static object localConnection;
        private static Task localWork = Task.CompletedTask, localDelivery = Task.CompletedTask;
        private static VaultIdentity clientVault;
        private static float retryAt;
        internal static bool Ready => Runtime != null && failure == null && Runtime.Failure == null;
        internal static bool MetadataBusy => metadata != null;
        internal static VaultIdentity Identity(string id) => Runtime?.Vaults.FirstOrDefault(v => v.Identity.Vault == id)?.Identity;
        internal static Guid PendingEpoch(string id)
        { if (!epochs.TryGetValue(id, out var epoch)) epochs[id] = epoch = Guid.NewGuid(); return epoch; }
        internal static void EnsureStarted()
        {
            if (!shutdown.IsCompleted || ZNet.instance == null || !ZNet.instance.IsServer() || ObjectDB.instance == null || opening != null || failure != null) return;
            world = ZNet.instance;
            string uid = world.GetWorldUID().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (uid == "0") return;
            var names = new Dictionary<int, string>();
            foreach (var prefab in ObjectDB.instance.m_items) if (prefab != null) names[prefab.name.GetStableHashCode()] = prefab.name;
            int version = ItemWire.InventoryVersion, capacity = 8 * Main.KistRows.Value;
            string root = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "ThorsKist");
            string legacy = Path.Combine(root, "kist_" + uid + ".dat");
            string database = Path.Combine(root, "kist_" + uid + ".sqlite");
            opening = Task.Run(async () =>
            {
                Directory.CreateDirectory(root);
                return await JournalWorldRuntime.OpenAsync(database, uid, legacy,
                    bytes => JournalLegacyDecoder.DecodeCaptured(bytes, uid, capacity, version, names)
                        .Select(v => new LegacyVaultImport(v.State, v.CopyHeader())).ToArray(),
                    bytes => Hird.ReadHeader(new ZPackage(bytes)).Members.Select(m => m.PlayerId).ToArray(), clock, 64);
            });
            Main.Log.LogInfo("KIST AUTHORITY starting SQLite/journal at " + database + "; legacy writes disabled; failures close access");
        }
        internal static void Tick()
        {
            EnsureStarted();
            if (opening != null && Runtime == null && failure == null && opening.IsCompleted)
            {
                if (opening.IsFaulted) { Fail(opening.Exception.GetBaseException()); return; }
                Runtime = opening.Result;
                KistServer.InstallHeaders(Runtime.Vaults.Select(v => v.CopyHeader()));
                new JournalDirectRpc(world, Resolve,
                    (peer, binding, outgoing, frame, token) => Runtime.ReceiveAsync(peer, binding, outgoing, frame, token),
                    binding => Observe(Runtime.DisconnectAsync(binding))).Install();
                Main.Log.LogInfo("KIST AUTHORITY READY SQLite/journal; " + Runtime.Vaults.Count + " hirds; .dat import once only, no fallback or dual writes");
            }
            if (Runtime?.Failure != null && failure == null) Fail(Runtime.Failure);
            if (metadata != null && metadata.IsCompleted)
            {
                var done = metadata; metadata = null;
                if (done.IsFaulted) Fail(done.Exception.GetBaseException());
                else { var replies = committedReplies; committedReplies = null; foreach (var reply in replies) reply(); }
            }
            if (failure != null) return;
            var player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null) return;
            if (!ZNet.instance.IsServer() && JournalDirectRpc.Current == null)
                new JournalDirectRpc(ZNet.instance, Resolve, (peer, binding, outgoing, frame, token) =>
                    JournalGameplaySession.Current?.Client.ReceiveAsync(frame) ?? Task.CompletedTask, binding => { }).Install();
            var session = JournalGameplaySession.Current;
            if (clientVault == null || KistClient.Hird == null || clientVault.Vault != KistClient.Hird.Id)
            { CloseLocal(); return; }
            if (!JournalLogout.Pending && session != null && (session.Failure != null || session.Client.ProcessingEnded))
            {
                Main.Log.LogWarning("KIST SESSION ended: " + (session.Failure ?? session.Client.Failure)?.Message);
                KistClient.Message("Kist session closed. Rejoining after cooldown."); CloseLocal(); retryAt = Time.realtimeSinceStartup + 31f;
            }
            if (!JournalLogout.Pending && JournalGameplaySession.Current == null && Time.realtimeSinceStartup >= retryAt)
            {
                JournalSendQueue outgoing;
                if (ZNet.instance.IsServer())
                {
                    if (!Ready || MetadataBusy) return;
                    localRequests = new JournalSendQueue(); localReplies = new JournalSendQueue(); localConnection = new object();
                    var identity = LocalIdentity(); if (identity == null) return;
                    localBinding = new JournalPeerBinding(identity); outgoing = localRequests;
                }
                else
                {
                    var server = ZNet.instance.GetServerPeer(); if (server == null) return;
                    outgoing = JournalDirectRpc.Current.SendQueue(server); if (outgoing == null) return;
                }
                session = new JournalGameplaySession(player.GetInventory(), outgoing, clock, new JournalTransferBudget(),
                    () => Player.m_localPlayer != null && ReferenceEquals(player, Player.m_localPlayer) && !player.IsDead() ? player.GetInventory() : null);
                JournalGameplaySession.Current = session;
                Main.Log.LogInfo("KIST SESSION starting journal login; world=" + clientVault.World + " vault=" + clientVault.Vault);
                session.Login(clientVault);
            }
            if (ZNet.instance.IsServer() && localRequests != null && JournalGameplaySession.Current != null)
            {
                var peer = LocalIdentity();
                if (peer == null || !localBinding.IsCurrent(peer)) { CloseLocal(); return; }
                if (localWork.IsFaulted) { Fail(localWork.Exception.GetBaseException()); return; }
                if (localWork.IsCompleted)
                {
                    var bytes = localRequests.Dequeue();
                    if (bytes != null)
                    {
                        var binding = localBinding; var outgoing = localReplies; var runtime = Runtime;
                        localWork = Task.Run(() => runtime.ReceiveAsync(peer, binding, outgoing, JournalWire.DecodeFrame(bytes)));
                    }
                }
                for (int i = 0; i < 2; i++) { var bytes = localReplies.Dequeue(); if (bytes == null) break;
                    var client = JournalGameplaySession.Current.Client; var previous = localDelivery;
                    localDelivery = Task.Run(async () => { await previous; await client.ReceiveAsync(JournalWire.DecodeFrame(bytes)); }); Observe(localDelivery); }
            }
        }
        private static JournalPeerIdentity LocalIdentity()
        {
            var player = Player.m_localPlayer; if (player == null || localConnection == null) return null;
            var resolved = Resolve("listen-host", player.GetPlayerID(), player.transform.position);
            return resolved.Vault == null ? null : new JournalPeerIdentity(localConnection, "listen-host:" + player.GetPlayerID(), player.GetPlayerID(), resolved.Vault, resolved.Access);
        }
        private static (VaultIdentity Vault, JournalAccess Access) Resolve(string account, long character, Vector3 position)
        {
            if (!Ready) return (null, JournalAccess.None);
            var vault = Runtime.Vaults.FirstOrDefault(v => v.IsMember(character)); if (vault == null) return (null, JournalAccess.None);
            var hird = Hird.ReadHeader(new ZPackage(vault.CopyHeader()));
            var access = JournalAccess.Login;
            if (hird.Members.Count >= Main.MinHirdSize.Value)
            {
                float distance = Main.UnlimitedRange.Value ? 0 : Vector3.Distance(position, hird.Anchor);
                if (distance <= Main.DepositRadius.Value) access |= JournalAccess.Deposit;
                if (distance <= Main.TakeRadius.Value) access |= JournalAccess.Take;
                if (Main.CraftFromKist.Value && distance <= Main.CraftRadius.Value) access |= JournalAccess.Craft | JournalAccess.Station;
            }
            return (vault.Identity, access);
        }
        internal static bool Authenticate(long sender, long character, out Vector3 position)
        {
            position = Vector3.zero; if (world == null || character == 0) return false;
            if (sender == ZNet.GetUID())
            {
                var p = Player.m_localPlayer; if (p == null || p.GetPlayerID() != character) return false;
                position = p.transform.position; return true;
            }
            var peer = world.GetPeer(sender);
            if (peer == null || !peer.IsReady() || !peer.m_socket.IsConnected() || peer.m_playerID != character
                || JournalDirectRpc.SocketAccount(peer) == null || peer.m_characterID.IsNone()) return false;
            var zdo = ZDOMan.instance?.GetZDO(peer.m_characterID);
            if (zdo == null || zdo.GetOwner() != peer.m_uid || zdo.GetLong(ZDOVars.s_playerID, 0L) != character) return false;
            position = zdo.GetPosition(); return true;
        }
        internal static void SelectVault(VaultIdentity vault)
        {
            if (clientVault != null && vault != null && clientVault.Matches(vault)) return;
            CloseLocal(); clientVault = vault; retryAt = 0;
        }
        private static void CloseLocal()
        {
            JournalGameplaySession.Current?.Dispose(); JournalGameplaySession.Current = null;
            if (localBinding != null) { localBinding.Close(); if (Runtime != null) Observe(Runtime.DisconnectAsync(localBinding)); }
            localRequests?.Dispose(); localReplies?.Dispose(); localRequests = null; localReplies = null; localBinding = null; localConnection = null;
        }
        internal static void CommitHeaders(byte[][] headers, List<Action> replies)
        {
            if (!Ready || metadata != null) { Fail(new IOException("Concurrent catalog change")); return; }
            var before = Runtime.Vaults.ToArray();
            var next = headers.Select(bytes => (Header: bytes, Hird: Hird.ReadHeader(new ZPackage(bytes)))).ToArray();
            var capturedEpochs = next.ToDictionary(v => v.Hird.Id, v => PendingEpoch(v.Hird.Id));
            committedReplies = replies;
            string worldId = world.GetWorldUID().ToString(); int capacity = 8 * Main.KistRows.Value;
            var runtime = Runtime;
            metadata = Task.Run(async () =>
            {
                foreach (var v in next)
                {
                    var old = before.FirstOrDefault(b => b.Identity.Vault == v.Hird.Id);
                    if (old == null) await runtime.CreateAsync(new VaultState(new VaultIdentity(worldId, v.Hird.Id, capturedEpochs[v.Hird.Id]), capacity, Array.Empty<StackRecord>()), v.Header);
                    else if (!old.CopyHeader().SequenceEqual(v.Header) && !await runtime.UpdateMetadataAsync(old.Identity, old.CopyHeader(), v.Header)) throw new IOException("Catalog changed concurrently");
                }
                foreach (var old in before.Where(b => !next.Any(v => v.Hird.Id == b.Identity.Vault))) await runtime.RetireEmptyAsync(old.Identity);
            });
        }
        internal static void Retire(string id, Action committed)
        {
            if (!Ready || metadata != null) return;
            var identity = Identity(id); if (identity == null) return;
            var runtime = Runtime;
            committedReplies = new List<Action> { committed };
            metadata = Task.Run(async () =>
            {
                try { await runtime.RetireEmptyAsync(identity); }
                catch (InvalidOperationException error)
                {
                    // An ordinary nonempty-vault refusal leaves catalog headers
                    // unchanged. Settlement may close sessions; they rejoin.
                    committedReplies = new List<Action> { () => KistClient.Message(error.Message) };
                }
            });
        }
        private static void Observe(Task task) => _ = task.ContinueWith(t => { if (t.IsFaulted) Main.Log.LogError("Kist journal operation failed: " + t.Exception.GetBaseException()); }, TaskScheduler.Default);
        private static void Fail(Exception error)
        {
            failure = error; CloseLocal(); Main.Log.LogError("KIST AUTHORITY FAILED CLOSED: " + error);
            KistClient.Message("Kist unavailable: SQLite/journal failed. See the log.");
        }
        internal static void Stop()
        {
            CloseLocal(); JournalDirectRpc.Current?.Dispose();
            var pendingMetadata = metadata; var runtime = Runtime; var startup = opening;
            if (runtime != null || startup != null)
            {
                shutdown = Task.Run(async () =>
                {
                    try { if (pendingMetadata != null) await pendingMetadata; }
                    finally
                    {
                        var active = runtime ?? await startup;
                        await active.StopAsync();
                    }
                });
                Observe(shutdown);
            }
            Runtime = null; opening = null; world = null; metadata = null; committedReplies = null; failure = null; clientVault = null; epochs.Clear(); localWork = Task.CompletedTask;
        }
    }
}
