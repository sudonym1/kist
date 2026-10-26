using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SharedInventory;

// Owned by the runtime-guarded TestPanel; excluded entirely from production.
// No patches, RPCs, automatic trigger, or changes to the global Tutorials flag.
internal sealed class RavenTestControls
{
    internal const string Key = "local.sharedinventory.testing.raven-poc.v1";
    private const string Topic = "Einherjar Chest";
    private const string Text = "Gather your companions at the sacrificial stones and begin the Einherjar ritual. " +
        "Oden may grant an Einherjar Essence. Carry it home to build an Einherjar Chest. " +
        "Every chest shares your world's stock. Stay within 100 metres of a loaded chest to use that stock. " +
        "Press F3 to browse it anywhere.";
    // Valheim exposes adding/acknowledging messages, but no scoped removal API.
    // Validate these private contracts before admitting the test action.
    private static readonly FieldInfo? TempTexts = Field(typeof(Raven), "m_tempTexts", typeof(List<Raven.RavenText>), true);
    private static readonly FieldInfo? CurrentText = Field(typeof(Raven), "m_currentText", typeof(Raven.RavenText), false);
    private static readonly FieldInfo? SeenTutorials = Field(typeof(Player), "m_shownTutorials", typeof(HashSet<string>), false);
    private readonly Action<Exception> reportError;
    private Player? owner;
    private Game? game;
    private Tutorial? catalog;
    private Tutorial.TutorialText? definition;
    private HashSet<string>? seen;
    private bool acknowledged;
    private string result = "No sample queued. Tutorials setting is respected.";
    private string failure = "";
    private Raven[] ravens = Array.Empty<Raven>();

    public bool Active
    {
        get
        {
            if (owner == null) return false;
            if (!acknowledged) return true;
            foreach (var raven in ravens)
                if (raven != null && (CurrentText?.GetValue(raven) as Raven.RavenText)?.m_key == Key &&
                    Chat.instance != null && Chat.instance.IsDialogVisible(raven.gameObject)) return true;
            return false;
        }
    }

    public RavenTestControls(Action<Exception> reportError) => this.reportError = reportError;

    private static FieldInfo? Field(Type type, string name, Type expected, bool isStatic)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance));
        return field?.FieldType == expected ? field : null;
    }

    private string? Unavailable
    {
        get
        {
            if (failure.Length != 0) return failure;
            if (TempTexts == null || CurrentText == null || SeenTutorials == null || TempTexts.GetValue(null) == null)
                return "This Valheim version's scoped raven cleanup is unavailable.";
            if (!Raven.m_tutorialsEnabled) return "Enable Tutorials in Valheim's settings to test Hugin.";
            var player = Player.m_localPlayer;
            if (player == null) return "A local test player is required.";
            if (player.IsDead()) return "The test player must be alive.";
            var view = player.GetComponent<ZNetView>();
            if (player.InIntro() || player.IsTeleporting() || view == null || !view.IsValid() || !view.IsOwner())
                return "Wait for the local test player to finish spawning or teleporting.";
            if (Game.instance == null || Chat.instance == null || ZoneSystem.instance == null || Tutorial.instance == null ||
                Tutorial.instance.m_ravenPrefab == null || Tutorial.instance.m_texts == null)
                return "Native tutorial, raven, or world objects are not ready.";
            return null;
        }
    }

    public bool CanQueue => Unavailable == null;
    public string Status => Unavailable is string reason ? "Hugin unavailable: " + reason : result;

    public void Press()
    {
        Update();
        if (Unavailable != null) return;
        if (owner != null && !acknowledged) return; // Repeated clicks keep one native queue entry.
        try
        {
            Clear();
            owner = Player.m_localPlayer;
            game = Game.instance;
            catalog = Tutorial.instance ?? throw new InvalidOperationException("Native tutorial object disappeared.");
            seen = SeenTutorials!.GetValue(owner) as HashSet<string>
                ?? throw new InvalidOperationException("The test player's tutorial state is unavailable.");
            seen.Remove(Key); // Replay only our own fixed key, never ResetSeenTutorials.
            definition = new Tutorial.TutorialText { m_name = Key, m_topic = Topic, m_text = Text,
                m_label = "", m_isMunin = false, m_globalKeyTrigger = "", m_tutorialTrigger = "" };
            catalog.m_texts.Add(definition);
            // This native path instantiates Hugin if missing, then adds a tutorial temp text.
            // An empty label avoids adding a persistent Known Text / journal entry.
            owner.ShowTutorial(Key);
            ravens = UnityEngine.Object.FindObjectsByType<Raven>(FindObjectsSortMode.None);
            result = "Hugin sample queued. Close F3 and look for Hugin; native landing and other messages may delay him. " +
                "Interact to acknowledge. Clicking again while queued adds no duplicates.";
        }
        catch (Exception error)
        {
            Clear();
            failure = "The sample failed; see the BepInEx log. Restart the test session before retrying.";
            reportError(error);
        }
    }

    public void Update()
    {
        if (ReferenceEquals(owner, null)) return;
        if (owner == null || !ReferenceEquals(owner, Player.m_localPlayer) || !ReferenceEquals(game, Game.instance) ||
            !ReferenceEquals(catalog, Tutorial.instance) || Unavailable != null)
        {
            Clear();
            result = "Sample cleared after player, session, or tutorial availability changed. Queue it again when ready.";
            return;
        }
        if (!acknowledged && owner.HaveSeenTutorial(Key))
        {
            acknowledged = true;
            ravens = UnityEngine.Object.FindObjectsByType<Raven>(FindObjectsSortMode.None);
            result = "Hugin sample acknowledged. Queue again to replay only this POC.";
        }
    }

    public void Stop()
    {
        Clear();
        failure = "";
        result = "No sample queued. Tutorials setting is respected.";
    }

    private void Clear()
    {
        // m_tempTexts survives native Raven.OnDestroy. Remove by our exact key,
        // preserving every vanilla/other-mod entry and its order.
        (TempTexts?.GetValue(null) as List<Raven.RavenText>)?.RemoveAll(text => text.m_key == Key);
        if (CurrentText != null)
        {
            foreach (var raven in UnityEngine.Object.FindObjectsByType<Raven>(FindObjectsSortMode.None))
            {
                if ((CurrentText.GetValue(raven) as Raven.RavenText)?.m_key != Key) continue;
                if (Chat.instance != null) Chat.instance.ClearNpcText(raven.gameObject);
                CurrentText.SetValue(raven, null); // Native Update handles departure/animation.
            }
        }
        if (catalog != null && definition != null) catalog.m_texts.Remove(definition);
        seen?.Remove(Key); // Captured collection still belongs to the original character on a switch.
        owner = null; game = null; catalog = null; definition = null; seen = null; acknowledged = false;
        ravens = Array.Empty<Raven>();
    }
}
