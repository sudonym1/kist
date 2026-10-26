using System;
using System.Collections.Generic;
using System.Linq;
using SharedInventory;

// Lifecycle doubles exercise the real controller, not native animations/spawning.
namespace UnityEngine
{
    public enum FindObjectsSortMode { None }
    public static class Object
    {
        public static T[] FindObjectsByType<T>(FindObjectsSortMode mode) => Raven.Instances.OfType<T>().ToArray();
    }
}

public sealed class ZoneSystem { public static ZoneSystem? instance = new ZoneSystem(); }
public sealed class Chat
{
    public static Chat? instance = new Chat();
    public readonly List<GameObject> Cleared = new List<GameObject>();
    public readonly HashSet<GameObject> Visible = new HashSet<GameObject>();
    public bool IsDialogVisible(GameObject raven) => Visible.Contains(raven);
    public void ClearNpcText(GameObject raven) { Cleared.Add(raven); Visible.Remove(raven); }
}
public sealed class Tutorial
{
    public sealed class TutorialText
    {
        public string m_name = "", m_topic = "", m_text = "", m_label = "", m_globalKeyTrigger = "", m_tutorialTrigger = "";
        public bool m_isMunin;
    }
    public static Tutorial? instance = new Tutorial();
    public GameObject? m_ravenPrefab = new GameObject();
    public bool ThrowAfterQueue;
    public List<TutorialText> m_texts = new List<TutorialText>();
    public void ShowText(string key, bool force)
    {
        var text = m_texts.Find(t => t.m_name == key)!;
        if (Raven.Instances.Count == 0) Raven.Instances.Add(new Raven());
        Raven.AddTempText(key, text.m_topic, text.m_text, text.m_label, text.m_isMunin);
        if (ThrowAfterQueue) throw new InvalidOperationException("Native tutorial failure after queueing");
    }
}
public sealed class Raven
{
    public sealed class RavenText
    {
        public string m_key = "", m_topic = "", m_text = "", m_label = "";
        public bool m_munin, m_isTutorial = true;
    }
    public static bool m_tutorialsEnabled = true;
    private static List<RavenText> m_tempTexts = new List<RavenText>();
    private RavenText? m_currentText;
    public static readonly List<Raven> Instances = new List<Raven>();
    public GameObject gameObject = new GameObject();
    public bool Away = true, Flying;
    public bool IsAway() => Away;
    public bool IsFlying() => Flying;
    public static List<RavenText> Queue => m_tempTexts;
    public RavenText? Current { get => m_currentText; set => m_currentText = value; }
    public static void AddTempText(string key, string topic, string text, string label, bool munin)
    {
        if (m_tempTexts.Any(t => t.m_key == key)) return;
        m_tempTexts.Add(new RavenText { m_key = key, m_topic = topic, m_text = text, m_label = label, m_munin = munin });
    }
}

internal static partial class Program
{
    private static RavenTestControls RavenSetup()
    {
        Game.instance = new Game(); ZoneSystem.instance = new ZoneSystem(); Chat.instance = new Chat();
        Tutorial.instance = new Tutorial(); Raven.Instances.Clear(); Raven.Queue.Clear(); Raven.m_tutorialsEnabled = true;
        return new RavenTestControls(error => throw new Exception("Unexpected controller failure", error));
    }
    private static void RavenTests()
    {
        Test("POC keeps real tutorials paused while its recreated native raven dialog is being read", () =>
        {
            var control = RavenSetup(); control.Press(); Raven.Instances.Clear();
            var replacement = new Raven { Current = Raven.Queue.Single() }; Raven.Instances.Add(replacement);
            Chat.instance!.Visible.Add(replacement.gameObject);
            Player.m_localPlayer.SetSeenTutorial(RavenTestControls.Key); control.Update();
            Equal(true, control.Active); Chat.instance.Visible.Clear(); Equal(false, control.Active); control.Stop();
        });
        Test("Raven partial queue failure rolls back only the sample and blocks retries until a new session", () =>
        {
            RavenSetup(); var errors = 0; var control = new RavenTestControls(_ => errors++);
            var unrelated = new Raven.RavenText { m_key = "other.mod" }; Raven.Queue.Add(unrelated);
            Tutorial.instance!.ThrowAfterQueue = true;
            control.Press(); control.Press();
            Equal(1, errors); Equal(false, control.CanQueue);
            Equal(unrelated, Raven.Queue.Single()); Equal(0, Tutorial.instance.m_texts.Count);
            Equal(false, Player.m_localPlayer.HaveSeenTutorial(RavenTestControls.Key));
            Tutorial.instance.ThrowAfterQueue = false; control.Stop();
            Equal(true, control.CanQueue); control.Press(); Equal(2, Raven.Queue.Count); control.Stop();
        });
        Test("Raven POC is manual, local and idempotent while queued", () =>
        {
            var control = RavenSetup(); control.Update();
            Equal(0, Raven.Queue.Count); Equal(0, Tutorial.instance!.m_texts.Count); Equal(0, Raven.Instances.Count);
            control.Press(); control.Press(); control.Press();
            Equal(1, Raven.Queue.Count); Equal(1, Tutorial.instance.m_texts.Count); Equal(1, Raven.Instances.Count);
            var text = Raven.Queue.Single();
            Equal(RavenTestControls.Key, text.m_key); Equal(false, text.m_munin); Equal(true, text.m_isTutorial);
            Equal("", text.m_label); Equal("", Tutorial.instance.m_texts.Single().m_globalKeyTrigger);
            Equal("", Tutorial.instance.m_texts.Single().m_tutorialTrigger);
            Equal(true, control.Status.Contains("queued")); control.Stop();
        });
        Test("Raven acknowledgement/replay preserves vanilla and other mod state and order", () =>
        {
            var control = RavenSetup(); var player = Player.m_localPlayer;
            player.SetSeenTutorial("vanilla"); player.SetSeenTutorial("other.mod");
            var vanilla = new Raven.RavenText { m_key = "vanilla" }; var other = new Raven.RavenText { m_key = "other.mod" };
            Raven.Queue.Add(vanilla); control.Press(); Raven.Queue.Add(other);
            var initial = Raven.Queue.Single(t => t.m_key == RavenTestControls.Key);
            var raven = Raven.Instances.Single(); raven.Current = initial;
            player.SetSeenTutorial(RavenTestControls.Key); control.Update();
            Equal(true, control.Status.Contains("acknowledged"));
            control.Press();
            Equal(false, player.HaveSeenTutorial(RavenTestControls.Key)); Equal(3, Raven.Queue.Count);
            Equal(vanilla, Raven.Queue[0]); Equal(other, Raven.Queue[1]);
            Equal(false, ReferenceEquals(initial, Raven.Queue[2]));
            Equal(1, Chat.instance!.Cleared.Count); Equal<Raven.RavenText?>(null, raven.Current);
            Equal(true, player.HaveSeenTutorial("vanilla")); Equal(true, player.HaveSeenTutorial("other.mod"));
            Equal(1, Tutorial.instance!.m_texts.Count); control.Stop();
            Equal(2, Raven.Queue.Count); Equal(0, Tutorial.instance.m_texts.Count);
        });
        Test("Raven cleanup handles character/session switches and static queues surviving destruction", () =>
        {
            var control = RavenSetup(); var original = Player.m_localPlayer; var originalCatalog = Tutorial.instance!;
            var vanillaDefinition = new Tutorial.TutorialText { m_name = "vanilla" };
            originalCatalog.m_texts.Add(vanillaDefinition); control.Press(); original.SetSeenTutorial(RavenTestControls.Key);
            Player.m_localPlayer = new Player(); Player.m_localPlayer.SetSeenTutorial("new.character");
            Raven.Instances.Clear(); control.Update();
            Equal(0, Raven.Queue.Count); Equal(false, original.HaveSeenTutorial(RavenTestControls.Key));
            Equal(true, Player.m_localPlayer.HaveSeenTutorial("new.character"));
            Equal(vanillaDefinition, originalCatalog.m_texts.Single());
            control.Press(); Game.instance = new Game(); control.Update(); Equal(0, Raven.Queue.Count);
            control.Press(); Tutorial.instance = new Tutorial(); control.Update(); Equal(0, Raven.Queue.Count);
            control.Press(); var raven = Raven.Instances.Single(); raven.Current = Raven.Queue.Single();
            var unrelated = new Raven { Current = new Raven.RavenText { m_key = "other.mod" } }; Raven.Instances.Add(unrelated);
            Player.m_localPlayer.SetSeenTutorial(RavenTestControls.Key); control.Stop(); control.Stop();
            Equal(0, Raven.Queue.Count); Equal(false, Player.m_localPlayer.HaveSeenTutorial(RavenTestControls.Key));
            Equal<Raven.RavenText?>(null, raven.Current); Equal("other.mod", unrelated.Current!.m_key);
            Equal(1, Chat.instance!.Cleared.Count);
        });
        Test("Raven POC respects Tutorials and guards unavailable, dead and not ready objects", () =>
        {
            var control = RavenSetup(); var player = Player.m_localPlayer;
            void Blocked(Action block, Action restore)
            {
                block(); Equal(false, control.CanQueue); control.Press(); Equal(0, Raven.Queue.Count);
                Equal(true, control.Status.Contains("unavailable")); restore(); Equal(true, control.CanQueue);
            }
            Blocked(() => Raven.m_tutorialsEnabled = false, () => Raven.m_tutorialsEnabled = true);
            Blocked(() => Player.m_localPlayer = null!, () => Player.m_localPlayer = player);
            Blocked(() => player.Dead = true, () => player.Dead = false);
            Blocked(() => player.Intro = true, () => player.Intro = false);
            Blocked(() => player.Teleporting = true, () => player.Teleporting = false);
            Blocked(() => player.View.Owner = false, () => player.View.Owner = true);
            Blocked(() => player.View.Valid = false, () => player.View.Valid = true);
            Blocked(() => Tutorial.instance = null, () => Tutorial.instance = new Tutorial());
            Blocked(() => Tutorial.instance!.m_ravenPrefab = null, () => Tutorial.instance!.m_ravenPrefab = new GameObject());
            Blocked(() => Chat.instance = null, () => Chat.instance = new Chat());
            Blocked(() => ZoneSystem.instance = null, () => ZoneSystem.instance = new ZoneSystem());
            Blocked(() => Game.instance = null, () => Game.instance = new Game());
            control.Press(); Raven.m_tutorialsEnabled = false; control.Update(); Equal(0, Raven.Queue.Count);
            Equal(false, Raven.m_tutorialsEnabled); Raven.m_tutorialsEnabled = true;
            control.Press(); player.Dead = true; control.Update(); Equal(0, Raven.Queue.Count);
            player.Dead = false; control.Press(); control.Stop(); Equal(0, Raven.Queue.Count);
        });
    }
}
