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
            var service = new WorldEntryService(new(store, SyntheticCatalog()), locations, catalog,
                combat: ResultEncounter(catalog), spells: ResultSpells(), outcomes: new(root));
            await using var host = new LocalSessionHost(characters: new(store, SyntheticCatalog()), worldEntry: service);
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
                var summary = ProtoObject.Read((await Event(world, "jwh", token)).Bytes(2), 4096);
                Assert.Equal(15UL, summary.UInt64(4)); Assert.Equal(1UL, summary.UInt64(7));
                Assert.Equal(1UL, (await Event(world, "irl", token)).UInt64(4));
                foreach (var type in new[] { "krg", "kjy", "kjp", "jow" }) await Event(world, type, token);
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
            Assert.Equal(1UL, (await Event(fresh, "irl", token)).UInt64(4)); await Event(fresh, "krg", token);
            await LoadedMap(fresh, 100, token);
            Assert.Equal(catalog.Initial, locations.LoadOrCreate(profile.Id, 290, id, catalog.Initial, catalog.Validate));
            Assert.Equal(1, new CombatOutcomeStore(root).Recover(profile.Id, 290, id));
        }
        finally { Directory.Delete(root, true); }
    }
}
