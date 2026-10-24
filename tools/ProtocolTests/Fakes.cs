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
        public readonly List<object> Warnings = new List<object>();
        public void LogWarning(object warning) => Warnings.Add(warning);
    }
}

public sealed class ZNet
{
    public static ZNet instance = new ZNet();
    public bool Server = true;
    public long World = 1;
    public bool IsServer() => Server;
    public long GetWorldUID() => World;
    public static long GetUID() => 1;
    public Peer GetServerPeer() => new Peer();
    public Peer? GetPeer(long id) => id > 1 && id < 100 ? new Peer { m_uid = id } : null;
    public sealed class Peer { public long m_uid = 1; }
}

// Loopback substitutes only routing; packages, codecs, worker, database, and
// persistence are production code, including the actual game's ZPackage.
public sealed class ZRoutedRpc
{
    public static long Everybody = 0;
    public Action<long, string, ZPackage>? Transport;
    public Action<long, string, ZPackage>? ReplyReceived;
    private readonly int mainThread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, Action<long, ZPackage>> handlers = new Dictionary<string, Action<long, ZPackage>>();
    public void Register<T>(string name, Action<long, T> handler) =>
        handlers.Add(name, (sender, package) => handler(sender, (T)(object)package));
    public void InvokeRoutedRPC(long target, string name, ZPackage package)
    {
        if (Environment.CurrentManagedThreadId != mainThread) throw new Exception("Routing left the main thread");
        if (Transport != null) { Transport(target, name, new ZPackage(package.GetArray())); return; }
        if (target != 1 && ReplyReceived != null) ReplyReceived(target, name, new ZPackage(package.GetArray()));
        else handlers[name](1, new ZPackage(package.GetArray()));
    }
    public void Inject(long peer, string name, ZPackage package)
    {
        if (Environment.CurrentManagedThreadId != mainThread) throw new Exception("Injection left the main thread");
        handlers[name](peer, new ZPackage(package.GetArray()));
    }
}

namespace UnityEngine { public static class Time { public static float unscaledTime; } }
public sealed class Player
{
    public static Player? m_localPlayer;
    public bool Dead;
    public bool IsDead() => Dead;
    public sealed class Position { public UnityEngine.Vector3 position; }
    public Position transform = new Position();
}
public sealed class ZDO
{
    public ZDOID m_uid;
    public int Prefab;
    public UnityEngine.Vector3 Position;
    public bool IsValid() => true;
    public int GetPrefab() => Prefab;
    public UnityEngine.Vector3 GetPosition() => Position;
}
public sealed class ZDOMan
{
    public static ZDOMan instance = new ZDOMan();
    public readonly Dictionary<ZDOID, ZDO> Records = new Dictionary<ZDOID, ZDO>();
    public int Scans;
    public bool Complete = true;
    public ZDO? GetZDO(ZDOID id) => Records.TryGetValue(id, out var record) ? record : null;
    public bool GetAllZDOsWithPrefabIterative(string prefab, List<ZDO> records, ref int index)
    {
        Scans++;
        foreach (var record in Records.Values) if (record.Prefab == prefab.GetStableHashCode()) records.Add(record);
        return Complete;
    }
}
