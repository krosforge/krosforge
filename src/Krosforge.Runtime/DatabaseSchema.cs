using Microsoft.Data.Sqlite;
namespace Krosforge.Runtime;

internal static class DatabaseSchema
{
    internal const int Current = 4;

    internal static void Initialize(SqliteConnection db, string directory, Action? beforeCommit = null)
    {
        // Reject future databases before changing journal settings or starting migration.
        if (Version(db) > Current) throw new InvalidOperationException("ErrorDatabaseVersion");
        Execute(db, "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        using var transaction = db.BeginTransaction(deferred: false);
        var version = Version(db, transaction);
        if (version > Current) throw new InvalidOperationException("ErrorDatabaseVersion");
        if (version == Current) { transaction.Commit(); return; }

        // The writer lock excludes concurrent commits. A separate read connection
        // sees the last committed WAL snapshot without backing up an active writer.
        if (version > 0) Backup(directory, version);
        if (version == 0)
            Execute(db, """
                CREATE TABLE profiles(id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE, slot INTEGER NOT NULL UNIQUE);
                CREATE TABLE counters(name TEXT PRIMARY KEY, value INTEGER NOT NULL);
                INSERT INTO counters VALUES('profile',0);
                CREATE TABLE characters(id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, realm_id INTEGER NOT NULL, name TEXT NOT NULL, name_key TEXT NOT NULL, appearance TEXT NOT NULL, UNIQUE(realm_id,name_key));
                CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE cleanup(profile_id TEXT PRIMARY KEY);
                """, transaction);
        if (version < 2) Execute(db, """
            CREATE TABLE character_locations(
                character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
                map_id INTEGER NOT NULL CHECK(map_id>0),
                cell_id INTEGER NOT NULL CHECK(cell_id BETWEEN 0 AND 559),
                direction INTEGER NOT NULL CHECK(direction BETWEEN 0 AND 7),
                revision INTEGER NOT NULL CHECK(revision>0));
            PRAGMA user_version=2;
            """, transaction);
        if (version < 3) Execute(db, """
            CREATE TABLE character_wallets(
                character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
                balance INTEGER NOT NULL CHECK(typeof(balance)='integer' AND balance>=0));
            CREATE TABLE combat_outcomes(
                id TEXT PRIMARY KEY,
                character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
                encounter_id INTEGER NOT NULL,
                map_id INTEGER NOT NULL,
                cell_id INTEGER NOT NULL,
                location_revision INTEGER NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('active','victory','defeat','abandoned','interrupted')),
                reward INTEGER NOT NULL CHECK(typeof(reward)='integer' AND reward>=0),
                duration_ms INTEGER NOT NULL CHECK(duration_ms>=0));
            CREATE UNIQUE INDEX combat_active_character ON combat_outcomes(character_id) WHERE status='active';
            PRAGMA user_version=3;
            """, transaction);
        Execute(db, """
            CREATE TABLE character_progression(
                character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
                experience INTEGER NOT NULL CHECK(typeof(experience)='integer' AND experience>=0));
            CREATE TABLE character_items(
                id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 2147483647),
                character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
                template_id INTEGER NOT NULL CHECK(template_id>0),
                quantity INTEGER NOT NULL CHECK(typeof(quantity)='integer' AND quantity BETWEEN 1 AND 2147483647),
                effects TEXT NOT NULL,
                UNIQUE(character_id,template_id,effects));
            CREATE TABLE combat_awards(
                outcome_id TEXT PRIMARY KEY REFERENCES combat_outcomes(id) ON DELETE CASCADE,
                experience INTEGER NOT NULL CHECK(typeof(experience)='integer' AND experience>=0),
                items TEXT NOT NULL);
            PRAGMA user_version=4;
            """, transaction);
        beforeCommit?.Invoke();
        transaction.Commit();
    }

    private static void Backup(string directory, long version)
    {
        var folder = Path.Combine(directory, "backups");
        SafePaths.CheckTree(directory);
        Directory.CreateDirectory(folder);
        SafePaths.CheckTree(folder);
        var destination = Path.Combine(folder, $"schema-{version}-before-{Current}-" + Guid.NewGuid().ToString("N") + ".db");
        var temporary = destination + ".tmp";
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "krosforge.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = temporary, Pooling = false }.ToString()))
            {
                source.Open(); target.Open();
                source.BackupDatabase(target);
                Execute(target, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;");
                using var integrity = target.CreateCommand(); integrity.CommandText = "PRAGMA integrity_check";
                if (!Equals(integrity.ExecuteScalar(), "ok") || Version(target) != version)
                    throw new IOException("Database backup verification failed.");
            }
            using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                file.Flush(flushToDisk: true);
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static long Version(SqliteConnection db, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version"; return Convert.ToInt64(command.ExecuteScalar());
    }
    private static void Execute(SqliteConnection db, string sql, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = sql; command.ExecuteNonQuery();
    }
}
