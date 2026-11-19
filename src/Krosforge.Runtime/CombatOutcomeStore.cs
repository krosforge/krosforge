using Krosforge.Core;
using Microsoft.Data.Sqlite;
namespace Krosforge.Runtime;

internal sealed record CombatReceipt(Guid Id, string Status, long Reward, long Balance, long DurationMilliseconds)
{
    internal CombatAward Award { get; init; } = new(0, []);
    internal CharacterRewards State { get; init; } = new(0, []);
}

// A fight identity survives retries. Outcome and currency become durable in the same transaction.
internal sealed partial class CombatOutcomeStore(string directory)
{
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "krosforge.db"), Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, Pooling = false }.ToString());
        try
        {
            db.Open(); using var c = db.CreateCommand();
            c.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;"; c.ExecuteNonQuery(); return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static SqliteCommand Owned(SqliteConnection db, SqliteTransaction tx, Guid profile, int realm, long character)
    {
        var c = db.CreateCommand(); c.Transaction = tx;
        c.Parameters.AddWithValue("$character", character); c.Parameters.AddWithValue("$profile", profile.ToString()); c.Parameters.AddWithValue("$realm", realm);
        c.CommandText = "SELECT COUNT(*) FROM characters WHERE id=$character AND profile_id=$profile AND realm_id=$realm";
        if (Convert.ToInt64(c.ExecuteScalar()) != 1) { c.Dispose(); throw new InvalidDataException("Combat character ownership changed."); }
        c.CommandText = "INSERT OR IGNORE INTO character_wallets VALUES($character,0)"; c.ExecuteNonQuery();
        c.CommandText = "INSERT OR IGNORE INTO character_progression VALUES($character,0)"; c.ExecuteNonQuery();
        return c;
    }
    private static long Balance(SqliteCommand c)
    { c.CommandText = "SELECT balance FROM character_wallets WHERE character_id=$character"; return Convert.ToInt64(c.ExecuteScalar()); }
    internal long Recover(Guid profile, int realm, long character)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var c = Owned(db, tx, profile, realm, character);
        // Only the coordinator admitting a new selection calls this. An unfinished fight earns nothing.
        c.CommandText = "UPDATE combat_outcomes SET status='interrupted' WHERE character_id=$character AND status='active'"; c.ExecuteNonQuery();
        var balance = Balance(c); tx.Commit(); return balance;
    }
    internal Guid Begin(Guid profile, int realm, long character, long encounter, WorldLocation location)
    {
        location.Validate();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var c = Owned(db, tx, profile, realm, character);
        c.Parameters.AddWithValue("$map", location.MapId); c.Parameters.AddWithValue("$cell", location.CellId); c.Parameters.AddWithValue("$revision", location.Revision);
        c.CommandText = "SELECT COUNT(*) FROM character_locations WHERE character_id=$character AND map_id=$map AND cell_id=$cell AND revision=$revision";
        if (Convert.ToInt64(c.ExecuteScalar()) != 1) throw new InvalidDataException("Combat return location changed.");
        var id = Guid.NewGuid(); c.Parameters.AddWithValue("$id", id.ToString()); c.Parameters.AddWithValue("$encounter", encounter);
        c.CommandText = "INSERT INTO combat_outcomes VALUES($id,$character,$encounter,$map,$cell,$revision,'active',0,0)";
        c.ExecuteNonQuery(); tx.Commit(); return id;
    }
    internal CombatReceipt Complete(Guid profile, int realm, long character, Guid id, string status, long reward, long durationMilliseconds,
        Action? beforeCommit = null, CombatAward? award = null)
    {
        award = NormalizeAward(award ?? new(0, []));
        if (id == Guid.Empty || status is not ("victory" or "defeat" or "abandoned" or "interrupted") || reward < 0 ||
            status != "victory" && (reward != 0 || award.Experience != 0 || award.Items.Length != 0) || durationMilliseconds < 0) throw new ArgumentException("Invalid combat outcome.");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var c = Owned(db, tx, profile, realm, character);
        c.Parameters.AddWithValue("$id", id.ToString());
        c.CommandText = "SELECT status,reward,duration_ms FROM combat_outcomes WHERE id=$id AND character_id=$character";
        string previous; long previousReward, previousDuration;
        using (var row = c.ExecuteReader())
        {
            if (!row.Read()) throw new InvalidDataException("Combat identity does not belong to this character.");
            previous = row.GetString(0); previousReward = row.GetInt64(1); previousDuration = row.GetInt64(2);
        }
        var balance = Balance(c);
        if (previous != "active")
        {
            var committed = ReadAward(c);
            // Cleanup may follow a successful commit whose publication failed. Never replace its result.
            if (status != "interrupted" && (previous != status || previousReward != reward || previousDuration != durationMilliseconds || !SameAward(committed, award)))
                throw new InvalidDataException("Combat outcome already committed with different values.");
            var recovered = ReadState(c); tx.Commit();
            return new(id, previous, previousReward, balance, previousDuration) { Award = committed, State = recovered };
        }
        balance = checked(balance + reward);
        c.Parameters.AddWithValue("$balance", balance); c.Parameters.AddWithValue("$status", status);
        c.Parameters.AddWithValue("$reward", reward); c.Parameters.AddWithValue("$duration", durationMilliseconds);
        c.CommandText = "UPDATE character_wallets SET balance=$balance WHERE character_id=$character; UPDATE combat_outcomes SET status=$status,reward=$reward,duration_ms=$duration WHERE id=$id AND character_id=$character";
        c.ExecuteNonQuery();
        ApplyAward(c, award);
        var state = ReadState(c);
        beforeCommit?.Invoke(); tx.Commit();
        return new(id, status, reward, balance, durationMilliseconds) { Award = award, State = state };
    }
}
