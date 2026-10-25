using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharedInventory;

internal static class RitualStateFile
{
    public static RitualState Load(string path, long world)
    {
        if (!File.Exists(path))
        {
            // A leftover initial temporary file means an interrupted first write.
            if (File.Exists(path + ".tmp") || File.Exists(path + ".bak")) throw new InvalidDataException("Interrupted ritual state creation; manual recovery required.");
            var empty = new RitualState { World = world }; Save(path, empty); return empty;
        }
        var json = JObject.Parse(File.ReadAllText(path));
        if (json["Version"]?.Type != JTokenType.Integer || json["World"]?.Type != JTokenType.Integer ||
            json["DeadlineUtcTicks"]?.Type != JTokenType.Integer || json["Attempts"] is not JArray)
            throw new InvalidDataException("Incomplete ritual state.");
        var state = json.ToObject<RitualState>() ?? throw new InvalidDataException("Missing ritual state.");
        if (state.Version != 1 || state.World != world || state.DeadlineUtcTicks < 0 || state.DeadlineUtcTicks > DateTime.MaxValue.Ticks ||
            state.Attempts == null || state.Attempts.Any(a => a == null || string.IsNullOrEmpty(a.Id) || a.Victims == null || a.Victims.Count == 0 ||
                a.CommittedUtcTicks <= 0 || a.CommittedUtcTicks > DateTime.MaxValue.Ticks ||
                a.Victims.Select(v => v?.Peer).Distinct().Count() != a.Victims.Count || (!a.Punishment && a.Victims.Count != 1) ||
                a.Victims.Any(v => v == null || v.Peer == 0 || string.IsNullOrEmpty(v.Life) || string.IsNullOrEmpty(v.OwnerLife)) ||
                !new[] { "committed", "waiting-deaths", "reward-reserved", "finished", "incomplete" }.Contains(a.Status) ||
                !new[] { "none", "reserved", "published" }.Contains(a.Reward) || (a.Punishment && a.Reward != "none") ||
                (a.Status == "finished" && (a.Victims.Any(v => !v.Confirmed) || (!a.Punishment && a.Reward != "published"))) ||
                (a.Reward == "published" && a.Status != "finished") || (a.Reward == "reserved" && a.Status != "reward-reserved")))
            throw new InvalidDataException("Invalid ritual state; manual recovery required.");
        if (state.Attempts.Select(a => a.Id).Distinct().Count() != state.Attempts.Count) throw new InvalidDataException("Duplicate ritual IDs.");
        return state;
    }

    public static void Save(string path, RitualState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 4096, true))
            { writer.Write(JsonConvert.SerializeObject(state, Formatting.Indented)); writer.Flush(); }
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }
}

// Same ordered worker/main-thread completion pattern as inventory, with separate
// files and no Unity calls on the worker. Shutdown drains writes, never deaths.
internal sealed class RitualStorage
{
    private static Task previous = Task.CompletedTask;
    private readonly Worker worker;
    private readonly ConcurrentQueue<Action> main = new ConcurrentQueue<Action>();
    private readonly string path;
    private bool stopped;
    public RitualStorage(string path, long world, Action<RitualState?, Exception?> loaded)
    {
        this.path = path;
        worker = new Worker(() => { }, _ => { }, previous);
        worker.Post(() =>
        {
            try { var state = RitualStateFile.Load(path, world); main.Enqueue(() => loaded(state, null)); }
            catch (Exception error) { main.Enqueue(() => loaded(null, error)); }
        });
    }
    public void Save(RitualState state, Action<Exception?> complete) => worker.Post(() =>
    {
        Exception? failure = null;
        try { RitualStateFile.Save(path, state); } catch (Exception error) { failure = error; }
        main.Enqueue(() => complete(failure));
    });
    public void Pump() { while (!stopped && main.TryDequeue(out var action)) action(); }
    public void Stop() { if (stopped) return; stopped = true; previous = worker.Stop(); }
    public static void FinishShutdown() => previous.GetAwaiter().GetResult();
}
