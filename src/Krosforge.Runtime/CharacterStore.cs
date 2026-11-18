using System.Text.Json;
using Krosforge.Core;
using Microsoft.Data.Sqlite;
namespace Krosforge.Runtime;

internal sealed record CharacterAppearance(int Version, string Build, int Breed, int Gender, int Head, int Body, int[] Colors);
internal sealed record CharacterRecord(long Id, string Name, CharacterAppearance Appearance);
internal enum CharacterCreationResult { Success, Failure, NameUnavailable, Full }

// Every operation owns its connection. SQLite serializes durable writes across profiles.
internal sealed class CharacterStore(string directory)
{
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "krosforge.db"), ForeignKeys = true, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        try
        {
            db.Open();
            using var c = db.CreateCommand(); c.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;"; c.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    internal CharacterRecord[] List(Guid profile, int realm)
    {
        using var db = Open(); using var c = Scope(db, profile, realm);
        c.CommandText = "SELECT id,name,appearance FROM characters WHERE profile_id=$profile AND realm_id=$realm ORDER BY id";
        using var rows = c.ExecuteReader(); var result = new List<CharacterRecord>();
        while (rows.Read())
        {
            var appearance = JsonSerializer.Deserialize<CharacterAppearance>(rows.GetString(2)) ?? throw new InvalidDataException("Invalid character appearance.");
            if (appearance.Version != 1 || appearance.Build != ClientInstallation.SupportedBuild || appearance.Colors is not { Length: 6 }) throw new InvalidDataException("Unsupported character appearance.");
            result.Add(new(rows.GetInt64(0), rows.GetString(1), appearance));
        }
        return result.ToArray();
    }
    internal int Count(Guid profile, int realm)
    {
        using var db = Open(); using var c = Scope(db, profile, realm);
        c.CommandText = "SELECT COUNT(*) FROM characters WHERE profile_id=$profile AND realm_id=$realm";
        return Convert.ToInt32(c.ExecuteScalar());
    }
    internal bool Available(string name, int realm)
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM characters WHERE realm_id=$realm AND name_key=$key";
        c.Parameters.AddWithValue("$realm", realm); c.Parameters.AddWithValue("$key", CharacterName.Key(name));
        return Convert.ToInt64(c.ExecuteScalar()) == 0;
    }
    internal CharacterCreationResult Create(Guid profile, int realm, string name, CharacterAppearance appearance)
        => Create(profile, realm, name, appearance, out _);
    internal CharacterCreationResult Create(Guid profile, int realm, string name, CharacterAppearance appearance, out long createdId)
    {
        createdId = 0;
        name = CharacterName.Normalize(name);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var c = Scope(db, profile, realm); c.Transaction = tx;
        c.CommandText = "SELECT COUNT(*) FROM characters WHERE profile_id=$profile AND realm_id=$realm";
        if (Convert.ToInt64(c.ExecuteScalar()) >= 5) return CharacterCreationResult.Full;
        c.CommandText = "INSERT INTO characters(profile_id,realm_id,name,name_key,appearance) VALUES($profile,$realm,$name,$key,$appearance) RETURNING id";
        c.Parameters.AddWithValue("$name", name); c.Parameters.AddWithValue("$key", CharacterName.Key(name)); c.Parameters.AddWithValue("$appearance", JsonSerializer.Serialize(appearance));
        try { var id = Convert.ToInt64(c.ExecuteScalar()); tx.Commit(); createdId = id; return CharacterCreationResult.Success; }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == 2067) { return CharacterCreationResult.NameUnavailable; }
    }
    internal bool Delete(Guid profile, int realm, long id)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var c = Scope(db, profile, realm); c.Transaction = tx;
        c.CommandText = "DELETE FROM characters WHERE id=$id AND profile_id=$profile AND realm_id=$realm";
        c.Parameters.AddWithValue("$id", id); var deleted = c.ExecuteNonQuery() == 1; tx.Commit(); return deleted;
    }
    private static SqliteCommand Scope(SqliteConnection db, Guid profile, int realm)
    {
        var c = db.CreateCommand(); c.Parameters.AddWithValue("$profile", profile.ToString()); c.Parameters.AddWithValue("$realm", realm); return c;
    }
}
