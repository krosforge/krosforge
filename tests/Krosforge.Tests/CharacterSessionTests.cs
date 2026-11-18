using System.Net.Sockets;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    private static CreationCatalog SyntheticCatalog()
    {
        Dictionary<string, object> option = new() { ["id"] = 1, ["breed"] = 1, ["gender"] = 0, ["availableAtCreation"] = 1, ["payable"] = 0, ["order"] = 0, ["skins"] = "1" };
        return new(new() { [1] = new() { ["maleLook"] = "{1|1||100}", ["maleColors"] = new Dictionary<string, object> { ["array"] = new object[] { 1, 2, 3, 4, 5, 6 } } } }, new() { [1] = option }, new() { [1] = option });
    }
    [Fact]
    public async Task CharacterTcpCyclePersistsAndRequiresOwnedPreparedDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "character-tcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var a = profiles.Create("Alpha"); var b = profiles.Create("Beta");
            var store = new CharacterStore(root);
            await using var host = new LocalSessionHost(characters: new CharacterService(store, SyntheticCatalog()));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var token = timeout.Token;
            using var first = host.Register(a, "en", _ => { }); using var second = host.Register(b, "pt", _ => { });
            using var loginA = new TcpClient(); using var loginB = new TcpClient();
            await Login(host, loginA, first.IssueTicket(), token); await Login(host, loginB, second.IssueTicket(), token);
            using var worldA = await Enter(host, loginA, token); using var worldB = await Enter(host, loginB, token);
            var draft = WorldProtocol.Bytes(1, WorldProtocol.Join(S(2, "Hero"), WorldProtocol.Number(4, 1)));
            await Send(worldA, "kru", draft, token); Assert.Equal(0UL, (await Event(worldA, "ksn", token)).UInt64(1));
            var roster = await Event(worldA, "ksv", token); var character = ProtoObject.Read(Assert.Single(roster.Repeated(2)).Bytes, 128); var id = character.UInt64(3);
            Assert.Equal((long)id, Assert.Single(new CharacterStore(root).List(a.Id, 290)).Id);
            await Send(worldB, "ksu", [], token); (await Event(worldB, "ksv", token)).Only();
            await Send(worldB, "ksm", WorldProtocol.Number(1, id), token); await Event(worldB, "kst", token);
            await Send(worldA, "kso", WorldProtocol.Number(2, id), token); await Event(worldA, "kst", token);
            Assert.Equal(1, store.Count(a.Id, 290));
            await Send(worldA, "ksm", WorldProtocol.Number(1, id), token); await Event(worldA, "ksw", token);
            await Send(worldA, "kso", WorldProtocol.Number(2, id + 1), token); await Event(worldA, "kst", token);
            await Send(worldA, "ksm", WorldProtocol.Number(1, id), token); await Event(worldA, "ksw", token);
            await Send(worldA, "kso", WorldProtocol.Number(2, id), token); (await Event(worldA, "ksv", token)).Only();
            Assert.Empty(new CharacterStore(root).List(a.Id, 290));
            await Send(worldA, "kso", WorldProtocol.Number(2, id), token); await Event(worldA, "kst", token);
            await Send(worldB, "kne", WorldProtocol.Number(2, 1), token); await Event(worldB, "knb", token);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SocketLossAfterCommitPreservesDurableState(bool deletion)
    {
        var root = Path.Combine(Path.GetTempPath(), "character-loss-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var profile = profiles.Create("Alpha");
            var store = new CharacterStore(root);
            await using var host = new LocalSessionHost(characters: new CharacterService(store, SyntheticCatalog()));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = timeout.Token;
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var session = host.Register(profile, "en", phase => { if (phase == "disconnected") disconnected.TrySetResult(); });
            using var login = new TcpClient(); await Login(host, login, session.IssueTicket(), token);
            using var world = await Enter(host, login, token);
            await Send(world, "kru", WorldProtocol.Bytes(1, WorldProtocol.Join(S(2, "Hero"), WorldProtocol.Number(4, 1))), token);
            if (deletion)
            {
                await Event(world, "ksn", token); await Event(world, "ksv", token);
                var id = (ulong)Assert.Single(store.List(profile.Id, 290)).Id;
                await Send(world, "ksm", WorldProtocol.Number(1, id), token); await Event(world, "ksw", token);
                await Send(world, "kso", WorldProtocol.Number(2, id), token);
            }
            // Observe the commit without consuming the response, then reset the real socket.
            while (store.Count(profile.Id, 290) != (deletion ? 0 : 1)) await Task.Delay(10, token);
            world.Client.LingerState = new LingerOption(true, 0); world.Dispose();
            await disconnected.Task.WaitAsync(token);
            using var fresh = new TcpClient(); await Login(host, fresh, session.IssueTicket(), token);
            Assert.Equal(deletion ? 0 : 1, new CharacterStore(root).Count(profile.Id, 290));
        }
        finally { Directory.Delete(root, true); }
    }

}
