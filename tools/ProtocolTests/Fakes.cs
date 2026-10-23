using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace BepInEx
{
    public static class Paths { public static string ConfigPath = ""; }
}
namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public readonly ConcurrentQueue<object> Errors = new ConcurrentQueue<object>();
        public void LogError(object error) => Errors.Enqueue(error);
    }
}

public sealed class ZNet
{
    public static ZNet instance = new ZNet();
    public bool IsServer() => true;
    public long GetWorldUID() => 1;
    public static long GetUID() => 1;
    public Peer GetServerPeer() => new Peer();
    public sealed class Peer { public long m_uid = 1; }
}

// Loopback substitutes only routing; packages, codecs, worker, database, and
// persistence are production code, including the actual game's ZPackage.
public sealed class ZRoutedRpc
{
    public Action<long, string, ZPackage>? ReplyReceived;
    private readonly int mainThread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, Action<long, ZPackage>> handlers = new Dictionary<string, Action<long, ZPackage>>();
    public void Register<T>(string name, Action<long, T> handler) =>
        handlers.Add(name, (sender, package) => handler(sender, (T)(object)package));
    public void InvokeRoutedRPC(long target, string name, ZPackage package)
    {
        if (Environment.CurrentManagedThreadId != mainThread) throw new Exception("Routing left the main thread");
        if (target != 1 && ReplyReceived != null) ReplyReceived(target, name, new ZPackage(package.GetArray()));
        else handlers[name](1, new ZPackage(package.GetArray()));
    }
    public void Inject(long peer, string name, ZPackage package)
    {
        if (Environment.CurrentManagedThreadId != mainThread) throw new Exception("Injection left the main thread");
        handlers[name](peer, new ZPackage(package.GetArray()));
    }
}
