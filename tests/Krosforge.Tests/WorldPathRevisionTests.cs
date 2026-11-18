using System.Net.Sockets;
using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    [Fact]
    public async Task TcpPathRevisionKeepsPeersAlignedAcrossTransitionAndReconnect()
    {
        var root = Path.Combine(Path.GetTempPath(), "revision-tcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var a = profiles.Create("Alpha"); var b = profiles.Create("Beta");
            var store = new CharacterStore(root); var ca = CreateCharacter(store, a.Id, "Hero"); var cb = CreateCharacter(store, b.Id, "Other");
            var catalog = MovementCatalog(1000, 1); var locations = new LocationStore(root); var clock = new MovementClock(deferTimers: true);
            var service = new WorldEntryService(new(store, SyntheticCatalog()), locations, catalog, clock);
            await using var host = new LocalSessionHost(characters: new(store, SyntheticCatalog()), worldEntry: service);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10)); var token = timeout.Token;
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sa = host.Register(a, "pt", phase => { if (phase == "disconnected") disconnected.TrySetResult(); });
            using var sb = host.Register(b, "pt", _ => { });
            using var la = new TcpClient(); using var lb = new TcpClient();
            await Login(host, la, sa.IssueTicket(), token); await Login(host, lb, sb.IssueTicket(), token);
            using var wa = await Enter(host, la, token, emptyRoster: false); using var wb = await Enter(host, lb, token, emptyRoster: false);
            wa.NoDelay = true; wb.NoDelay = true;
            await EnterMap(wa, ca, 100, token); await EnterMap(wb, cb, 100, token); await Event(wa, "jns", token);
            await Send(wa, "jok", Convert.FromHexString("0a0200031064"), token);
            await Event(wa, "jnz", token); await Event(wb, "jnz", token);
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            await Send(wa, "jok", Convert.FromHexString("0a0200021064"), token);
            foreach (var socket in new[] { wa, wb })
            {
                var movement = await Event(socket, "jnz", token);
                Assert.Equal((ulong)ca, movement.UInt64(4)); Assert.Equal(new byte[] { 1,2 }, movement.Bytes(1));
            }
            await Send(wa, "jny", [], token); await Send(wa, "kne", [], token); await Event(wa, "knb", token);
            Assert.Equal(catalog.Initial, locations.LoadOrCreate(a.Id, 290, ca, catalog.Initial, catalog.Validate));
            clock.Advance(TimeSpan.FromMilliseconds(500)); // No timer callback: map change must finish the revision.
            await Send(wa, "joc", Convert.FromHexString("10c801"), token);
            await Event(wa, "jpk", token); Assert.Equal(200UL, (await Event(wa, "jow", token)).UInt64(1));
            Assert.Equal((ulong)ca, (await Event(wb, "kjz", token)).UInt64(1));
            await LoadedMap(wa, 200, token);
            wa.Client.LingerState = new LingerOption(true, 0); wa.Dispose(); await disconnected.Task.WaitAsync(token);
            using var login = new TcpClient(); await Login(host, login, sa.IssueTicket(), token);
            using var restored = await Enter(host, login, token, emptyRoster: false);
            var snapshot = await EnterMap(restored, ca, 200, token);
            var actor = ProtoObject.Read(Assert.Single(snapshot.Repeated(3)).Bytes, 128);
            Assert.Equal((ulong)ca, actor.UInt64(1)); Assert.Equal(0UL, ProtoObject.Read(actor.Bytes(2), 128).UInt64(3));
            Assert.Equal(new WorldLocation(200, 0, 0, 3), locations.LoadOrCreate(a.Id, 290, ca, catalog.Initial, catalog.Validate));
            Assert.Equal(catalog.Initial, locations.LoadOrCreate(b.Id, 290, cb, catalog.Initial, catalog.Validate));
            await Send(wb, "kne", [], token); await Event(wb, "knb", token); // No correction or stale movement queued.
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NativePathRevisionDoesNotTriggerTheObservedCorrectionCascade()
    {
        var catalog = new WorldEntryCatalog(new(100, 287, 0, 1), ShippedMovementPolicy(), [],
            new EntryMap(100, 1, Enumerable.Range(0, 560).ToHashSet()));
        using var f = new MovementFixture(deferTimers: true, catalog: catalog);
        var (a, notices) = f.Join("Alpha"); var (_, observer) = f.Join("Beta");
        f.Service.Move(a, new(100, [287,288,274,277,333,391], 0));
        var first = a.Moving!;
        f.Clock.Advance(TimeSpan.FromMilliseconds(1024));
        f.Service.Move(a, new(100, [287,288,274,277,333,377], 0));
        Assert.Empty(notices.OfType<ActorCorrected>());
        Assert.NotNull(a.Moving);
        Assert.Equal(first.Started, a.Moving.Started);
        Assert.Equal(377, a.Moving.Path.Cells[^1]);
        Assert.Equal(305, notices.OfType<ActorMoved>().Last().Cells[0]);
        Assert.Equal(notices.OfType<ActorMoved>().Last().Cells, observer.OfType<ActorMoved>().Last().Cells);
        f.Service.Advance(a, first); Assert.Equal(377, a.Moving.Path.Cells[^1]);
        // Recorded at 15:37:13.506 -> 15:37:19.281, before recovery from the
        // old implementation's forced cell 305. The client can still interrupt
        // an unacknowledged animation after the authoritative timer has elapsed.
        (int Milliseconds, int[] Cells)[] requests =
        [
            (2041, [277]), (2042, [277,274,260,259]),
            (3024, [259]), (3025, [259,371,429]),
            (4208, [429]), (4209, [429,371,231]),
            (4942, [343]), (4943, [343,455]), (5775, [455,203,188,160])
        ];
        var previous = 1024;
        foreach (var (milliseconds, cells) in requests)
        {
            f.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds - previous)); previous = milliseconds;
            if (cells.Length == 1) f.Service.Cancel(a, cells[0]);
            else f.Service.Move(a, new(100, cells, 0));
            Assert.Empty(notices.OfType<ActorCorrected>());
        }
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Service.Advance(a, a.Moving!);
        Assert.Equal(160, a.Location.CellId);
    }

    [Fact]
    public async Task RevisingTheFutureTailKeepsDeadlinesAndInvalidatesTheOldTimer()
    {
        var catalog = new WorldEntryCatalog(new(100, 0, 0, 1), new(14, 560, 1000), [],
            new EntryMap(100, 1, Enumerable.Range(0, 560).ToHashSet()));
        using var f = new MovementFixture(deferTimers: true, catalog: catalog);
        var (a, notices) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0,5], 0)); var first = a.Moving!; var completion = a.MovementCompletion;
        f.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        f.Service.Move(a, new(100, [0,4], 0)); var revised = a.Moving!;
        await completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotSame(first, revised); Assert.Equal(first.Started, revised.Started);
        Assert.Equal(new[] { 1,2,3,4 }, notices.OfType<ActorMoved>().Last().Cells);
        f.Service.Move(a, new(100, [0,4], 0)); Assert.Same(revised, a.Moving);
        f.Clock.Advance(TimeSpan.FromMilliseconds(2499)); f.Service.Advance(a, first); f.Service.Advance(a, revised);
        Assert.Equal(0, a.Location.CellId);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1)); f.Service.Advance(a, revised);
        Assert.Equal(new WorldLocation(100, 4, 0, 2), a.Location);
    }

    [Theory]
    [InlineData("past")]
    [InlineData("active-segment")]
    [InlineData("shorter-than-progress")]
    [InlineData("ends-at-progress")]
    [InlineData("other-map")]
    public void ARevisionCannotRewriteTravelledCellsOrSkipTheActiveSegment(string kind)
    {
        var catalog = new WorldEntryCatalog(new(100, 0, 0, 1), new(14, 560, 1000), [],
            new EntryMap(100, 1, Enumerable.Range(0, 560).ToHashSet()));
        using var f = new MovementFixture(deferTimers: true, catalog: catalog);
        var (a, notices) = f.Join("Alpha"); f.Service.Move(a, new(100, [0,5], 0));
        f.Clock.Advance(TimeSpan.FromMilliseconds(kind == "ends-at-progress" ? 1000 : 1500));
        var turns = kind switch { "past" => new[] { 0,14,1,4 }, "active-segment" => [0,1,15,18], "shorter-than-progress" or "ends-at-progress" => [0,1], _ => [0,4] };
        f.Service.Move(a, new(kind == "other-map" ? 200 : 100, turns, 0));
        Assert.Null(a.Moving); Assert.Equal(1, a.Location.CellId);
        Assert.Equal(1, Assert.Single(notices.OfType<ActorCorrected>()).Cell);
    }

    [Fact]
    public void ARevisionPreservesThePartialReversalAndTheRemainingPathForNewObservers()
    {
        using var f = new MovementFixture(deferTimers: true, cancellationLeadSteps: 1);
        var (a, notices) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0,3], 0));
        f.Clock.Advance(TimeSpan.FromMilliseconds(1600)); f.Service.Cancel(a, 2);
        f.Service.Move(a, new(100, [2,0], 4)); var reversed = a.Moving!;
        Assert.Equal(TimeSpan.FromMilliseconds(400), reversed.FirstStepCredit);
        f.Clock.Advance(TimeSpan.FromMilliseconds(200));
        f.Service.Move(a, new(100, [2,1], 4)); var revised = a.Moving!;
        Assert.Equal(reversed.Started, revised.Started);
        Assert.Equal(reversed.FirstStepCredit, revised.FirstStepCredit);
        Assert.Equal(TimeSpan.FromMilliseconds(600), revised.Path.Duration);
        Assert.Empty(notices.OfType<ActorCorrected>());
        var (_, observer) = f.Join("Beta");
        Assert.Equal(new[] { 2,1 }, Assert.Single(observer.OfType<ActorMoved>()).Cells);
        f.Clock.Advance(TimeSpan.FromMilliseconds(399)); f.Service.Advance(a, revised);
        Assert.Equal(2, a.Location.CellId);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1)); f.Service.Advance(a, revised);
        Assert.Equal(1, a.Location.CellId);
    }
}
