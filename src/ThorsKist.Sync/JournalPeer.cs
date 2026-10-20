using System;
using System.Collections.Generic;
using System.IO;

namespace ThorsKist.Sync
{
    [Flags]
    public enum JournalAccess { None = 0, Login = 1, Deposit = 2, Take = 4, Craft = 8, Station = 16, All = 31 }
    // Facts captured from the current authenticated connection on the game
    // thread; never deserialize these from the command's claimed header.
    public sealed class JournalPeerIdentity
    {
        public object Connection { get; }
        public string Account { get; }
        public long Character { get; }
        public VaultIdentity Vault { get; }
        public JournalAccess Access { get; }
        public JournalPeerIdentity(object connection, string account, long character, VaultIdentity vault, JournalAccess access = JournalAccess.All)
        {
            if (connection == null || string.IsNullOrWhiteSpace(account) || character == 0) throw new ArgumentException("Authenticated peer required");
            if ((access & ~JournalAccess.All) != 0) throw new ArgumentOutOfRangeException(nameof(access));
            Connection = connection; Account = account; Character = character; Vault = vault ?? throw new ArgumentNullException(nameof(vault)); Access = access;
        }
    }

    public sealed class JournalPeerBinding
    {
        private readonly JournalPeerIdentity peer;
        private SessionAddress? session;
        private bool closed;
        private readonly object sync = new object();
        public SessionAddress? Session { get { lock (sync) return session; } }
        public bool IsClosed { get { lock (sync) return closed; } }
        public JournalPeerBinding(JournalPeerIdentity peer) { this.peer = peer; }
        public bool IsCurrent(JournalPeerIdentity current)
        { lock (sync) return !closed && ReferenceEquals(peer.Connection, current.Connection)
            && peer.Account == current.Account && peer.Character == current.Character && peer.Vault.Matches(current.Vault); }
        public bool Bind(JournalPeerIdentity current, SessionAddress address)
        {
            lock (sync)
            {
            if (!IsCurrent(current) || !peer.Vault.Contains(address) || peer.Account != address.Player
                || (session != null && !JournalChecksum.SameAddress(session, address))) return false;
            session = address; return true;
            }
        }
        public bool Authorizes(JournalPeerIdentity current, SessionAddress address)
        { lock (sync) return IsCurrent(current) && session != null && JournalChecksum.SameAddress(session, address); }
        // Kist sessions are shorter-lived than game connections. Release only
        // after the writer confirms closure; old UUIDs immediately lose ingress.
        public bool ReleaseSession(JournalPeerIdentity current, SessionAddress address)
        {
            lock (sync)
            {
                if (!Authorizes(current, address)) return false;
                session = null; return true;
            }
        }
        public void Close() { lock (sync) { closed = true; session = null; } }
    }

    // Each direct connection has its own bounded queue. A blocked or flooding
    // peer cannot occupy another peer's queue. No Unity calls under this lock.
    public sealed class JournalSendQueue : IDisposable
    {
        private readonly struct Queued
        {
            internal readonly byte[] Bytes;
            internal readonly JournalDiagnostics.Scope Waiting;
            internal Queued(byte[] bytes, JournalDiagnostics.Scope waiting) { Bytes = bytes; Waiting = waiting; }
        }
        private readonly Queue<Queued> frames = new Queue<Queued>();
        private readonly string diagnosticName;
        private readonly int maximumBytes, maximumFrames;
        private int bytes;
        private bool closed;
        public JournalSendQueue(int maximumBytes = 256 * 1024, int maximumFrames = 16, string diagnosticName = "transport.outgoing_queue.wait")
        {
            if (maximumBytes < JournalWire.MaximumFrameBytes || maximumFrames <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            this.maximumBytes = maximumBytes; this.maximumFrames = maximumFrames; this.diagnosticName = diagnosticName;
        }
        // Encoding, including large image hashing, must be performed on workers.
        public bool TryEnqueue(byte[] encodedFrame)
        {
            if (encodedFrame == null || encodedFrame.Length > JournalWire.MaximumFrameBytes || encodedFrame.Length < JournalWire.FrameHeaderBytes)
                throw new InvalidDataException("Invalid outbound frame size");
            lock (frames)
            {
                if (closed || frames.Count >= maximumFrames || encodedFrame.Length > maximumBytes - bytes) return false;
                frames.Enqueue(new Queued((byte[])encodedFrame.Clone(), JournalDiagnostics.Measure(diagnosticName, JournalTimingKind.Wait, JournalTimingThread.Any, encodedFrame.Length)));
                bytes += encodedFrame.Length; return true;
            }
        }
        public byte[]? Dequeue()
        {
            lock (frames)
            {
                if (closed || frames.Count == 0) return null;
                var frame = frames.Dequeue(); bytes -= frame.Bytes.Length; frame.Waiting.Dispose(); return frame.Bytes;
            }
        }
        public void Dispose() { lock (frames) { closed = true; frames.Clear(); bytes = 0; } }
    }
}
