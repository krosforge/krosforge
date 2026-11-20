using Krosforge.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Krosforge.Tests;

public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "migration-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(root, "krosforge.db");
    private SqliteConnection Open(string? path = null)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? Database, Pooling = false, ForeignKeys = true }.ToString());
        db.Open(); return db;
    }
    private static void Execute(SqliteConnection db, string sql)
    { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql)
    { using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
    private SqliteConnection Legacy()
    {
        Directory.CreateDirectory(root);
        var db = Open();
        Execute(db, """
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=0;
            CREATE TABLE profiles(id TEXT PRIMARY KEY,name TEXT NOT NULL,name_key TEXT NOT NULL UNIQUE,slot INTEGER NOT NULL UNIQUE);
            CREATE TABLE counters(name TEXT PRIMARY KEY,value INTEGER NOT NULL);
            CREATE TABLE characters(id INTEGER PRIMARY KEY AUTOINCREMENT,profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,realm_id INTEGER NOT NULL,name TEXT NOT NULL,name_key TEXT NOT NULL,appearance TEXT NOT NULL,UNIQUE(realm_id,name_key));
            CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE cleanup(profile_id TEXT PRIMARY KEY);
            INSERT INTO profiles VALUES('11111111-1111-1111-1111-111111111111','Alpha','ALPHA',7);
            INSERT INTO counters VALUES('profile',7);
            INSERT INTO characters VALUES(42,'11111111-1111-1111-1111-111111111111',290,'Hero','HERO','{"Version":1,"Colors":[1,2,3,4,5,6]}');
            INSERT INTO settings VALUES('server.name','Custom');
            PRAGMA user_version=1;
            """);
        return db;
    }
    private static void AssertLegacyData(SqliteConnection db)
    {
        Assert.Equal("Alpha", Scalar(db, "SELECT name FROM profiles"));
        Assert.Equal(7L, Scalar(db, "SELECT value FROM counters"));
        Assert.Equal("Custom", Scalar(db, "SELECT value FROM settings"));
        Assert.Equal("{\"Version\":1,\"Colors\":[1,2,3,4,5,6]}", Scalar(db, "SELECT appearance FROM characters WHERE id=42"));
    }
    [Fact]
    public void MigrationBacksUpCommittedWalAndPreservesAllExistingData()
    {
        using var legacy = Legacy();
        Assert.True(new FileInfo(Database + "-wal").Length > 0);
        using (var store = new ProfileStore(root)) Assert.Equal("Alpha", Assert.Single(store.List()).Name);
        Assert.Equal((long)DatabaseSchema.Current, Scalar(legacy, "PRAGMA user_version")); AssertLegacyData(legacy);
        Assert.Equal(0L, Scalar(legacy, "SELECT COUNT(*) FROM character_locations"));
        var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "*.db"));
        using var backup = Open(backupPath);
        Assert.Equal(1L, Scalar(backup, "PRAGMA user_version")); AssertLegacyData(backup);
        Assert.Equal("ok", Scalar(backup, "PRAGMA integrity_check"));
        using var reopened = new ProfileStore(root);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "backups")));
    }
    [Fact]
    public void FailedMigrationRollsBackAndCanRetryWithAnIntactBackup()
    {
        using var db = Legacy();
        Assert.Throws<IOException>(() => DatabaseSchema.Initialize(db, root, () => throw new IOException("Interrupted before commit")));
        Assert.Equal(1L, Scalar(db, "PRAGMA user_version")); AssertLegacyData(db);
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE name='character_locations'"));
        DatabaseSchema.Initialize(db, root);
        Assert.Equal((long)DatabaseSchema.Current, Scalar(db, "PRAGMA user_version")); AssertLegacyData(db);
    }
    [Fact]
    public void BackupFailureLeavesSchemaAndCharactersUntouched()
    {
        using var db = Legacy(); File.WriteAllText(Path.Combine(root, "backups"), "occupied");
        Assert.Throws<IOException>(() => DatabaseSchema.Initialize(db, root));
        Assert.Equal(1L, Scalar(db, "PRAGMA user_version")); AssertLegacyData(db);
        File.Delete(Path.Combine(root, "backups")); DatabaseSchema.Initialize(db, root);
        Assert.Equal((long)DatabaseSchema.Current, Scalar(db, "PRAGMA user_version"));
    }
    [Fact]
    public void SchemaTwoMigrationPreservesLocationsAndRollsBackNewTablesOnFailure()
    {
        using var db = Legacy();
        Execute(db, """
            CREATE TABLE character_locations(character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
                map_id INTEGER NOT NULL,cell_id INTEGER NOT NULL,direction INTEGER NOT NULL,revision INTEGER NOT NULL);
            INSERT INTO character_locations VALUES(42,100,10,3,7);
            PRAGMA user_version=2;
            """);
        Assert.Throws<IOException>(() => DatabaseSchema.Initialize(db, root, () => throw new IOException("Interrupted")));
        Assert.Equal(2L, Scalar(db, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('character_wallets','combat_outcomes')"));
        Assert.Equal(7L, Scalar(db, "SELECT revision FROM character_locations WHERE character_id=42"));
        var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "*.db"));
        using (var backup = Open(backupPath))
        {
            Assert.Equal(2L, Scalar(backup, "PRAGMA user_version"));
            Assert.Equal(7L, Scalar(backup, "SELECT revision FROM character_locations WHERE character_id=42"));
        }
        DatabaseSchema.Initialize(db, root);
        Assert.Equal((long)DatabaseSchema.Current, Scalar(db, "PRAGMA user_version")); AssertLegacyData(db);
        Assert.Equal(7L, Scalar(db, "SELECT revision FROM character_locations WHERE character_id=42"));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM combat_outcomes"));
    }
    [Fact]
    public void SchemaThreeUpgradePreservesCommittedFightsAndBacksUpBeforeAddingRewards()
    {
        using (var profiles = new ProfileStore(root)) { }
        using var db = Open();
        Execute(db, """
            INSERT INTO profiles VALUES('11111111-1111-1111-1111-111111111111','Alpha','ALPHA',7);
            INSERT INTO characters VALUES(42,'11111111-1111-1111-1111-111111111111',290,'Hero','HERO','{}');
            INSERT INTO character_wallets VALUES(42,7);
            INSERT INTO combat_outcomes VALUES('fight',42,-2,100,10,1,'victory',7,100);
            DROP TABLE character_spell_bars; DROP TABLE character_allocations; DROP TABLE combat_awards; DROP TABLE character_items; DROP TABLE character_progression;
            PRAGMA user_version=3;
            """);
        Assert.Throws<IOException>(() => DatabaseSchema.Initialize(db, root, () => throw new IOException("Interrupted")));
        Assert.Equal(3L, Scalar(db, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE name='character_items'"));
        Assert.Equal(7L, Scalar(db, "SELECT balance FROM character_wallets"));
        var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "*.db"));
        using (var backup = Open(backupPath))
        {
            Assert.Equal(3L, Scalar(backup, "PRAGMA user_version"));
            Assert.Equal("victory", Scalar(backup, "SELECT status FROM combat_outcomes"));
        }
        DatabaseSchema.Initialize(db, root);
        Assert.Equal((long)DatabaseSchema.Current, Scalar(db, "PRAGMA user_version"));
        Assert.Equal(7L, Scalar(db, "SELECT balance FROM character_wallets"));
        Assert.Equal(7L, Scalar(db, "SELECT reward FROM combat_outcomes"));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM character_progression"));
    }
    [Fact]
    public void FutureSchemaIsRejectedWithoutChangingJournalOrContents()
    {
        using var db = Legacy(); Execute(db, "PRAGMA user_version=999; PRAGMA journal_mode=DELETE;");
        Assert.Throws<InvalidOperationException>(() => new ProfileStore(root));
        Assert.Equal("delete", Scalar(db, "PRAGMA journal_mode")); AssertLegacyData(db);
        Assert.Equal(999L, Scalar(db, "PRAGMA user_version")); Assert.False(Directory.Exists(Path.Combine(root, "backups")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
