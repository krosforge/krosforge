using System.Net.Sockets;
using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;

namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    [Fact]
    public async Task TcpWalksEverySurfaceConnectionAndRestoresExactArrivalAfterDisconnect()
    {
        var catalog = SurfaceMapDataTests.ReferenceCatalog();
        var root = Path.Combine(Path.GetTempPath(), "surface-tcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var a = profiles.Create("Alpha"); var b = profiles.Create("Beta");
            var store = new CharacterStore(root); var ca = CreateCharacter(store, a.Id, "Hero"); var cb = CreateCharacter(store, b.Id, "Other");
            var locations = new LocationStore(root); var clock = new MovementClock(deferTimers: true);
            var service = new WorldEntryService(new(store, SyntheticCatalog()), locations, catalog, clock);
            await using var host = new LocalSessionHost(characters: new(store, SyntheticCatalog()), worldEntry: service);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90)); var token = timeout.Token;
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sa = host.Register(a, "pt", phase => { if (phase == "disconnected") disconnected.TrySetResult(); });
            using var sb = host.Register(b, "pt", _ => { });
            using var la = new TcpClient(); using var lb = new TcpClient();
            await Login(host, la, sa.IssueTicket(), token); await Login(host, lb, sb.IssueTicket(), token);
            using var wa = await Enter(host, la, token, emptyRoster: false); using var wb = await Enter(host, lb, token, emptyRoster: false);
            wa.NoDelay = true; wb.NoDelay = true;
            var current = catalog.Initial;
            await EnterMap(wa, ca, current.MapId, token); await EnterMap(wb, cb, current.MapId, token); await Event(wa, "jns", token);
            var links = catalog.Transitions.GroupBy(r => (r.Source, r.Destination)).ToDictionary(g => g.Key, g => g.First());
            var remaining = links.Keys.ToHashSet();
            var visits = new HashSet<long> { current.MapId };
            while (remaining.Count > 0)
            {
                // Find a route to the nearest map with an untested outgoing connection.
                var queue = new Queue<(long Map, MapTransition[] Path)>(); queue.Enqueue((current.MapId, []));
                var seen = new HashSet<long> { current.MapId }; MapTransition[]? itinerary = null;
                while (queue.TryDequeue(out var item) && itinerary is null)
                    foreach (var route in links.Values.Where(r => r.Source == item.Map))
                    {
                        if (remaining.Contains((route.Source, route.Destination))) { itinerary = [.. item.Path, route]; break; }
                        if (seen.Add(route.Destination)) queue.Enqueue((route.Destination, [.. item.Path, route]));
                    }
                Assert.NotNull(itinerary);
                foreach (var route in itinerary)
                {
                    var map = catalog.Map(current.MapId);
                    var path = SurfaceMapDataTests.FindPath(map, catalog.Movement, current.CellId, route.ExitCell);
                    if (path.Length > 1)
                    {
                        var payload = WorldProtocol.Join([.. path.Select(c => WorldProtocol.Number(1, (ulong)c)), WorldProtocol.Number(2, (ulong)current.MapId)]);
                        await Send(wa, "jok", payload, token);
                        Assert.Equal((ulong)ca, (await Event(wa, "jnz", token)).UInt64(4));
                        if (current.MapId == catalog.Initial.MapId) await Event(wb, "jnz", token);
                        clock.Advance(TimeSpan.FromMinutes(1));
                    }
                    await Send(wa, "joc", WorldProtocol.Number(2, (ulong)route.Destination), token);
                    if (path.Length > 1) await Event(wa, "jpk", token);
                    Assert.Equal((ulong)route.Destination, (await Event(wa, "jow", token)).UInt64(1));
                    if (current.MapId == catalog.Initial.MapId) await Event(wb, "kjz", token);
                    current = new(route.Destination, route.ArrivalCell, route.Direction, current.Revision + (path.Length > 1 ? 2 : 1));
                    var snapshot = await LoadedMap(wa, current.MapId, token);
                    AssertSurfacePosition(snapshot, ca, current);
                    if (current.MapId == catalog.Initial.MapId) await Event(wb, "jns", token);
                    else Assert.Single(snapshot.Repeated(3));
                    Assert.Equal(current, locations.LoadOrCreate(a.Id, 290, ca, catalog.Initial, catalog.Validate));
                    remaining.Remove((route.Source, route.Destination)); visits.Add(current.MapId);
                }
            }
            Assert.Equal(50, visits.Count);
            // Excluded indoor map identifiers cannot move or persist this character.
            await Send(wa, "joc", WorldProtocol.Number(2, 153884672), token);
            await Event(wa, "jpk", token);
            Assert.Equal((ulong)current.CellId, (await Event(wa, "joz", token)).UInt64(1));
            if (current.MapId == catalog.Initial.MapId) await Event(wb, "joz", token);
            Assert.Equal(current, locations.LoadOrCreate(a.Id, 290, ca, catalog.Initial, catalog.Validate));
            wa.Client.LingerState = new LingerOption(true, 0); wa.Dispose();
            await disconnected.Task.WaitAsync(token);
            if (current.MapId == catalog.Initial.MapId) await Event(wb, "kjz", token);
            using var login = new TcpClient(); await Login(host, login, sa.IssueTicket(), token);
            using var restored = await Enter(host, login, token, emptyRoster: false);
            AssertSurfacePosition(await EnterMap(restored, ca, current.MapId, token), ca, current);
            if (current.MapId == catalog.Initial.MapId) await Event(wb, "jns", token);
            await Send(wb, "kne", [], token); await Event(wb, "knb", token);
            Assert.Equal(catalog.Initial, locations.LoadOrCreate(b.Id, 290, cb, catalog.Initial, catalog.Validate));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AssertSurfacePosition(ProtoObject snapshot, long id, WorldLocation expected)
    {
        Assert.Equal((ulong)expected.MapId, snapshot.UInt64(10));
        var actor = snapshot.Repeated(3).Select(f => ProtoObject.Read(f.Bytes, 128)).Single(a => a.UInt64(1) == (ulong)id);
        var position = ProtoObject.Read(actor.Bytes(2), 128);
        Assert.Equal((ulong)expected.CellId, position.UInt64(3));
        Assert.Equal((ulong)expected.Direction, position.UInt64(4));
    }
}
