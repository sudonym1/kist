using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ThorsKist
{
    // Exercise the installed game's ZRpc + ZSocket2, not a transport imitation.
    // Loopback only; no Steam lobby, remote peer, or second account required.
    internal sealed class TcpProbe : IDisposable
    {
        private readonly ZRpc sender, receiver;
        private readonly ZSocket2 sendingSocket, receivingSocket;
        private readonly Action<string> report;
        private int received;
        private readonly DateTime due = DateTime.UtcNow.AddSeconds(15);
        private string failure;
        private bool complete;
        public TcpProbe(Action<string> report)
        {
            this.report = report;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var outgoing = new TcpClient();
                outgoing.Connect((IPEndPoint)listener.LocalEndpoint);
                var incoming = listener.AcceptTcpClient();
                sendingSocket = new ZSocket2(outgoing);
                receivingSocket = new ZSocket2(incoming);
                sender = new ZRpc(sendingSocket); receiver = new ZRpc(receivingSocket);
                receiver.Register<ZPackage>("ThorsKist_M5_Probe", (rpc, p) =>
                {
                    try
                    {
                        int sequence = p.ReadInt(); string data = p.ReadString();
                        if (!ReferenceEquals(rpc, receiver) || sequence != received || data != new string((char)('a' + sequence % 26), 1024))
                            failure = "Wrong sender, sequence or payload at " + received;
                        received++;
                    }
                    catch (Exception error) { failure = error.ToString(); }
                });
                for (int i = 0; i < 256; i++)
                {
                    var p = new ZPackage(); p.Write(i); p.Write(new string((char)('a' + i % 26), 1024));
                    sender.Invoke("ThorsKist_M5_Probe", p);
                }
            }
            finally { listener.Stop(); }
        }
        public void Tick()
        {
            if (complete) return;
            sender.Update(0.01f); receiver.Update(0.01f);
            if (failure != null || DateTime.UtcNow > due || received == 256)
            {
                complete = true;
                report(failure == null && received == 256 ? "M5 TCP RPC PASS 256 ordered 1 KiB messages over real ZRpc/ZSocket2 loopback"
                    : "M5 TCP RPC FAIL received=" + received + " " + failure);
                Dispose();
            }
        }
        public void Dispose()
        {
            complete = true; sender?.Dispose(); receiver?.Dispose(); sendingSocket?.Dispose(); receivingSocket?.Dispose();
        }
    }
}
