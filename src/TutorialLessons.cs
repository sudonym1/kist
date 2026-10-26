using System;
using System.Collections.Generic;

namespace SharedInventory;

internal enum TutorialLesson { Arrival, Essence, Chest, Deposit, Station, Coverage, Wrath }

internal sealed class TutorialLessonText
{
    public readonly TutorialLesson Lesson;
    public readonly string Key, Topic, Text, PendingKey;
    public string Label => "Einherjar Chest: " + Topic;
    public TutorialLessonText(TutorialLesson lesson, string name, string topic, string text)
    { Lesson = lesson; Key = "local.sharedinventory.tutorial." + name + ".v1"; PendingKey = Key + ".pending"; Topic = topic; Text = text; }
}

// Permanent native seen keys, distinct from Testing's deliberately replayable POC.
// Pending flags live in Player.m_customData and follow the character across worlds.
internal static class TutorialLessons
{
    private const string Preferred = "local.sharedinventory.tutorial.preferred.v1";
    internal static readonly TutorialLessonText[] All =
    {
        new TutorialLessonText(TutorialLesson.Wrath, "wrath", "Odin's wrath",
            "The thunder that felled you was Odin’s judgment. Your gathering sought another gift while his anger still burned, " +
            "and every life within the circle paid the price. No essence follows such a sacrifice. Leave these stones in peace " +
            "for a long while. Let many deeds fill your saga before you seek his favor again."),
        new TutorialLessonText(TutorialLesson.Arrival, "arrival", "Odin's bargain",
            "Go to the sacrificial stones where you first arrived in this world. There, Odin offers a path to a shared hoard. " +
            "Gather every companion within the circle, then use a boss stone’s Begin Einherjar ritual action. " +
            "A life is the price of his favor. Read the warning before you call upon him."),
        new TutorialLessonText(TutorialLesson.Essence, "essence", "A gift dearly earned",
            "An Einherjar Essence can become an Einherjar Chest with your hammer. No other materials or workbench are needed. " +
            "Dismantling the chest with your hammer or destroying it by damage returns a Dimmed Essence. " +
            "It can rebuild within 100 metres of the original site in this same world; moving it again never extends that reach. " +
            "Its tooltip shows the bound site."),
        new TutorialLessonText(TutorialLesson.Chest, "chest", "One hoard, many doors",
            "Every Einherjar Chest in this world opens the same hoard. Alt-click a carried stack to deposit it; choose an amount " +
            "here to withdraw. You may browse anywhere with F3, but using the hoard requires a nearby chest."),
        new TutorialLessonText(TutorialLesson.Deposit, "deposit", "The hoard lends a hand",
            "Your shared supplies can serve your hammer, your crafting, and your work around the hearth. While a chest is within " +
            "reach, missing materials are drawn from the hoard. What you carry is used first."),
        new TutorialLessonText(TutorialLesson.Station, "station", "Feed the fires",
            "Your shared hoard can feed this station too. Use it normally, or follow the Alt-number shortcuts shown when you " +
            "look at its controls to choose a particular supply."),
        new TutorialLessonText(TutorialLesson.Coverage, "coverage", "Beyond the chest's reach",
            "The hoard is beyond your reach. Stand within 100 metres of an Einherjar Chest that is loaded around you to draw " +
            "upon it. You can still inspect your supplies with F3.")
    };

    internal static TutorialLessonText Get(TutorialLesson lesson)
    {
        foreach (var text in All) if (text.Lesson == lesson) return text;
        throw new ArgumentOutOfRangeException(nameof(lesson));
    }
    internal static string PendingKey(TutorialLessonText text) => text.PendingKey;
    internal static bool Record(IDictionary<string, string> data, Func<string, bool> seen, TutorialLesson lesson)
    {
        var text = Get(lesson);
        if (seen(text.Key)) { data.Remove(PendingKey(text)); return false; }
        if (data.ContainsKey(PendingKey(text))) return false; // Repeated hover/events don't reorder waiting lessons.
        data[PendingKey(text)] = "1";
        data[Preferred] = text.Key;
        return true;
    }
    internal static TutorialLessonText? Next(IDictionary<string, string> data, Func<string, bool> seen)
    {
        TutorialLessonText? next = null, preferred = null, wrath = null, arrival = null;
        data.TryGetValue(Preferred, out var preferredKey);
        foreach (var text in All)
        {
            if (seen(text.Key)) data.Remove(PendingKey(text));
            else if (data.ContainsKey(PendingKey(text)))
            {
                if (text.Key == preferredKey) preferred = text;
                if (text.Lesson == TutorialLesson.Wrath) wrath = text;
                else if (text.Lesson == TutorialLesson.Arrival) arrival = text;
                else if (next == null) next = text;
            }
        }
        if (preferred == null) data.Remove(Preferred);
        // New relevant events can supersede unread older hints. The introduction
        // remains available after event lessons, without being falsely marked seen.
        return wrath ?? preferred ?? next ?? arrival;
    }
}
