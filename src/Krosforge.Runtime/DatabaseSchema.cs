using Microsoft.Data.Sqlite;
namespace Krosforge.Runtime;

internal static class DatabaseSchema
{
    internal const int Current = 2;

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
        if (version == 1) Backup(directory);
        if (version == 0)
            Execute(db, """
                CREATE TABLE profiles(id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE, slot INTEGER NOT NULL UNIQUE);
                CREATE TABLE counters(name TEXT PRIMARY KEY, value INTEGER NOT NULL);
                INSERT INTO counters VALUES('profile',0);
                CREATE TABLE characters(id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, realm_id INTEGER NOT NULL, name TEXT NOT NULL, name_key TEXT NOT NULL, appearance TEXT NOT NULL, UNIQUE(realm_id,name_key));
                CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE cleanup(profile_id TEXT PRIMARY KEY);
                """, transaction);
        Execute(db, """
            CREATE TABLE character_locations(
                character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
                map_id INTEGER NOT NULL CHECK(map_id>0),
                cell_id INTEGER NOT NULL CHECK(cell_id BETWEEN 0 AND 559),
                direction INTEGER NOT NULL CHECK(direction BETWEEN 0 AND 7),
                revision INTEGER NOT NULL CHECK(revision>0));
            PRAGMA user_version=2;
            """, transaction);
        beforeCommit?.Invoke();
        transaction.Commit();
    }

    private static void Backup(string directory)
    {
        var folder = Path.Combine(directory, "backups");
        SafePaths.CheckTree(directory);
        Directory.CreateDirectory(folder);
        SafePaths.CheckTree(folder);
        var destination = Path.Combine(folder, "schema-1-before-2-" + Guid.NewGuid().ToString("N") + ".db");
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
                if (!Equals(integrity.ExecuteScalar(), "ok") || Version(target) != 1)
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
