using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Krosforge.Runtime;

internal readonly record struct SpellShortcut(int Slot, int Spell);

internal sealed partial class CombatOutcomeStore
{
    private static SpellShortcut[]? ReadSpellBar(SqliteCommand c)
    {
        c.CommandText = "SELECT layout FROM character_spell_bars WHERE character_id=$character";
        if (c.ExecuteScalar() is not string layout) return null;
        var shortcuts = JsonSerializer.Deserialize<SpellShortcut[]>(layout);
        return shortcuts is not null && ValidSpellBar(shortcuts) ? shortcuts : throw new InvalidDataException("Invalid saved spell bar.");
    }

    internal void SaveSpellBar(Guid profile, int realm, long character, SpellShortcut[] shortcuts)
    {
        if (!ValidSpellBar(shortcuts)) throw new ArgumentException("Invalid spell bar.", nameof(shortcuts));
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var c = Owned(db, tx, profile, realm, character);
        c.Parameters.AddWithValue("$layout", JsonSerializer.Serialize(shortcuts));
        c.CommandText = """
            INSERT INTO character_spell_bars VALUES($character,$layout)
            ON CONFLICT(character_id) DO UPDATE SET layout=excluded.layout;
            """;
        c.ExecuteNonQuery(); tx.Commit();
    }

    private static bool ValidSpellBar(SpellShortcut[] shortcuts) => shortcuts.Length <= 256 &&
        shortcuts.All(x => x.Slot is >= 0 and <= short.MaxValue && x.Spell >= 0) &&
        shortcuts.Select(x => x.Slot).Distinct().Count() == shortcuts.Length;
}
