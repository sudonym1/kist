using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ThorsKist.Sync;
using ThorsKist.Sync.Sqlite;

namespace ThorsKist
{
    // Full production client/vault lifetime over actual game RPCs, with a
    // scratch inventory. TCP supplies no account authentication: the fixture
    // identity is deliberately test-only and never installs JournalDirectRpc.
    internal sealed class JournalSessionTcpProbe : IDisposable
    {
        private readonly ZRpc clientRpc, serverRpc;
        private readonly ZSocket2 clientSocket, serverSocket;
        private readonly JournalSendQueue toServer = new JournalSendQueue(), toClient = new JournalSendQueue();
        private readonly JournalConnectionWorker clientWorker, serverWorker;
        private readonly JournalGameThread dispatch = new JournalGameThread(8);
        private readonly JournalPeerIdentity peer;
        private readonly JournalPeerBinding binding;
        private readonly VaultIdentity vault = new VaultIdentity("m7-mono", "session-loopback", Guid.NewGuid());
        private readonly SystemReplicationClock clock = new SystemReplicationClock();
        private readonly Inventory inventory = new Inventory("M7 session scratch", null, 2, 2);
        private readonly JournalCharacterInventory character;
        private readonly ItemDrop.ItemData source;
        private readonly ItemRecord record;
        private readonly Action<string> report;
        private readonly Task run;
        private readonly DateTime due = DateTime.UtcNow.AddSeconds(45);
        private JournalVaultRuntime authority;
        private JournalClientConnection client;
        private JournalGameplaySession gameplay;
        private bool disposed, reported;
        private int checks;
        private string transportFailure;
        internal JournalSessionTcpProbe(ItemDrop.ItemData source, string databasePath, Action<string> report)
        {
            this.source = source; this.report = report;
            source.m_customData["m7-large-record"] = new string('x', 100 * 1024);
            source.m_gridPos = Vector2i.zero; inventory.GetAllItems().Add(source);
            record = JournalItemCodec.Capture(source, 2);
            character = new JournalCharacterInventory(inventory);
            peer = new JournalPeerIdentity(new object(), "test-only:mono-loopback", 123, vault);
            binding = new JournalPeerBinding(peer);
            client = NewClient();
            serverWorker = new JournalConnectionWorker((frame, context, token) => authority.ReceiveAsync((JournalPeerIdentity)context, binding, toClient, frame, token));
            clientWorker = new JournalConnectionWorker((frame, token) => client.ReceiveAsync(frame));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var outgoing = new TcpClient(); outgoing.Connect((IPEndPoint)listener.LocalEndpoint);
                var incoming = listener.AcceptTcpClient();
                clientSocket = new ZSocket2(outgoing); serverSocket = new ZSocket2(incoming);
                clientRpc = new ZRpc(clientSocket); serverRpc = new ZRpc(serverSocket);
                serverRpc.Register<ZPackage>(JournalDirectRpc.RpcName, (rpc, package) =>
                { if (!ReferenceEquals(rpc, serverRpc) || !serverWorker.TryReceive(package.GetArray(), peer)) transportFailure = "Server RPC identity/queue rejected"; });
                clientRpc.Register<ZPackage>(JournalDirectRpc.RpcName, (rpc, package) =>
                { if (!ReferenceEquals(rpc, clientRpc) || !clientWorker.TryReceive(package.GetArray())) transportFailure = "Client RPC identity/queue rejected"; });
            }
            finally { listener.Stop(); }
            run = RunAsync(databasePath);
        }
        private JournalClientConnection NewClient() => new JournalClientConnection(character, dispatch.DispatchAsync, toServer, clock, new JournalTransferBudget());
        private void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        private async Task Wait(Func<bool> predicate)
        {
            while (!predicate())
            {
                if (DateTime.UtcNow >= due || disposed || transportFailure != null || client.Failure != null)
                    throw new Exception("Session probe interrupted: " + transportFailure, client.Failure);
                await Task.Delay(10);
            }
        }
        private async Task Acked(long sequence)
        {
            while ((await authority.ReadAsync()).Replication.Sessions.Last().Acknowledged < sequence)
            { if (DateTime.UtcNow >= due) throw new TimeoutException("Session acknowledgment deadline"); await Task.Delay(10); }
        }
        private async Task RunAsync(string databasePath)
        {
            try
            {
                authority = await JournalVaultRuntime.OpenAsync(new SqliteJournalStorage(databasePath, vault),
                    new VaultState(vault, 2, Array.Empty<StackRecord>()), clock, 4, new JournalTransferBudget());
                Check((await client.LoginAsync(vault, Guid.NewGuid())).Status == SessionStatus.Ok, "Native login control reply");
                await Wait(() => client.AccessEnabled);
                Check(client.Sequence == 1, "Native login image and barrier acknowledgment activate session");
                var address = client.Address; var request = Guid.NewGuid(); var stack = Guid.NewGuid();
                Check(character.TryReserve(request, character.Identify(source), 2) && source.m_stack == 10, "Native reserve keeps exact source present");
                var deposit = new ItemCommand(address, request, stack, ItemCommandKind.Deposit, record);
                var result = await client.ExecuteAsync(deposit);
                Check(result.Command == CommandStatus.Accepted, "Native deposit request commits");
                await Wait(() => source.m_stack == 8 && client.Sequence == 2); await Acked(2);
                Check(!character.HasReservations && (await authority.ReadAsync()).Current.Stacks.Single().Item.Quantity == 2,
                    "Native verified removal precedes persisted acknowledgment");
                var retry = await client.ExecuteAsync(deposit);
                Check(retry.Operation == result.Operation && source.m_stack == 8 && client.Sequence == 2, "Native retry does not repeat deposit removal");
                var take = await client.ExecuteAsync(new ItemCommand(address, Guid.NewGuid(), stack, ItemCommandKind.Withdraw, record));
                Check(take.Command == CommandStatus.Accepted, "Native withdrawal request commits");
                await Wait(() => client.Sequence == 3 && inventory.GetAllItems().Sum(i => i.m_stack) == 10); await Acked(3);
                Check((await authority.ReadAsync()).Current.Stacks.Count == 0, "Native take adds exact metadata and acknowledges debit");
                var logoutId = Guid.NewGuid(); await client.LogoutAsync(logoutId); await Wait(() => client.ProcessingStopped);
                Check((await client.LogoutAsync(logoutId)).Phase == SessionPhase.Closed, "Native completed logout retry preserves closed session");
                await client.CloseAsync(); client = NewClient();
                await client.LoginAsync(vault, Guid.NewGuid()); await Wait(() => client.AccessEnabled);
                Check(client.Address.Session != address.Session && inventory.GetAllItems().Sum(i => i.m_stack) == 10,
                    "Native rejoin skips old character effects and retains fresh UUID");
                await client.LogoutAsync(Guid.NewGuid()); await Wait(() => client.ProcessingStopped);
                await client.CloseAsync(); character.Dispose();
                gameplay = new JournalGameplaySession(inventory, toServer, clock, new JournalTransferBudget());
                client = gameplay.Client;
                await client.LoginAsync(vault, Guid.NewGuid()); await Wait(() => gameplay.AccessEnabled);
                int before = source.m_stack;
                Check(gameplay.Deposit(inventory, source, 2) && source.m_stack == before,
                    "Gameplay deposit enters reservation without optimistic removal");
                await Wait(() => source.m_stack == before - 2 && gameplay.Stock.Count == 1);
                var row = gameplay.Stock.Rows.Single(); var displayed = row.Item; var stackId = row.Stack.Id;
                Check(gameplay.Stock.Identify(displayed) == stackId && row.Stack.Item.Quantity == 2,
                    "Gameplay display binds exact original stack UUID");
                Check(gameplay.Take(stackId, 1), "Gameplay withdrawal uses authoritative UUID");
                await Wait(() => gameplay.Stock.Rows.Single().Stack.Item.Quantity == 1);
                Check(gameplay.Stock.Identify(displayed) == Guid.Empty && gameplay.Stock.Rows.Single().Stack.Id == stackId,
                    "Delta replaces only changed display object while preserving stack UUID");
                Check(gameplay.Take(stackId, 1), "Previously captured UUID still addresses residual stack");
                await Wait(() => gameplay.Stock.Count == 0 && inventory.GetAllItems().Sum(i => i.m_stack) == 10);
                await Acked(client.Sequence);
                Check(!gameplay.Take(stackId, 1), "Deleted UUID cannot target another item or slot");
                report("M7 SESSION TCP MONO PASS " + checks + " assertions: real ZRpc login/deposit/take/ack/retry/logout/rejoin with SQLite and bounded game-thread effects");
            }
            catch (Exception error) { report("M7 SESSION TCP MONO FAIL " + error); }
            finally
            {
                await client.CloseAsync();
                if (authority != null) await authority.StopAsync();
                gameplay?.Dispose(); character.Dispose();
            }
        }
        internal void Tick()
        {
            if (disposed) return;
            dispatch.Drain(4);
            gameplay?.Tick();
            for (int i = 0; i < 2; i++)
            {
                var bytes = toServer.Dequeue(); if (bytes != null) clientRpc.Invoke(JournalDirectRpc.RpcName, new ZPackage(bytes));
                bytes = toClient.Dequeue(); if (bytes != null) serverRpc.Invoke(JournalDirectRpc.RpcName, new ZPackage(bytes));
            }
            clientRpc.Update(0.01f); serverRpc.Update(0.01f);
            if (serverWorker.Failure != null) transportFailure = serverWorker.Failure.ToString();
            if (clientWorker.Failure != null) transportFailure = clientWorker.Failure.ToString();
            if (run.IsCompleted) { if (run.IsFaulted) report("M7 SESSION TCP MONO FAIL cleanup " + run.Exception); Dispose(); }
            else if (!reported && (transportFailure != null || DateTime.UtcNow >= due))
            { reported = true; report("M7 SESSION TCP MONO FAIL transport/deadline " + transportFailure); Dispose(); }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            dispatch.Dispose(); clientWorker.Dispose(); serverWorker.Dispose();
            toServer.Dispose(); toClient.Dispose();
            clientRpc.Dispose(); serverRpc.Dispose(); clientSocket.Dispose(); serverSocket.Dispose();
        }
    }
}
