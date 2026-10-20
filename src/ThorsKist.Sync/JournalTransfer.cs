using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ThorsKist.Sync
{
    // One immutable login image per accepted login, encoded on a worker. The
    // caller advances frames only when its bounded peer send queue accepts one.
    public sealed class JournalTransfer
    {
        internal const int HeaderBytes = 4 + 4 + 32;
        public const int ChunkBytes = JournalWire.MaximumFrameBody - HeaderBytes;
        private readonly byte[] image, digest;
        public Guid Id { get; }
        private readonly JournalMessage message;
        public int FrameCount => (image.Length + ChunkBytes - 1) / ChunkBytes;
        public JournalTransfer(Guid id, byte[] image, JournalMessage message = JournalMessage.Transfer)
        {
            if (id == Guid.Empty || image == null || image.Length == 0 || image.Length > JournalWire.MaximumTransferBytes)
                throw new ArgumentException("Invalid transfer bounds");
            if (message != JournalMessage.Transfer && message != JournalMessage.CommandChunk && message != JournalMessage.EntriesChunk) throw new ArgumentException("Invalid transfer direction");
            Id = id; this.image = (byte[])image.Clone(); this.message = message;
            using (var hash = SHA256.Create()) digest = hash.ComputeHash(this.image);
        }
        public JournalFrame Frame(int index)
        {
            if (index < 0 || index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
            int offset = checked(index * ChunkBytes); int size = Math.Min(ChunkBytes, image.Length - offset);
            return new JournalFrame(message, Id, JournalWire.Encode(w =>
            { w.Write(image.Length); w.Write(offset); w.Write(digest); w.Write(image, offset, size); }, JournalWire.MaximumFrameBody));
        }
    }

    // Shared across connections, in addition to the per-connection single image
    // bound. Reserve declared length before allocating so peers cannot each
    // independently consume the full server transfer allowance.
    public sealed class JournalTransferBudget
    {
        private readonly long maximum;
        private readonly object sync = new object();
        private long used;
        public JournalTransferBudget(long maximum = 128L * 1024 * 1024)
        { if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum)); this.maximum = maximum; }
        public long Used { get { lock (sync) return used; } }
        internal bool Reserve(int bytes) { lock (sync) { if (bytes > maximum - used) return false; used += bytes; return true; } }
        internal void Release(int bytes) { lock (sync) used -= bytes; }
    }

    // Connection-owned, used by its serial worker only. No client files. There
    // is no gap/retransmit layer: a malformed/late transfer closes the session.
    public sealed class JournalTransferReceiver : IDisposable
    {
        private readonly JournalTransferBudget budget;
        private readonly IReplicationClock clock;
        private byte[]? image, digest;
        private Guid id;
        private int received;
        private TimeSpan due;
        private bool disposed;
        private readonly int maximum;
        private JournalMessage message;
        public JournalTransferReceiver(JournalTransferBudget budget, IReplicationClock clock, int maximum = JournalWire.MaximumTransferBytes)
        { if (maximum <= 0 || maximum > JournalWire.MaximumTransferBytes) throw new ArgumentOutOfRangeException(nameof(maximum)); this.budget = budget; this.clock = clock; this.maximum = maximum; }
        public byte[]? Accept(JournalFrame frame)
        {
            if (disposed) throw new ObjectDisposedException(nameof(JournalTransferReceiver));
            try
            {
                return JournalWire.Decode(frame.CopyBody(), r =>
                {
                    if (frame.Message != JournalMessage.Transfer && frame.Message != JournalMessage.CommandChunk && frame.Message != JournalMessage.EntriesChunk) throw new InvalidDataException("Not a transfer");
                    int total = r.ReadInt32(), offset = r.ReadInt32(); var expected = r.ReadBytes(32);
                    int size = checked((int)(r.BaseStream.Length - r.BaseStream.Position));
                    if (total <= 0 || total > maximum || offset < 0 || offset >= total
                        || expected.Length != 32 || size != Math.Min(JournalTransfer.ChunkBytes, total - offset))
                        throw new InvalidDataException("Invalid transfer chunk");
                    if (image == null)
                    {
                        if (offset != 0 || !budget.Reserve(total)) throw new InvalidDataException("Transfer start/budget rejected");
                        try { image = new byte[total]; } catch { budget.Release(total); throw; }
                        id = frame.Correlation; message = frame.Message; digest = expected; received = 0; due = clock.Elapsed + TimeSpan.FromSeconds(30);
                    }
                    if (clock.Elapsed >= due || id != frame.Correlation || message != frame.Message || total != image.Length || offset != received || !expected.SequenceEqual(digest!))
                        throw new InvalidDataException("Transfer identity/order/deadline mismatch");
                    int n = r.Read(image, received, size); if (n != size) throw new EndOfStreamException(); received += n;
                    if (received != total) return null;
                    using (var hash = SHA256.Create())
                        if (!hash.ComputeHash(image).SequenceEqual(digest!)) throw new InvalidDataException("Transfer checksum mismatch");
                    var completed = image; Reset(); return completed;
                }, JournalWire.MaximumFrameBody);
            }
            catch { Reset(); throw; }
        }
        public bool Expire()
        {
            if (image == null || clock.Elapsed < due) return false;
            Reset(); return true;
        }
        private void Reset() { if (image != null) budget.Release(image.Length); image = null; digest = null; received = 0; id = Guid.Empty; }
        public void Dispose() { Reset(); disposed = true; }
    }
}
