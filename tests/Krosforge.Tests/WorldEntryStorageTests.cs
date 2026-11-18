using Krosforge.Core;
using Krosforge.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Krosforge.Tests;

public sealed class WorldEntryStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "entry-" + Guid.NewGuid().ToString("N"));
    private static readonly WorldLocation Initial = new(100, 10, 3, 1);
    private static void Validate(WorldLocation location)
    { location.Validate(); if (location.MapId != 100 || location.CellId is < 10 or > 20) throw new InvalidDataException("Unsupported position"); }
    private (Guid Profile, long Character) Create()
    {
        using var profiles = new ProfileStore(root); var profile = profiles.Create("Alpha");
        var store = new CharacterStore(root);
        store.Create(profile.Id, 290, "Hero", new(1, ClientInstallation.SupportedBuild, 1, 0, 1, 1, [1,2,3,4,5,6]), out var id);
        return (profile.Id, id);
    }
    [Fact]
    public async Task ConcurrentInitialEntryCreatesOnlyOneDurablePosition()
    {
        var (profile, character) = Create();
        var store = new LocationStore(root);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            store.LoadOrCreate(profile, 290, character, Initial with { CellId = 10 + i }, Validate))));
        Assert.Single(results.Distinct());
        Assert.Equal(results[0], new LocationStore(root).LoadOrCreate(profile, 290, character, Initial, Validate));
    }
    [Fact]
    public void ForeignDeletedAndWrongRealmCharactersCannotAcquireLocations()
    {
        var (profile, character) = Create(); var store = new LocationStore(root);
        Assert.Null(store.LoadOrCreate(Guid.NewGuid(), 290, character, Initial, Validate));
        Assert.Null(store.LoadOrCreate(profile, 291, character, Initial, Validate));
        Assert.Equal(Initial, store.LoadOrCreate(profile, 290, character, Initial, Validate));
        Assert.True(new CharacterStore(root).Delete(profile, 290, character));
        Assert.Null(store.LoadOrCreate(profile, 290, character, Initial, Validate));
        using var db = Open(); Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM character_locations"));
    }
    [Fact]
    public void InvalidSavedPositionIsRejectedWithoutResettingIt()
    {
        var (profile, character) = Create(); var store = new LocationStore(root);
        store.LoadOrCreate(profile, 290, character, Initial, Validate);
        using var db = Open(); Execute(db, "UPDATE character_locations SET map_id=999");
        Assert.Throws<InvalidDataException>(() => store.LoadOrCreate(profile, 290, character, Initial, Validate));
        Assert.Equal(999L, Scalar(db, "SELECT map_id FROM character_locations"));
    }
    [Fact]
    public void FailedInitializationCanBeRetriedWithoutPartialPosition()
    {
        var (profile, character) = Create(); var store = new LocationStore(root);
        using var db = Open(); Execute(db, "CREATE TRIGGER reject_position AFTER INSERT ON character_locations BEGIN SELECT RAISE(ABORT,'failure'); END");
        Assert.Throws<SqliteException>(() => store.LoadOrCreate(profile, 290, character, Initial, Validate));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM character_locations"));
        Execute(db, "DROP TRIGGER reject_position");
        Assert.Equal(Initial, store.LoadOrCreate(profile, 290, character, Initial, Validate));
    }
    private SqliteConnection Open()
    { var db = new SqliteConnection("Pooling=False;Data Source=" + Path.Combine(root, "krosforge.db")); db.Open(); return db; }
    private static object? Scalar(SqliteConnection db, string sql)
    { using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
    private static void Execute(SqliteConnection db, string sql)
    { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
