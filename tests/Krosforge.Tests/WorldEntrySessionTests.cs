using System.Net.Sockets;
using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    private static WorldEntryCatalog EntryCatalog() => new(new WorldLocation(100, 10, 3, 1), new EntryMap(100, 25, new HashSet<int> { 10, 11 }));
    private static long CreateCharacter(CharacterStore store, Guid profile, string name)
    {
        Assert.Equal(CharacterCreationResult.Success, store.Create(profile, 290, name,
            new(1, ClientInstallation.SupportedBuild, 1, 0, 1, 1, [1, 2, 3, 4, 5, 6]), out var id));
        return id;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryPublishesOwnedCharacterAndRecoversAfterSocketLoss(bool loseDuringLoading)
    {
        var root = Path.Combine(Path.GetTempPath(), "entry-tcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var a = profiles.Create("Alpha"); var b = profiles.Create("Beta");
            var store = new CharacterStore(root); var characters = new CharacterService(store, SyntheticCatalog());
            var service = new WorldEntryService(characters, new LocationStore(root), EntryCatalog());
            await using var host = new LocalSessionHost(characters: characters, worldEntry: service);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var token = timeout.Token;
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var session = host.Register(a, "pt", phase => { if (phase == "disconnected") disconnected.TrySetResult(); });
            using var other = host.Register(b, "pt", _ => { });
            using var login = new TcpClient(); using var otherLogin = new TcpClient();
            await Login(host, login, session.IssueTicket(), token); await Login(host, otherLogin, other.IssueTicket(), token);
            using var world = await Enter(host, login, token); using var otherWorld = await Enter(host, otherLogin, token);
            var id = CreateCharacter(store, a.Id, "Hero"); var foreign = CreateCharacter(store, b.Id, "Other");
            var deleted = CreateCharacter(store, a.Id, "Deleted"); Assert.True(store.Delete(a.Id, 290, deleted));
            foreach (var invalid in new[] { foreign, deleted })
            {
                await Send(world, "kta", WorldProtocol.Number(1, (ulong)invalid), token);
                Assert.True((await Event(world, "ksb", token)).Has(2));
            }
            await Send(world, "kta", WorldProtocol.Number(1, (ulong)id), token);
            var success = ProtoObject.Read((await Event(world, "ksb", token)).Bytes(1), 128);
            var selected = ProtoObject.Read(success.Bytes(1), 128);
            Assert.Equal((ulong)id, selected.UInt64(3));
            Assert.Equal("Hero", ProtoObject.Read(selected.Bytes(4), 128).Text(6));
            // A different connection cannot acquire the same character while it is selected.
            Assert.Null(service.Select(a.Id, a.Slot, new object(), id));
            await Send(world, "kkc", [], token);
            Assert.Equal(0UL, (await Event(world, "kjp", token)).UInt64(1));
            Assert.Equal(100UL, (await Event(world, "jow", token)).UInt64(1));
            var inventory = await Event(world, "irl", token); Assert.Equal(0UL, inventory.UInt64(4)); Assert.True(inventory.Has(3));
            var statistics = ProtoObject.Read((await Event(world, "krg", token)).Bytes(1), 128);
            Assert.Equal(110UL, statistics.UInt64(2));
            var entries = statistics.Repeated(1).Select(f => ProtoObject.Read(f.Bytes, 128)).ToDictionary(e => e.UInt64(2));
            Assert.Equal(100UL, ProtoObject.Read(entries[95].Bytes(3), 128).UInt64(2));
            Assert.Equal(6UL, ProtoObject.Read(entries[1].Bytes(1), 128).UInt64(4));
            Assert.Equal(3UL, ProtoObject.Read(entries[23].Bytes(1), 128).UInt64(4));
            if (!loseDuringLoading) await AssertMap(world, id, a.Slot, token);
            world.Client.LingerState = new LingerOption(true, 0); world.Dispose();
            await disconnected.Task.WaitAsync(token);
            using var fresh = new TcpClient(); await Login(host, fresh, session.IssueTicket(), token);
            using var reentered = await Enter(host, fresh, token, emptyRoster: false);
            await Send(reentered, "kta", WorldProtocol.Number(1, (ulong)id), token); Assert.True((await Event(reentered, "ksb", token)).Has(1));
            await Send(reentered, "kkc", [], token); await Event(reentered, "kjp", token); await Event(reentered, "jow", token); await Event(reentered, "irl", token); await Event(reentered, "krg", token);
            await AssertMap(reentered, id, a.Slot, token);
            await Send(otherWorld, "kne", WorldProtocol.Number(2, 1), token); await Event(otherWorld, "knb", token);
            Assert.Equal(new WorldLocation(100, 10, 3, 1), new LocationStore(root).LoadOrCreate(a.Id, 290, id,
                new(100, 11, 0, 1), EntryCatalog().Validate));
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task AssertMap(TcpClient world, long id, long account, CancellationToken token)
    {
        await Send(world, "kjt", WorldProtocol.Number(1, 100), token);
        var info = await Event(world, "jnj", token); Assert.Equal(100UL, info.UInt64(10)); Assert.Equal(25UL, info.UInt64(12));
        var actor = ProtoObject.Read(Assert.Single(info.Repeated(3)).Bytes, 128); Assert.Equal((ulong)id, actor.UInt64(1));
        var position = ProtoObject.Read(actor.Bytes(2), 128); Assert.Equal(10UL, position.UInt64(3)); Assert.Equal(3UL, position.UInt64(4));
        var details = ProtoObject.Read(actor.Bytes(3), 128); var look = ProtoObject.Read(details.Bytes(2), 128);
        Assert.Equal(1UL, look.UInt64(1)); Assert.Equal(new byte[] { 1, 1 }, look.Bytes(3));
        // Six indexed RGB colors, encoded independently from the production encoder.
        Assert.Equal(Convert.FromHexString("818080088280801083808018848080208580802886808030"), look.Bytes(5));
        Assert.Equal(new byte[] { 100 }, look.Bytes(6));
        var humanoid = ProtoObject.Read(ProtoObject.Read(details.Bytes(1), 128).Bytes(3), 128);
        Assert.Equal("Hero", humanoid.Text(5)); var player = ProtoObject.Read(humanoid.Bytes(1), 128);
        Assert.Equal((ulong)account, player.UInt64(2)); Assert.Equal(0UL, player.UInt64(7)); Assert.True(player.Has(6));
        var reference = await Event(world, "jnv", token); Assert.Equal(100UL, reference.UInt64(3)); Assert.Equal(25UL, reference.UInt64(5));
    }
    [Fact]
    public void ReleasingAnOldSelectionCannotReleaseItsReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "entry-generation-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var profile = profiles.Create("Alpha");
            var store = new CharacterStore(root); var id = CreateCharacter(store, profile.Id, "Hero");
            var service = new WorldEntryService(new CharacterService(store, SyntheticCatalog()), new LocationStore(root), EntryCatalog());
            var previous = service.Select(profile.Id, profile.Slot, new object(), id); Assert.NotNull(previous); previous.Dispose();
            using var current = service.Select(profile.Id, profile.Slot, new object(), id); Assert.NotNull(current);
            previous.Dispose(); Assert.Null(service.Select(profile.Id, profile.Slot, new object(), id));
        }
        finally { Directory.Delete(root, true); }
    }
}
