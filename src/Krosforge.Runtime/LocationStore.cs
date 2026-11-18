using Krosforge.Core;
using Microsoft.Data.Sqlite;
namespace Krosforge.Runtime;

internal sealed class LocationStore(string directory)
{
    // Initial position is committed once. A retry reads the same row, including
    // after a successful commit whose network response could not be delivered.
    internal WorldLocation? LoadOrCreate(Guid profile, int realm, long character, WorldLocation initial, Action<WorldLocation> validate)
    {
        initial.Validate(); validate(initial);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT COUNT(*) FROM characters WHERE id=$id AND profile_id=$profile AND realm_id=$realm";
        command.Parameters.AddWithValue("$id", character); command.Parameters.AddWithValue("$profile", profile.ToString()); command.Parameters.AddWithValue("$realm", realm);
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) return null;
        command.CommandText = "SELECT map_id,cell_id,direction,revision FROM character_locations WHERE character_id=$id";
        WorldLocation location;
        using (var row = command.ExecuteReader())
            location = row.Read() ? new(row.GetInt64(0), row.GetInt32(1), row.GetInt32(2), row.GetInt64(3)) : initial;
        location.Validate(); validate(location);
        command.CommandText = "INSERT OR IGNORE INTO character_locations VALUES($id,$map,$cell,$direction,$revision)";
        command.Parameters.AddWithValue("$map", location.MapId); command.Parameters.AddWithValue("$cell", location.CellId);
        command.Parameters.AddWithValue("$direction", location.Direction); command.Parameters.AddWithValue("$revision", location.Revision);
        command.ExecuteNonQuery(); tx.Commit(); return location;
    }
    internal void Save(Guid profile, int realm, long character, WorldLocation expected, WorldLocation next, Action<WorldLocation> validate,
        Action? beforeCommit = null)
    {
        expected.Validate(); next.Validate(); validate(next);
        if (next.Revision != checked(expected.Revision + 1)) throw new InvalidDataException("Invalid location revision.");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            UPDATE character_locations SET map_id=$map,cell_id=$cell,direction=$direction,revision=$revision
            WHERE character_id=$id AND map_id=$previousMap AND cell_id=$previousCell
              AND direction=$previousDirection AND revision=$previousRevision
              AND EXISTS(SELECT 1 FROM characters WHERE id=$id AND profile_id=$profile AND realm_id=$realm)
            """;
        command.Parameters.AddWithValue("$id", character); command.Parameters.AddWithValue("$profile", profile.ToString()); command.Parameters.AddWithValue("$realm", realm);
        command.Parameters.AddWithValue("$map", next.MapId); command.Parameters.AddWithValue("$cell", next.CellId);
        command.Parameters.AddWithValue("$direction", next.Direction); command.Parameters.AddWithValue("$revision", next.Revision);
        command.Parameters.AddWithValue("$previousMap", expected.MapId); command.Parameters.AddWithValue("$previousCell", expected.CellId);
        command.Parameters.AddWithValue("$previousDirection", expected.Direction); command.Parameters.AddWithValue("$previousRevision", expected.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Location ownership or revision changed.");
        beforeCommit?.Invoke(); tx.Commit();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "krosforge.db"), Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, Pooling = false }.ToString());
        try
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;"; command.ExecuteNonQuery(); return db;
        }
        catch { db.Dispose(); throw; }
    }
}
