using System.Net.Sockets;
using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    private static async Task<ProtoObject> EnterMap(TcpClient world, long id, long map, CancellationToken token)
    {
        await Send(world, "kta", WorldProtocol.Number(1, (ulong)id), token); await Event(world, "ksb", token);
        await Send(world, "kkc", [], token); await Event(world, "kjp", token);
        Assert.Equal((ulong)map, (await Event(world, "jow", token)).UInt64(1));
        await Event(world, "irl", token); await Event(world, "krg", token);
        return await LoadedMap(world, map, token);
    }
    private static async Task<ProtoObject> LoadedMap(TcpClient world, long map, CancellationToken token)
    {
        await Send(world, "kjt", WorldProtocol.Number(1, (ulong)map), token);
        var snapshot = await Event(world, "jnj", token); await Event(world, "jnv", token); return snapshot;
    }
    private static ulong[] ActorIds(ProtoObject snapshot) => snapshot.Repeated(3).Select(f => ProtoObject.Read(f.Bytes, 128).UInt64(1)).ToArray();
    [Fact]
    public async Task TcpRapidRedirectionCorrectsToTheActiveSegmentAndPreservesRemainingTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "redirect-tcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var a = profiles.Create("Alpha"); var b = profiles.Create("Beta");
            var store = new CharacterStore(root); var ca = CreateCharacter(store, a.Id, "Hero"); var cb = CreateCharacter(store, b.Id, "Other");
            var catalog = MovementCatalog(1000, 1); var locations = new LocationStore(root); var clock = new MovementClock();
            var service = new WorldEntryService(new(store, SyntheticCatalog()), locations, catalog, clock);
            await using var host = new LocalSessionHost(characters: new(store, SyntheticCatalog()), worldEntry: service);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10)); var token = timeout.Token;
            using var sa = host.Register(a, "pt", _ => { }); using var sb = host.Register(b, "pt", _ => { });
            using var la = new TcpClient(); using var lb = new TcpClient();
            await Login(host, la, sa.IssueTicket(), token); await Login(host, lb, sb.IssueTicket(), token);
            using var wa = await Enter(host, la, token, emptyRoster: false); using var wb = await Enter(host, lb, token, emptyRoster: false);
            wa.NoDelay = true; wb.NoDelay = true;
            await EnterMap(wa, ca, 100, token); await EnterMap(wb, cb, 100, token); await Event(wa, "jns", token);
            await Send(wa, "jok", Convert.FromHexString("0a0200031064"), token);
            await Event(wa, "jnz", token); await Event(wb, "jnz", token);
            clock.Advance(TimeSpan.FromMilliseconds(600));
            await Send(wa, "jpj", Convert.FromHexString("0801"), token);
            await Event(wa, "jpk", token);
            foreach (var socket in new[] { wa, wb })
            {
                var correction = await Event(socket, "joz", token);
                Assert.Equal(1UL, correction.UInt64(1)); Assert.Equal((ulong)ca, correction.UInt64(2));
            }
            await Send(wa, "jok", Convert.FromHexString("0a0201031064"), token);
            await Send(wa, "kne", [], token); await Event(wa, "knb", token); // New animation waits for the unfinished segment.
            clock.Advance(TimeSpan.FromMilliseconds(400));
            foreach (var socket in new[] { wa, wb })
                Assert.Equal(new byte[] { 1, 2, 3 }, (await Event(socket, "jnz", token)).Bytes(1));
            await Send(wa, "jny", [], token); // Old animation ACK cannot finish the new path.
            clock.Advance(TimeSpan.FromMilliseconds(600));
            await Send(wa, "kne", [], token); await Event(wa, "knb", token);
            Assert.Equal(new WorldLocation(100, 1, 0, 2), locations.LoadOrCreate(a.Id, 290, ca, catalog.Initial, catalog.Validate));
            clock.Advance(TimeSpan.FromMilliseconds(1400)); await Event(wa, "jpk", token);
            Assert.Equal(new WorldLocation(100, 3, 0, 3), locations.LoadOrCreate(a.Id, 290, ca, catalog.Initial, catalog.Validate));
            Assert.Equal(catalog.Initial, locations.LoadOrCreate(b.Id, 290, cb, catalog.Initial, catalog.Validate));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task TcpPresenceMovementTransitionsAndReconnectKeepAccountsAndMapsIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "motion-tcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var a = profiles.Create("Alpha"); var b = profiles.Create("Beta");
            var store = new CharacterStore(root); var ca = CreateCharacter(store, a.Id, "Hero"); var cb = CreateCharacter(store, b.Id, "Other");
            var catalog = MovementCatalog(); var locations = new LocationStore(root); var clock = new MovementClock();
            var service = new WorldEntryService(new(store, SyntheticCatalog()), locations, catalog, clock);
            await using var host = new LocalSessionHost(characters: new(store, SyntheticCatalog()), worldEntry: service);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var token = timeout.Token;
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sa = host.Register(a, "pt", phase => { if (phase == "disconnected") disconnected.TrySetResult(); });
            using var sb = host.Register(b, "pt", _ => { });
            using var la = new TcpClient(); using var lb = new TcpClient();
            await Login(host, la, sa.IssueTicket(), token); await Login(host, lb, sb.IssueTicket(), token);
            using var wa = await Enter(host, la, token, emptyRoster: false); using var wb = await Enter(host, lb, token, emptyRoster: false);
            wa.NoDelay = true; wb.NoDelay = true;
            Assert.Equal(new[] { (ulong)ca }, ActorIds(await EnterMap(wa, ca, 100, token)));
            Assert.Equal(new[] { (ulong)ca, (ulong)cb }, ActorIds(await EnterMap(wb, cb, 100, token)));
            var appeared = await Event(wa, "jns", token);
            Assert.Equal((ulong)cb, ProtoObject.Read(Assert.Single(appeared.Repeated(1)).Bytes, 128).UInt64(1));

            // Independent protobuf fixture: packed turning cells [0,2], map=100.
            await Send(wa, "jok", Convert.FromHexString("0a0200021064"), token);
            foreach (var socket in new[] { wa, wb })
            {
                var movement = await Event(socket, "jnz", token);
                Assert.Equal((ulong)ca, movement.UInt64(4)); Assert.Equal(new byte[] { 0, 1, 2 }, movement.Bytes(1));
            }
            await Send(wa, "jny", [], token); await Send(wa, "kne", [], token); await Event(wa, "knb", token);
            Assert.Equal(0, locations.LoadOrCreate(a.Id, 290, ca, catalog.Initial, catalog.Validate)!.CellId);
            clock.Advance(TimeSpan.FromMilliseconds(100)); await Event(wa, "jpk", token);
            Assert.Equal(new WorldLocation(100, 2, 0, 2), locations.LoadOrCreate(a.Id, 290, ca, catalog.Initial, catalog.Validate));
            for (var i = 0; i < 60; i++) await Send(wa, "lwx", [], token);

            await Send(wa, "joc", Convert.FromHexString("10c801"), token); // destination=200
            Assert.Equal(200UL, (await Event(wa, "jow", token)).UInt64(1));
            Assert.Equal((ulong)ca, (await Event(wb, "kjz", token)).UInt64(1));
            Assert.Equal(new[] { (ulong)ca }, ActorIds(await LoadedMap(wa, 200, token)));
            for (var i = 0; i < 60; i++) await Send(wa, "lwx", [], token);
            await Send(wb, "jok", Convert.FromHexString("0a0200011064"), token);
            await Event(wb, "jnz", token);
            await Send(wa, "kne", [], token); await Event(wa, "knb", token); // no movement from the other map
            clock.Advance(TimeSpan.FromMilliseconds(50)); await Event(wb, "jpk", token);
            await Send(wa, "jny", [], token); // delayed ACK from the previous map cannot update location
            await Send(wa, "joc", Convert.FromHexString("1064"), token);
            await Event(wa, "jow", token);
            Assert.Equal(new[] { (ulong)ca, (ulong)cb }, ActorIds(await LoadedMap(wa, 100, token)));
            for (var i = 0; i < 60; i++) await Send(wa, "lwx", [], token);
            await Event(wb, "jns", token);

            // Drop the socket after a durable transition, before map readiness.
            await Send(wa, "joc", Convert.FromHexString("10c801"), token); await Event(wa, "jow", token); await Event(wb, "kjz", token);
            wa.Client.LingerState = new LingerOption(true, 0); wa.Dispose(); await disconnected.Task.WaitAsync(token);
            using var login = new TcpClient(); await Login(host, login, sa.IssueTicket(), token);
            using var restored = await Enter(host, login, token, emptyRoster: false);
            Assert.Equal(new[] { (ulong)ca }, ActorIds(await EnterMap(restored, ca, 200, token)));
            await Send(wb, "kne", [], token); await Event(wb, "knb", token);
            Assert.Equal(new WorldLocation(200, 0, 0, 5), locations.LoadOrCreate(a.Id, 290, ca, catalog.Initial, catalog.Validate));
            Assert.Equal(new WorldLocation(100, 1, 0, 2), locations.LoadOrCreate(b.Id, 290, cb, catalog.Initial, catalog.Validate));
        }
        finally { Directory.Delete(root, true); }
    }
}
