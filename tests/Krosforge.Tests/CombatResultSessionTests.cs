using System.Net.Sockets;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpVictoryReturnsToWorldAndSocketLossBeforeAckPreservesOneReward(bool loseFinalAck)
    {
        var root = Path.Combine(Path.GetTempPath(), "combat-result-tcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var profile = profiles.Create("Alpha");
            var store = new CharacterStore(root); var id = CreateCharacter(store, profile.Id, "Hero");
            var catalog = TurnWorld(); var locations = new LocationStore(root);
            var characters = new CharacterService(store, SyntheticCatalog(), catalog.Experience);
            var encounter = ResultEncounter(catalog).Encounters.Single();
            var combat = new CombatCatalog(catalog, encounter with { Monster = encounter.Monster with { Experience = 50 } });
            var loot = new CombatLootCatalog(new()
            {
                [encounter.Monster.Definition] = [new(100, [50, 50, 50, 50, 50], 63, Krosforge.Core.DropCondition.Parse("PL<3"), false, [])]
            }, new() { [100] = new(100, [new(125, 11, 15, 0, false)]) });
            var rewards = new CombatRewardCalculator(new Krosforge.Core.GroupExperienceRules([1], 5, 10), new(2.5, 2_000_000_000),
                new("Local", 2.5m, 2m), loot, new(85, 150, 117, 1.5, 0.26, 2.5), dice: (_, maximum) => maximum);
            var service = new WorldEntryService(characters, locations, catalog,
                combat: combat, spells: ResultSpells(), outcomes: new(root), rewards: rewards);
            await using var host = new LocalSessionHost(characters: characters, worldEntry: service);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20)); var token = timeout.Token;
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var session = host.Register(profile, "pt", phase => { if (phase == "disconnected") disconnected.TrySetResult(); });
            using var login = new TcpClient(); await Login(host, login, session.IssueTicket(), token);
            using var world = await Enter(host, login, token, emptyRoster: false); await EnterMap(world, id, 100, token);
            await Send(world, "hms", WorldProtocol.Number(2, unchecked((ulong)-30001L)), token);
            foreach (var type in new[] { "kjz", "kjy", "kjp", "jxi", "jow" }) await Event(world, type, token);
            await Send(world, "kjt", WorldProtocol.Number(1, 100), token);
            foreach (var type in new[] { "jnj", "jnv", "jww", "jwj", "jwj", "jxk", "juf", "jxh" }) await Event(world, type, token);
            await Send(world, "jxj", [8, 1], token);
            foreach (var type in new[] { "jxr", "jvu", "krg", "jvi", "jvj" }) await Event(world, type, token);
            await Send(world, "jsr", Convert.FromHexString("0801108001"), token);
            foreach (var type in new[] { "jqh", "jsq", "jsq", "jsq", "jsq" }) await Event(world, type, token);
            var sequence = (await Event(world, "jsy", token)).UInt64(2); await Event(world, "krg", token);
            if (!loseFinalAck)
            {
                await Send(world, "jst", WorldProtocol.Join(WorldProtocol.Number(2, sequence), WorldProtocol.Number(3, 1)), token);
                var result = await Event(world, "jwp", token);
                var player = result.Repeated(4).Select(v => ProtoObject.Read(v.Bytes, 4096))
                    .Single(v => ProtoObject.Read(v.Bytes(2), 4096).UInt64(5) == (ulong)id);
                Assert.Equal(2UL, player.UInt64(1)); Assert.Equal(1UL, ProtoObject.Read(player.Bytes(3), 4096).UInt64(2));
                var itemPrize = ProtoObject.Read(ProtoObject.Read(ProtoObject.Read(player.Bytes(3), 4096).Bytes(1), 4096).Repeated(3).Single().Bytes, 4096);
                Assert.Equal(1UL, itemPrize.UInt64(1)); Assert.Equal(100UL, itemPrize.UInt64(3));
                var characterResult = ProtoObject.Read(ProtoObject.Read(player.Bytes(2), 4096).Bytes(3), 4096);
                Assert.Equal(2UL, characterResult.UInt64(2));
                var experience = ProtoObject.Read(ProtoObject.Read(characterResult.Repeated(1).Single().Bytes, 4096).Bytes(1), 4096);
                Assert.Equal(125UL, experience.UInt64(3)); Assert.Equal(125UL, experience.UInt64(8)); Assert.Equal(110UL, experience.UInt64(4));
                var summary = ProtoObject.Read((await Event(world, "jwh", token)).Bytes(2), 4096);
                Assert.Equal(15UL, summary.UInt64(4)); Assert.Equal(1UL, summary.UInt64(7));
                AssertCombatInventory(await Event(world, "irl", token));
                Assert.Equal(1UL, (await Event(world, "ith", token)).UInt64(2));
                Assert.Equal(1UL, ProtoObject.Read((await Event(world, "krg", token)).Bytes(1), 4096).UInt64(6));
                foreach (var type in new[] { "kjy", "kjp", "jow" }) await Event(world, type, token);
                await LoadedMap(world, 100, token);
                // A late duplicate acknowledgement cannot produce another result or payment.
                await Send(world, "jst", WorldProtocol.Join(WorldProtocol.Number(2, sequence), WorldProtocol.Number(3, 1)), token);
                await Send(world, "kne", WorldProtocol.Number(2, 1), token); await Event(world, "knb", token);
            }
            world.Client.LingerState = new LingerOption(true, 0); world.Dispose(); await disconnected.Task.WaitAsync(token);
            using var freshLogin = new TcpClient(); await Login(host, freshLogin, session.IssueTicket(), token);
            using var fresh = await Enter(host, freshLogin, token, emptyRoster: false);
            await Send(fresh, "kta", WorldProtocol.Number(1, (ulong)id), token); await Event(fresh, "ksb", token);
            await Send(fresh, "kkc", [], token); await Event(fresh, "kjp", token); await Event(fresh, "jow", token);
            var itemId = AssertCombatInventory(await Event(fresh, "irl", token));
            Assert.Equal(1UL, (await Event(fresh, "ith", token)).UInt64(2));
            var restoredStatistics = ProtoObject.Read((await Event(fresh, "krg", token)).Bytes(1), 4096);
            Assert.Equal(125UL, restoredStatistics.UInt64(11)); Assert.Equal(1UL, restoredStatistics.UInt64(6));
            await LoadedMap(fresh, 100, token);
            Assert.Equal(catalog.Initial, locations.LoadOrCreate(profile.Id, 290, id, catalog.Initial, catalog.Validate));
            Assert.Equal(1, new CombatOutcomeStore(root).Recover(profile.Id, 290, id));
            Assert.Equal(125, new CombatOutcomeStore(root).Read(profile.Id, 290, id).Experience);
            var persistedItem = Assert.Single(new CombatOutcomeStore(root).Read(profile.Id, 290, id).Items);
            Assert.Equal((ulong)persistedItem.Id, itemId); Assert.Equal(1, persistedItem.Quantity);
        }
        finally { Directory.Delete(root, true); }
    }
    private static ulong AssertCombatInventory(ProtoObject inventory)
    {
        Assert.Equal(1UL, inventory.UInt64(4));
        var item = ProtoObject.Read(inventory.Repeated(2).Single().Bytes, 4096); Assert.Equal(63UL, item.UInt64(4));
        var common = ProtoObject.Read(item.Bytes(1), 4096);
        Assert.Equal(1UL, common.UInt64(3)); Assert.Equal(100UL, common.UInt64(4));
        var effect = ProtoObject.Read(common.Repeated(7).Single().Bytes, 4096);
        Assert.Equal(125UL, effect.UInt64(10)); Assert.Equal(15UL, effect.UInt64(5));
        return common.UInt64(5);
    }
}
