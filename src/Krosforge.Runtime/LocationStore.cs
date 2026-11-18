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
