using System;
using System.Collections.Generic;
using System.Linq;
using SharedInventory;

internal static partial class Program
{
    private static TutorialRavens TutorialSetup(Func<bool>? paused = null)
    {
        RavenSetup();
        return new TutorialRavens(error => throw new Exception("Unexpected production tutorial failure", error), paused);
    }
    private static void TutorialTests()
    {
        Test("Shutdown and unavailable native catalog cannot requeue guidance after cleanup", () =>
        {
            var controller = TutorialSetup(); var player = Player.m_localPlayer; controller.Update(0);
            Equal(1, Raven.Queue.Count); Game.instance!.ShuttingDown = true; controller.Stop();
            controller.Update(1); Equal(0, Raven.Queue.Count); Equal(false, TutorialRavens.PlayerReady(player));
            Game.instance.ShuttingDown = false; Tutorial.instance!.m_texts = null!;
            controller.Update(2); Equal(0, Raven.Queue.Count);
            Tutorial.instance.m_texts = new List<Tutorial.TutorialText>();
            controller.Update(3); Equal(1, Raven.Queue.Count); controller.Stop();
        });
        Test("Arrival queues on first ready frame, once per character, and acknowledgement keeps the readable journal dialog", () =>
        {
            var controller = TutorialSetup(); var player = Player.m_localPlayer;
            player.Intro = true; controller.Update(0); Equal(0, Raven.Queue.Count);
            player.Intro = false; controller.Update(1);
            var arrival = TutorialLessons.Get(TutorialLesson.Arrival);
            Equal(arrival.Key, Raven.Queue.Single().m_key); Equal(false, player.HaveSeenTutorial(arrival.Key));
            Equal(arrival.Label, Raven.Queue.Single().m_label);
            Raven.Instances.Clear(); // Native raven was recreated before it delivered the message.
            var raven = new Raven { Current = Raven.Queue.Single(), Away = false }; Raven.Instances.Add(raven);
            Chat.instance!.Visible.Add(raven.gameObject);
            player.SetSeenTutorial(arrival.Key); controller.Record(player, TutorialLesson.Chest); controller.Update(2);
            Equal(0, Chat.instance.Cleared.Count); Equal(arrival.Key, raven.Current!.m_key);
            controller.Update(99); Equal(arrival.Key, Raven.Queue.Single().m_key); // A long visible dialog isn't truncated.
            Chat.instance.Visible.Clear(); raven.Away = true; controller.Update(100);
            Equal(0, Raven.Queue.Count); Equal(true, player.HaveSeenTutorial(arrival.Key));
            controller.Update(104); Equal(0, Raven.Queue.Count);
            controller.Update(105); Equal(TutorialLessons.Get(TutorialLesson.Chest).Key, Raven.Queue.Single().m_key);
            controller.Stop(); controller.Update(106);
            Equal(TutorialLessons.Get(TutorialLesson.Chest).Key, Raven.Queue.Single().m_key); // Arrival stays acknowledged.
            controller.Stop();
        });
        Test("Every independent event persists pending through native-style character snapshots, death, logout and world changes", () =>
        {
            var controller = TutorialSetup(); var player = Player.m_localPlayer;
            player.SetSeenTutorial(TutorialLessons.Get(TutorialLesson.Arrival).Key);
            foreach (var lesson in new[] { TutorialLesson.Essence, TutorialLesson.Chest, TutorialLesson.Deposit,
                TutorialLesson.Station, TutorialLesson.Coverage, TutorialLesson.Wrath }) controller.Record(player, lesson);
            player.Dead = true; controller.Update(0); Equal(0, Raven.Queue.Count);
            var snapshot = new Dictionary<string, string>(player.m_customData);
            controller.Stop(); Player.m_localPlayer = null!; controller.Update(1); Equal(0, Raven.Queue.Count);
            var respawned = new Player { m_customData = snapshot }; respawned.SetSeenTutorial(TutorialLessons.Get(TutorialLesson.Arrival).Key);
            Player.m_localPlayer = respawned; Game.instance = new Game(); controller.Update(2);
            Equal(TutorialLessons.Get(TutorialLesson.Wrath).Key, Raven.Queue.Single().m_key);
            Equal(false, respawned.HaveSeenTutorial(TutorialLessons.Get(TutorialLesson.Wrath).Key));
            controller.Stop();
            Equal(7, respawned.m_customData.Count); // Six event flags plus preferred key; queueing cannot consume them.
            controller.Update(3); Equal(TutorialLessons.Get(TutorialLesson.Wrath).Key, Raven.Queue.Single().m_key);
            controller.Stop();
        });
        Test("Production cleanup preserves other character histories, mod queues, catalog definitions and visible dialogs", () =>
        {
            var controller = TutorialSetup(); var original = Player.m_localPlayer;
            original.m_customData["other.mod"] = "keep"; original.SetSeenTutorial("vanilla");
            var vanilla = new Raven.RavenText { m_key = "vanilla" }; Raven.Queue.Add(vanilla);
            var nativeDefinition = new Tutorial.TutorialText { m_name = "vanilla" }; Tutorial.instance!.m_texts.Add(nativeDefinition);
            controller.Update(0);
            var unrelatedRaven = new Raven { Current = vanilla }; Raven.Instances.Add(unrelatedRaven);
            Chat.instance!.Visible.Add(unrelatedRaven.gameObject);
            Player.m_localPlayer = new Player(); controller.Update(1);
            Equal(true, original.HaveSeenTutorial("vanilla")); Equal("keep", original.m_customData["other.mod"]);
            Equal(true, original.m_customData.ContainsKey(TutorialLessons.PendingKey(TutorialLessons.Get(TutorialLesson.Arrival))));
            Equal(vanilla, Raven.Queue[0]); Equal(2, Raven.Queue.Count);
            Equal(true, Chat.instance.IsDialogVisible(unrelatedRaven.gameObject));
            Equal(nativeDefinition, Tutorial.instance.m_texts[0]);
            controller.Stop(); Equal(vanilla, Raven.Queue.Single()); Equal(nativeDefinition, Tutorial.instance.m_texts.Single());
        });
        Test("Tutorials off and testing suspension retain real pending state without changing seen keys or duplicating messages", () =>
        {
            var paused = false; var controller = TutorialSetup(() => paused); var player = Player.m_localPlayer;
            Raven.m_tutorialsEnabled = false; controller.Update(0); controller.Record(player, TutorialLesson.Essence);
            Equal(0, Raven.Queue.Count); Equal(false, player.HaveSeenTutorial(TutorialLessons.Get(TutorialLesson.Arrival).Key));
            Raven.m_tutorialsEnabled = true; controller.Update(1); controller.Update(2); Equal(1, Raven.Queue.Count);
            paused = true; controller.Update(3); Equal(0, Raven.Queue.Count); Equal(3, player.m_customData.Count);
            var poc = new RavenTestControls(error => throw error); poc.Press(); controller.Update(4);
            Equal(RavenTestControls.Key, Raven.Queue.Single().m_key); poc.Stop();
            Equal(false, player.HaveSeenTutorial(TutorialLessons.Get(TutorialLesson.Arrival).Key));
            paused = false; controller.Update(5); Equal(TutorialLessons.Get(TutorialLesson.Essence).Key, Raven.Queue.Single().m_key);
            player.Teleporting = true; controller.Update(6); Equal(0, Raven.Queue.Count);
            player.Teleporting = false; controller.Update(7); Equal(1, Raven.Queue.Count); controller.Stop();
        });
        Test("Acknowledged character flags are pruned with a bounded owned namespace and no lesson prerequisite", () =>
        {
            var data = new Dictionary<string, string> { ["other.mod"] = "keep" };
            var seen = new HashSet<string>();
            for (var repeat = 0; repeat < 100; ++repeat)
                TutorialLessons.Record(data, seen.Contains, TutorialLesson.Station);
            Equal(3, data.Count); Equal(TutorialLesson.Station, TutorialLessons.Next(data, seen.Contains)!.Lesson);
            seen.Add(TutorialLessons.Get(TutorialLesson.Station).Key);
            Equal<TutorialLessonText?>(null, TutorialLessons.Next(data, seen.Contains)); Equal(1, data.Count);
            Equal("keep", data["other.mod"]);
        });
        Test("Newly relevant lessons supersede an unread introduction without consuming it or cutting visible dialog", () =>
        {
            var controller = TutorialSetup(); var player = Player.m_localPlayer; controller.Update(0);
            var arrival = TutorialLessons.Get(TutorialLesson.Arrival); var chest = TutorialLessons.Get(TutorialLesson.Chest);
            var raven = Raven.Instances.Single(); raven.Current = Raven.Queue.Single(); Chat.instance!.Visible.Add(raven.gameObject);
            controller.Record(player, TutorialLesson.Chest); controller.Update(1);
            Equal(arrival.Key, Raven.Queue.Single().m_key); Equal(0, Chat.instance.Cleared.Count);
            Chat.instance.Visible.Clear(); controller.Update(2);
            Equal(chest.Key, Raven.Queue.Single().m_key); Equal(false, player.HaveSeenTutorial(arrival.Key));
            Equal(true, player.m_customData.ContainsKey(arrival.PendingKey));
            controller.Record(player, TutorialLesson.Deposit); controller.Update(3);
            Equal(TutorialLessons.Get(TutorialLesson.Deposit).Key, Raven.Queue.Single().m_key);
            controller.Record(player, TutorialLesson.Chest); controller.Update(4); // Repeated event doesn't steal focus back.
            Equal(TutorialLessons.Get(TutorialLesson.Deposit).Key, Raven.Queue.Single().m_key);
            controller.Stop(); controller.Update(5);
            Equal(TutorialLessons.Get(TutorialLesson.Deposit).Key, Raven.Queue.Single().m_key);
            controller.Stop();
        });
        Test("Wrath is recorded for every actual local punishment death and never for ordinary sacrifice, survival or bystanders", () =>
        {
            var controller = TutorialSetup(); var first = Player.m_localPlayer;
            controller.RitualDeath(first, true); Equal(0, first.m_customData.Count); // Survived.
            first.Dead = true; controller.RitualDeath(first, false); Equal(0, first.m_customData.Count); // Sacrifice.
            var bystander = new Player { Dead = true }; controller.RitualDeath(bystander, true); Equal(0, bystander.m_customData.Count);
            controller.RitualDeath(first, true);
            Equal(true, first.m_customData.ContainsKey(TutorialLessons.Get(TutorialLesson.Wrath).PendingKey));
            controller.Update(0); Equal(0, Raven.Queue.Count);
            var second = new Player { Dead = true }; Player.m_localPlayer = second; controller.RitualDeath(second, true);
            Equal(true, second.m_customData.ContainsKey(TutorialLessons.Get(TutorialLesson.Wrath).PendingKey));
            second.Dead = false; controller.Update(1);
            Equal(TutorialLessons.Get(TutorialLesson.Wrath).Key, Raven.Queue.Single().m_key);
            Equal(false, second.HaveSeenTutorial(TutorialLessons.Get(TutorialLesson.Wrath).Key));
            controller.Stop();
        });
        Test("Deposit tutorial events exclude rejects, zero successes and internal unused-withdrawal returns", () =>
        {
            var (client, api) = Setup("Wood", 10); var player = Player.m_localPlayer; var events = 0; Inventory? recorded = null;
            client.Deposited += inventory => { events++; recorded = inventory; };
            var carried = Items.Default("Wood")!; carried.m_stack = 2; player.Inventory.AddItem(carried);
            var stack = player.Inventory.GetAllItems().Single(); client.Deposit(stack);
            client.OnDeposit(new TransferReply { RequestId = api.Calls.Last().Id, ItemId = "Wood", Amount = 2, Total = 12 });
            Equal(1, events); Equal(player.Inventory, recorded);
            client.Withdrawal("Wood", false); player.Dead = true;
            client.OnWithdrawal(new TransferReply { RequestId = api.Calls.Last().Id, ItemId = "Wood", Amount = 1, Total = 11 });
            var returnCall = api.Calls.Last(); Equal("Deposit", returnCall.Method);
            client.OnDeposit(new TransferReply { RequestId = returnCall.Id, ItemId = "Wood", Amount = 1, Total = 12 });
            Equal(1, events); player.Dead = false;
            carried = Items.Default("Wood")!; carried.m_stack = 1; player.Inventory.AddItem(carried);
            client.Deposit(player.Inventory.GetAllItems().Single());
            client.OnDeposit(new TransferReply { RequestId = api.Calls.Last().Id, ItemId = "Wood", Amount = 0, Total = 12 });
            Equal(1, events);
            carried = Items.Default("Wood")!; carried.m_stack = 1; player.Inventory.AddItem(carried);
            client.Deposit(player.Inventory.GetAllItems().Single());
            client.OnDeposit(new TransferReply { RequestId = api.Calls.Last().Id, ItemId = "Wood", Error = "rejected" });
            Equal(1, events);
        });
        Test("Coverage event requires an attempted denied transfer, while station guidance requires usable shared input", () =>
        {
            var covered = false; Prefab("Wood"); Prefab("FineWood"); var api = new RpcApi();
            var client = new Client(api, () => 0, () => covered); client.Counts["Wood"] = 2; client.Counts["FineWood"] = 2;
            var denied = 0; client.AccessDenied += () => denied++;
            var inputs = new StationInventory(client); var context = new StationInputContext(new object(), "charcoal_kiln", StationInputKind.Processing);
            Equal(0L, client.Available("Wood")); Equal(0, denied);
            Equal(false, inputs.HasSharedInput(context, new[] { "Wood" })); Equal(0, denied);
            client.Withdrawal("Wood", false); Equal(1, denied); Equal(ChestAccess.Unavailable, client.Status);
            covered = true; Equal(true, inputs.HasSharedInput(context, new[] { "Wood" }));
            Equal(false, inputs.HasSharedInput(context, new[] { "FineWood" }));
            Equal(false, inputs.HasSharedInput(context, new[] { "Coal" }));
            Game.m_worldLevel = 1; Equal(false, inputs.HasSharedInput(context, new[] { "Wood" })); Game.m_worldLevel = 0;
        });
    }
}
