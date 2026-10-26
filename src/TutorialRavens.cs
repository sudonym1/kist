using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SharedInventory;

// Local production guidance. No separate assets, RPC endpoints, timer display,
// or resets of native tutorial history. Native Talk owns acknowledgement/journal.
internal sealed class TutorialRavens
{
    private static readonly FieldInfo? tempTexts = Field("m_tempTexts", typeof(List<Raven.RavenText>), true);
    private static readonly FieldInfo? currentText = Field("m_currentText", typeof(Raven.RavenText), false);
    private readonly Action<Exception> report;
    private readonly Func<bool> paused;
    private Player? owner;
    private Game? game;
    private Tutorial? catalog;
    private Tutorial.TutorialText? definition;
    private TutorialLessonText? active;
    private Raven[] ravens = Array.Empty<Raven>();
    private double nextDelivery;
    private bool acknowledged, failed;
    private string replacement = "";

    public TutorialRavens(Action<Exception> report, Func<bool>? paused = null)
    { this.report = report; this.paused = paused ?? (() => false); }
    private static FieldInfo? Field(string name, Type type, bool isStatic)
    {
        var field = typeof(Raven).GetField(name, BindingFlags.NonPublic | BindingFlags.Public |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance));
        return field?.FieldType == type ? field : null;
    }
    internal static bool PlayerReady(Player? player)
    {
        if (player == null || player.IsDead() || player.InIntro() || player.IsTeleporting() ||
            Game.instance == null || Game.instance.IsShuttingDown()) return false;
        var view = player.GetComponent<ZNetView>();
        return view != null && view.IsValid() && view.IsOwner();
    }
    public void Record(Player player, TutorialLesson lesson)
    {
        // Events can arrive while dead (wrath, a deposit reply). They must be
        // written before native respawn snapshots/recreates the Player object.
        if (player != null && player == Player.m_localPlayer)
            TutorialLessons.Record(player.m_customData, player.HaveSeenTutorial, lesson);
    }
    public void RitualDeath(Player player, bool punishment)
    {
        if (punishment && player.IsDead()) Record(player, TutorialLesson.Wrath);
    }
    public void Update(double now)
    {
        var player = Player.m_localPlayer;
        if (!ReferenceEquals(owner, player) || !ReferenceEquals(game, Game.instance))
        {
            ClearActive(); owner = player; game = Game.instance; failed = false; nextDelivery = 0;
        }
        if (!PlayerReady(player)) { ClearActive(); return; }
        Record(player!, TutorialLesson.Arrival);
        var next = TutorialLessons.Next(player!.m_customData, player.HaveSeenTutorial);
        if (failed || paused() || !Raven.m_tutorialsEnabled || Tutorial.instance == null ||
            Tutorial.instance.m_ravenPrefab == null || Tutorial.instance.m_texts == null || Chat.instance == null || ZoneSystem.instance == null)
        { ClearActive(); return; }
        if (active != null && !ReferenceEquals(catalog, Tutorial.instance)) ClearActive();
        if (active != null && !player.HaveSeenTutorial(active.Key))
        {
            if (next == null || ReferenceEquals(active, next)) return;
            if (replacement != next.Key)
            { replacement = next.Key; ravens = UnityEngine.Object.FindObjectsByType<Raven>(FindObjectsSortMode.None); }
            foreach (var raven in ravens)
                if (raven != null && (currentText!.GetValue(raven) as Raven.RavenText)?.m_key == active.Key &&
                    Chat.instance.IsDialogVisible(raven.gameObject)) return;
            ClearActive(); // Deferred pending flag survives; only the one native queue entry changes.
        }
        if (active != null)
        {
            if (!acknowledged)
            {
                acknowledged = true; nextDelivery = now + 5;
                // A native raven can be destroyed/recreated while the lesson waits.
                // Protect the instance which actually delivered this acknowledgement.
                ravens = UnityEngine.Object.FindObjectsByType<Raven>(FindObjectsSortMode.None);
            }
            // Seen is set at Talk, before the player finishes reading. Keep the
            // native dialog intact, and don't wait for m_currentText == null:
            // native Raven retains that reference even after it flies away.
            foreach (var raven in ravens)
                if (raven != null && (currentText!.GetValue(raven) as Raven.RavenText)?.m_key == active.Key &&
                    (Chat.instance.IsDialogVisible(raven.gameObject) || (!raven.IsAway() && !raven.IsFlying()))) return;
            ClearActive(); nextDelivery = Math.Max(nextDelivery, now + 5);
            return;
        }
        if (next == null || now < nextDelivery) return;
        try
        {
            if (tempTexts == null || currentText == null || tempTexts.GetValue(null) == null)
                throw new InvalidOperationException("Scoped native raven cleanup is unavailable for this Valheim version.");
            active = next; catalog = Tutorial.instance;
            definition = new Tutorial.TutorialText { m_name = next.Key, m_topic = next.Topic, m_text = next.Text,
                m_label = next.Label, m_isMunin = false, m_globalKeyTrigger = "", m_tutorialTrigger = "" };
            catalog.m_texts.Add(definition);
            player.ShowTutorial(next.Key); // Also instantiates native Hugin when missing.
            ravens = UnityEngine.Object.FindObjectsByType<Raven>(FindObjectsSortMode.None);
        }
        catch (Exception error)
        {
            ClearActive(); failed = true; report(error); // Pending character flags remain available next session.
        }
    }
    // Used only by the guarded Testing panel before its POC takes the raven.
    // Production lesson state remains pending and is never reset by testing.
    public void Pause() => ClearActive();
    public void Stop()
    { ClearActive(); owner = null; game = null; failed = false; nextDelivery = 0; }
    private void ClearActive()
    {
        if (active == null) return;
        (tempTexts?.GetValue(null) as List<Raven.RavenText>)?.RemoveAll(text => text.m_key == active.Key);
        if (currentText != null)
            foreach (var raven in UnityEngine.Object.FindObjectsByType<Raven>(FindObjectsSortMode.None))
            {
                if ((currentText.GetValue(raven) as Raven.RavenText)?.m_key != active.Key) continue;
                if (Chat.instance != null) Chat.instance.ClearNpcText(raven.gameObject);
                currentText.SetValue(raven, null);
            }
        if (catalog != null && definition != null) catalog.m_texts?.Remove(definition);
        active = null; catalog = null; definition = null; ravens = Array.Empty<Raven>(); acknowledged = false; replacement = "";
    }
}
