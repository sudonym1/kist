using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ThorsKist.Sync;

namespace ThorsKist
{
    // Codec + bounded worker + real game RPC/stream framing. TCP is deliberately
    // not accepted as an authenticated game account by JournalDirectRpc.
    internal sealed class JournalTcpProbe : IDisposable
    {
        private readonly ZRpc sender, receiver;
        private readonly ZSocket2 sendingSocket, receivingSocket;
        private readonly JournalConnectionWorker worker;
        private readonly JournalTransferReceiver transfers;
        private readonly Queue<byte[]> outbound = new Queue<byte[]>();
        private readonly Action<string> report;
        private readonly DateTime due = DateTime.UtcNow.AddSeconds(15);
        private readonly ItemCommand[] commands;
        private readonly byte[] image = new byte[JournalTransfer.ChunkBytes * 2 + 7];
        private int processed, chunks;
        private volatile bool snapshotComplete;
        private string failure;
        private bool complete;
        private bool disposed;
        internal JournalTcpProbe(Action<string> report)
        {
            this.report = report;
            var address = new SessionAddress("loopback", "scratch", Guid.NewGuid(), "test-only", Guid.NewGuid());
            commands = Enumerable.Range(0, 4).Select(i => new ItemCommand(address, Guid.NewGuid(), Guid.NewGuid(), ItemCommandKind.Deposit,
                new ItemRecord("unknown-mod-item", i + 1, 109, Enumerable.Repeat((byte)i, 1024).ToArray()))).ToArray();
            new Random(246).NextBytes(image);
            foreach (var command in commands)
                outbound.Enqueue(JournalWire.EncodeFrame(new JournalFrame(JournalMessage.Command, command.RequestId, JournalWire.EncodeCommand(command))));
            var transfer = new JournalTransfer(Guid.NewGuid(), image);
            for (int i = 0; i < transfer.FrameCount; i++) outbound.Enqueue(JournalWire.EncodeFrame(transfer.Frame(i)));
            transfers = new JournalTransferReceiver(new JournalTransferBudget(image.Length), new SystemReplicationClock());
            worker = new JournalConnectionWorker((frame, token) =>
            {
                if (frame.Message == JournalMessage.Command)
                {
                    var command = JournalWire.DecodeCommand(frame.CopyBody()); var expected = commands[processed];
                    if (command.RequestId != expected.RequestId || frame.Correlation != expected.RequestId
                        || command.StackId != expected.StackId || !JournalChecksum.SameItem(command.Item, expected.Item))
                        throw new Exception("Loopback command identity/payload changed");
                    Interlocked.Increment(ref processed);
                }
                else
                {
                    var completed = transfers.Accept(frame); Interlocked.Increment(ref chunks);
                    if (completed != null)
                    {
                        if (!completed.SequenceEqual(image)) throw new Exception("Loopback image changed");
                        snapshotComplete = true;
                    }
                }
                return System.Threading.Tasks.Task.CompletedTask;
            });
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var outgoing = new TcpClient(); outgoing.Connect((IPEndPoint)listener.LocalEndpoint);
                var incoming = listener.AcceptTcpClient();
                sendingSocket = new ZSocket2(outgoing); receivingSocket = new ZSocket2(incoming);
                sender = new ZRpc(sendingSocket); receiver = new ZRpc(receivingSocket);
                receiver.Register<ZPackage>(JournalDirectRpc.RpcName, (rpc, package) =>
                {
                    if (!ReferenceEquals(rpc, receiver) || !worker.TryReceive(package.GetArray())) failure = "Loopback RPC identity/queue rejected";
                });
            }
            finally { listener.Stop(); }
        }
        internal void Tick()
        {
            if (complete) return;
            // Bounded work, also while an asynchronous decode is outstanding.
            for (int i = 0; i < 2 && outbound.Count != 0; i++) sender.Invoke(JournalDirectRpc.RpcName, new ZPackage(outbound.Dequeue()));
            sender.Update(0.01f); receiver.Update(0.01f);
            if (failure == null && worker.Failure != null) failure = worker.Failure.ToString();
            if (failure == null && DateTime.UtcNow <= due && (!snapshotComplete || processed != commands.Length)) return;
            complete = true;
            report(failure == null && snapshotComplete && processed == 4 && chunks == 3
                ? "M6 JOURNAL TCP PASS 4 exact commands and 3 snapshot chunks through bounded worker and real ZRpc/ZSocket2"
                : "M6 JOURNAL TCP FAIL processed=" + processed + " chunks=" + chunks + " " + failure);
            Dispose();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; complete = true; worker.Dispose();
            // Receiver belongs to the serial worker; release only after it stops.
            _ = worker.Completion.ContinueWith(_ => transfers.Dispose());
            sender?.Dispose(); receiver?.Dispose(); sendingSocket?.Dispose(); receivingSocket?.Dispose();
        }
    }
}
