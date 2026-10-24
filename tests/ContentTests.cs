using System.Collections.Generic;
using SharedInventory;

internal static partial class Program
{
    private static void ContentTests()
    {
        Test("Legacy shared essence remains recorded but cannot be transferred or spent", () =>
        {
            var (client, api) = Setup(ContentIds.Essence, 50);
            var essence = Items.Default(ContentIds.Essence)!;
            Player.m_localPlayer.GetInventory().AddItem(essence);
            essence = Player.m_localPlayer.GetInventory().GetAllItems()[0];
            client.Deposit(essence);
            Equal(true, Player.m_localPlayer.GetInventory().ContainsItem(essence));
            client.Withdrawal(ContentIds.Essence, true);
            client.Consume(new Dictionary<string, long> { [ContentIds.Essence] = 1 });
            Equal(0, api.Calls.Count);
            Equal(0L, client.Available(ContentIds.Essence));
            Equal(50L, client.Counts[ContentIds.Essence]);
        });
        Test("One carried essence belongs only to its owner's build requirements", () =>
        {
            var (client, api) = Setup(ContentIds.Essence, 100);
            var resources = new ResourceInventory(client);
            var inventory = Player.m_localPlayer.GetInventory();
            Equal(0, resources.Count(inventory, ContentIds.Essence, -1, false));
            inventory.AddItem(Items.Default(ContentIds.Essence)!);
            Equal(1, resources.Count(inventory, ContentIds.Essence, -1, false));
            using (resources.Begin()) resources.Remove(inventory, ContentIds.Essence, 1, -1, false);
            Equal(0, resources.Count(inventory, ContentIds.Essence, -1, false));
            Equal(0, api.Calls.Count);
            Equal(100L, client.Counts[ContentIds.Essence]);
            // A second client cannot use another character's carried material.
            var other = new Client(new RpcApi());
            other.Counts[ContentIds.Essence] = 100;
            Equal(0L, other.Available(ContentIds.Essence));
        });
        Test("Shared station selectors never offer legacy essence stock", () =>
        {
            var (client, api) = Setup(ContentIds.Essence, 10);
            var stations = new StationInventory(client);
            var context = new StationInputContext(new object(), "test", StationInputKind.Processing);
            var inventory = Player.m_localPlayer.GetInventory();
            Equal(0L, stations.Count(inventory, context, ContentIds.Essence));
            Equal<ItemDrop.ItemData?>(null, stations.Select(inventory, context, new[] { ContentIds.Essence }));
            Equal(0, api.Calls.Count);
        });
    }
}
