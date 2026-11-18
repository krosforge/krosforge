using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    private sealed class MovementClock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref now);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref now, elapsed.Ticks);
    }
    private sealed class MovementFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "motion-" + Guid.NewGuid().ToString("N"));
        internal readonly MovementClock Clock = new();
        internal readonly ProfileStore Profiles;
        internal readonly CharacterStore Characters;
        internal readonly WorldEntryCatalog Catalog;
        internal readonly WorldEntryService Service;
        internal readonly LocationStore Locations;
        private readonly List<WorldEntryService.Selection> selections = [];
        internal MovementFixture()
        {
            Profiles = new(Root); Characters = new(Root); Locations = new(Root);
            Catalog = MovementCatalog(1000);
            Service = new(new CharacterService(Characters, SyntheticCatalog()), Locations, Catalog, Clock);
        }
        internal (WorldEntryService.Selection Selection, List<WorldNotice> Notices) Join(string name)
        {
            var profile = Profiles.Create(name); var id = CreateCharacter(Characters, profile.Id, name);
            List<WorldNotice> notices = [];
            var selected = Service.Select(profile.Id, profile.Slot, new object(), id, notices.Add)!;
            selections.Add(selected); selected.ContextCreated = true; Service.Join(selected, 100);
            return (selected, notices);
        }
        public void Dispose()
        {
            foreach (var selected in selections) selected.Dispose();
            Profiles.Dispose(); Directory.Delete(Root, true);
        }
    }
    private static WorldEntryCatalog MovementCatalog(int stepMilliseconds = 50) => new(new(100, 0, 3, 1), new(14, 560, stepMilliseconds),
        [new(100, 200, "right", [2], 0, 0), new(200, 100, "left", [0], 2, 4)],
        new(100, 25, new HashSet<int> { 0, 1, 2, 3 }), new(200, 26, new HashSet<int> { 0, 1, 2, 3 }));
    [Fact]
    public async Task ConcurrentLocationWritesCannotOverwriteTheSameRevision()
    {
        using var f = new MovementFixture(); var (a, _) = f.Join("Alpha");
        var original = a.Location;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { f.Locations.Save(a.Profile, 290, a.Character.Id, original, original with { CellId = 1, Revision = 2 }, f.Catalog.Validate); return true; }
            catch (InvalidDataException) { return false; }
        })));
        Assert.Single(results, success => success);
        Assert.Equal(new WorldLocation(100, 1, 3, 2), f.Locations.LoadOrCreate(a.Profile, 290, a.Character.Id, original, f.Catalog.Validate));
    }
    [Fact]
    public void JoiningDuringMovementReceivesOneCoherentSnapshotAndRemainingPath()
    {
        using var f = new MovementFixture(); var (a, notices) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0, 3], 0)); f.Clock.Advance(TimeSpan.FromSeconds(1));
        var (b, joined) = f.Join("Beta");
        Assert.Equal(1, joined.OfType<MapSnapshot>().Single().Actors.Single(actor => actor.Character.Id == a.Character.Id).Location.CellId);
        Assert.Equal(new[] { 1, 2, 3 }, joined.OfType<ActorMoved>().Single().Cells);
        Assert.Equal(0, a.Location.CellId);
        f.Service.Join(b, 100); Assert.Single(notices.OfType<ActorAppeared>());
    }
    [Fact]
    public async Task MovementUsesServerTimeAndCommitsOnceDespiteDuplicateOrOldOperations()
    {
        using var f = new MovementFixture(); var (a, notices) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0, 2], 7)); var first = a.Moving!;
        f.Service.Advance(a, first); Assert.Equal(0, a.Location.CellId);
        f.Service.Move(a, new(100, [0, 2], 7)); Assert.Same(first, a.Moving);
        f.Clock.Advance(TimeSpan.FromSeconds(2)); f.Service.Advance(a, first);
        await a.MovementCompletion;
        Assert.Equal(new WorldLocation(100, 2, 0, 2), a.Location);
        f.Service.Move(a, new(100, [2, 3], 0)); var second = a.Moving;
        f.Service.Advance(a, first); Assert.Same(second, a.Moving); Assert.Equal(2, a.Location.Revision);
        f.Service.Cancel(a, 3); // Cannot cancel at a future cell.
        Assert.Equal(2, a.Location.CellId); Assert.Null(a.Moving);
        Assert.Equal(2, notices.OfType<ActorMoved>().Count());
        Assert.Equal(a.Location, f.Locations.LoadOrCreate(a.Profile, 290, a.Character.Id, f.Catalog.Initial, f.Catalog.Validate));
    }
    [Fact]
    public async Task PresenceSnapshotMovementAndTransitionRemainScopedAndOrdered()
    {
        using var f = new MovementFixture(); var (a, na) = f.Join("Alpha"); var (b, nb) = f.Join("Beta");
        Assert.Equal(new[] { a.Character.Id, b.Character.Id }, nb.OfType<MapSnapshot>().Single().Actors.Select(v => v.Character.Id));
        Assert.Equal(b.Character.Id, na.OfType<ActorAppeared>().Single().Actor.Character.Id);
        f.Service.Move(a, new(100, [0, 2], 0)); Assert.Single(nb.OfType<ActorMoved>());
        f.Service.ChangeMap(a, 200); // Moving is not permission to leave the map.
        Assert.Equal(100, a.Location.MapId); Assert.Equal(0, a.Location.CellId);
        f.Service.Move(a, new(100, [0, 2], 0));
        f.Clock.Advance(TimeSpan.FromSeconds(2)); f.Service.Advance(a, a.Moving!); await a.MovementCompletion;
        f.Service.ChangeMap(a, 200);
        Assert.Equal(new WorldLocation(200, 0, 0, 3), a.Location);
        Assert.Equal(a.Character.Id, nb.OfType<ActorDeparted>().Single().Id);
        Assert.IsType<MapLoading>(na[^1]);
        f.Service.Join(a, 200); Assert.Single(na.OfType<MapSnapshot>().Last().Actors);
        var previous = nb.Count;
        f.Service.Move(a, new(200, [0, 1], 0)); Assert.Equal(previous, nb.Count);
        f.Service.Cancel(a, 0); f.Service.ChangeMap(a, 100); f.Service.Join(a, 100);
        Assert.Equal(2, na.OfType<MapSnapshot>().Last().Actors.Count);
        Assert.Equal(a.Character.Id, nb.OfType<ActorAppeared>().Last().Actor.Character.Id);
    }
    [Fact]
    public async Task DisconnectDuringMovementKeepsConfirmedPositionAndOldTimerCannotAffectReconnect()
    {
        using var f = new MovementFixture(); var (a, _) = f.Join("Alpha"); var (_, others) = f.Join("Beta");
        f.Service.Move(a, new(100, [0, 3], 0)); var old = a.Moving!;
        f.Clock.Advance(TimeSpan.FromSeconds(1)); a.Dispose(); await a.MovementCompletion;
        Assert.Single(others.OfType<ActorDeparted>());
        using var next = f.Service.Select(a.Profile, a.Account, new object(), a.Character.Id)!;
        Assert.Equal(new WorldLocation(100, 0, 3, 1), next.Location);
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Service.Advance(a, old); a.Dispose();
        Assert.Null(f.Service.Select(a.Profile, a.Account, new object(), a.Character.Id));
        Assert.Equal(0, next.Location.CellId);
    }
    [Fact]
    public async Task CancellationPersistsOnlyReachedPrefixAndFailedTransitionKeepsPreviousLocation()
    {
        using var f = new MovementFixture(); var (a, notices) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0, 3], 0)); f.Clock.Advance(TimeSpan.FromSeconds(2)); f.Service.Cancel(a, 2);
        await a.MovementCompletion; Assert.Equal(new WorldLocation(100, 2, 0, 2), a.Location);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(f.Root, "krosforge.db"), Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER block_location BEFORE UPDATE ON character_locations BEGIN SELECT RAISE(ABORT,'test failure'); END"; command.ExecuteNonQuery();
        var before = notices.Count;
        Assert.Throws<SqliteException>(() => f.Service.ChangeMap(a, 200));
        Assert.Equal(new WorldLocation(100, 2, 0, 2), a.Location); Assert.Equal(before, notices.Count);
        command.CommandText = "DROP TRIGGER block_location"; command.ExecuteNonQuery();
        f.Service.ChangeMap(a, 200); a.Dispose();
        using var restored = f.Service.Select(a.Profile, a.Account, new object(), a.Character.Id)!;
        Assert.Equal(new WorldLocation(200, 0, 0, 3), restored.Location);
    }
}
