using Microsoft.Data.Sqlite;
using Krosforge.Contracts;
using Krosforge.Core;
namespace Krosforge.Runtime;

public sealed class ProfileStore : IDisposable
{
    private readonly SqliteConnection db;
    public ProfileStore(string directory)
    {
        Directory.CreateDirectory(directory);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "krosforge.db"), ForeignKeys = true, Pooling = false }.ToString());
        db.Open();
        Execute("PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        using var version = db.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt64(version.ExecuteScalar());
        if (current > 1)
        {
            db.Dispose();
            throw new InvalidOperationException("ErrorDatabaseVersion");
        }
        Execute("""
            BEGIN IMMEDIATE;
            CREATE TABLE IF NOT EXISTS profiles(id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE, slot INTEGER NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS counters(name TEXT PRIMARY KEY, value INTEGER NOT NULL);
            INSERT OR IGNORE INTO counters VALUES('profile',0);
            CREATE TABLE IF NOT EXISTS characters(id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, realm_id INTEGER NOT NULL, name TEXT NOT NULL, name_key TEXT NOT NULL, appearance TEXT NOT NULL, UNIQUE(realm_id,name_key));
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cleanup(profile_id TEXT PRIMARY KEY);
            PRAGMA user_version=1;
            COMMIT;
            """);
    }
    private void Execute(string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }
    public Profile[] List()
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT id,name,slot FROM profiles ORDER BY slot";
        using var rows = c.ExecuteReader();
        var result = new List<Profile>();
        while (rows.Read())
            result.Add(new(Guid.Parse(rows.GetString(0)), rows.GetString(1), rows.GetInt64(2)));
        return result.ToArray();
    }
    public Profile Create(string name)
    {
        name = ProfileName.Normalize(name);
        var id = Guid.NewGuid();
        using var tx = db.BeginTransaction(deferred: false);
        using var count = db.CreateCommand();
        count.Transaction = tx;
        count.CommandText = "UPDATE counters SET value=value+1 WHERE name='profile' RETURNING value";
        var slot = Convert.ToInt64(count.ExecuteScalar());
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "INSERT INTO profiles VALUES($id,$name,$key,$slot)";
        c.Parameters.AddWithValue("$id", id.ToString());
        c.Parameters.AddWithValue("$name", name);
        c.Parameters.AddWithValue("$key", ProfileName.Key(name));
        c.Parameters.AddWithValue("$slot", slot);
        try
        {
            c.ExecuteNonQuery();
            tx.Commit();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new ArgumentException("ErrorDuplicateProfile"); }
        return new(id, name, slot);
    }
    public void Rename(Guid id, string name)
    {
        name = ProfileName.Normalize(name);
        using var c = db.CreateCommand();
        c.CommandText = "UPDATE profiles SET name=$name,name_key=$key WHERE id=$id";
        c.Parameters.AddWithValue("$name", name);
        c.Parameters.AddWithValue("$key", ProfileName.Key(name));
        c.Parameters.AddWithValue("$id", id.ToString());
        try
        {
            if (c.ExecuteNonQuery() != 1)
                throw new ArgumentException("ErrorProfileNotFound");
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new ArgumentException("ErrorDuplicateProfile"); }
    }
    public void Delete(Guid id, string confirmation)
    {
        using var tx = db.BeginTransaction(deferred: false);
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT name FROM profiles WHERE id=$id";
        c.Parameters.AddWithValue("$id", id.ToString());
        var name = c.ExecuteScalar() as string ?? throw new ArgumentException("ErrorProfileNotFound");
        if (!string.Equals(name, confirmation, StringComparison.Ordinal))
            throw new ArgumentException("ErrorDeleteConfirmation");
        c.CommandText = "INSERT OR IGNORE INTO cleanup VALUES($id); DELETE FROM profiles WHERE id=$id";
        c.ExecuteNonQuery();
        tx.Commit();
    }
    public Guid[] PendingCleanup()
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT profile_id FROM cleanup";
        using var r = c.ExecuteReader();
        var result = new List<Guid>();
        while (r.Read())
            result.Add(Guid.Parse(r.GetString(0)));
        return result.ToArray();
    }
    public void CompleteCleanup(Guid id)
    {
        using var c = db.CreateCommand();
        c.CommandText = "DELETE FROM cleanup WHERE profile_id=$id";
        c.Parameters.AddWithValue("$id", id.ToString());
        c.ExecuteNonQuery();
    }
    public string? Setting(string key)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT value FROM settings WHERE key=$key";
        c.Parameters.AddWithValue("$key", key);
        return c.ExecuteScalar() as string;
    }
    public void SetSetting(string key, string value)
    {
        using var c = db.CreateCommand();
        c.CommandText = "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        c.Parameters.AddWithValue("$key", key);
        c.Parameters.AddWithValue("$value", value);
        c.ExecuteNonQuery();
    }
    public void Dispose() => db.Dispose();
}
