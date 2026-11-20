using System.Text.Json;
using System.Text.Json.Serialization;
using Krosforge.Core;
using Microsoft.Data.Sqlite;
namespace Krosforge.Runtime;

internal sealed record ItemAward(int Template, int Quantity, string Effects = "[]");
internal sealed record InventoryItem(long Id, int Template, int Quantity, string Effects);
internal sealed record CombatAward(long Experience, ItemAward[] Items);
internal sealed record CharacterRewards(long Experience, InventoryItem[] Items)
{
    internal CharacteristicPoints Allocation { get; init; } = CharacteristicPoints.Empty;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InventoryEffect(int Action, int Minimum, int Maximum, int Constant);

internal sealed partial class CombatOutcomeStore
{
    internal CharacterRewards Read(Guid profile, int realm, long character)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var c = Owned(db, tx, profile, realm, character);
        var state = ReadState(c); tx.Commit(); return state;
    }
    private static CombatAward NormalizeAward(CombatAward award)
    {
        if (award.Experience < 0 || award.Items is null || award.Items.Length > 4096)
            throw new ArgumentException("Invalid combat award.");
        var items = award.Items.Select(item =>
        {
            if (item is null || item.Template <= 0 || item.Quantity <= 0 || item.Effects is null || item.Effects.Length > 65536)
                throw new ArgumentException("Invalid combat item award.");
            var effects = JsonSerializer.Deserialize<InventoryEffect[]>(item.Effects) ?? throw new ArgumentException("Invalid item effects.");
            if (effects.Length > 128 || effects.Any(e => e is null || e.Action <= 0 || e.Maximum != 0 && e.Maximum < e.Minimum))
                throw new ArgumentException("Invalid item effects.");
            return item with { Effects = JsonSerializer.Serialize(effects) };
        }).GroupBy(item => (item.Template, item.Effects))
            .Select(group => new ItemAward(group.Key.Template, group.Aggregate(0, (total, item) => checked(total + item.Quantity)), group.Key.Effects))
            .OrderBy(item => item.Template).ThenBy(item => item.Effects, StringComparer.Ordinal).ToArray();
        return new(award.Experience, items);
    }
    private static bool SameAward(CombatAward a, CombatAward b) => a.Experience == b.Experience && a.Items.SequenceEqual(b.Items);
    private static CombatAward ReadAward(SqliteCommand c)
    {
        c.CommandText = "SELECT experience,items FROM combat_awards WHERE outcome_id=$id";
        using var row = c.ExecuteReader();
        return row.Read() ? new(row.GetInt64(0), JsonSerializer.Deserialize<ItemAward[]>(row.GetString(1))
            ?? throw new InvalidDataException("Invalid committed item award.")) : new(0, []);
    }
    private static CharacterRewards ReadState(SqliteCommand c)
    {
        c.CommandText = "SELECT experience FROM character_progression WHERE character_id=$character";
        var experience = Convert.ToInt64(c.ExecuteScalar());
        c.CommandText = "SELECT id,template_id,quantity,effects FROM character_items WHERE character_id=$character ORDER BY id";
        var items = new List<InventoryItem>();
        using (var rows = c.ExecuteReader())
            while (rows.Read()) items.Add(new(rows.GetInt64(0), rows.GetInt32(1), rows.GetInt32(2), rows.GetString(3)));
        c.CommandText = "SELECT vitality,chance,agility,intelligence,strength,wisdom FROM character_allocations WHERE character_id=$character";
        using var allocation = c.ExecuteReader();
        return new(experience, items.ToArray())
        {
            Allocation = allocation.Read()
                ? new(allocation.GetInt32(0), allocation.GetInt32(1), allocation.GetInt32(2), allocation.GetInt32(3), allocation.GetInt32(4), allocation.GetInt32(5))
                : CharacteristicPoints.Empty
        };
    }
    internal CharacterRewards Redistribute(Guid profile, int realm, long character, ExperienceCurve curve, CharacteristicPoints points)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var c = Owned(db, tx, profile, realm, character);
        var state = ReadState(c);
        if (!points.Fits(curve.At(state.Experience).Level) || state.Allocation == points) return state;
        c.Parameters.AddWithValue("$vitality", points.Vitality);
        c.Parameters.AddWithValue("$chance", points.Chance);
        c.Parameters.AddWithValue("$agility", points.Agility);
        c.Parameters.AddWithValue("$intelligence", points.Intelligence);
        c.Parameters.AddWithValue("$strength", points.Strength);
        c.Parameters.AddWithValue("$wisdom", points.Wisdom);
        c.CommandText = """
            INSERT INTO character_allocations VALUES($character,$vitality,$chance,$agility,$intelligence,$strength,$wisdom)
            ON CONFLICT(character_id) DO UPDATE SET vitality=excluded.vitality, chance=excluded.chance,
                agility=excluded.agility, intelligence=excluded.intelligence, strength=excluded.strength, wisdom=excluded.wisdom;
            """;
        c.ExecuteNonQuery(); tx.Commit();
        return state with { Allocation = points };
    }
    private static void ApplyAward(SqliteCommand c, CombatAward award)
    {
        c.CommandText = "SELECT experience FROM character_progression WHERE character_id=$character";
        var total = checked(Convert.ToInt64(c.ExecuteScalar()) + award.Experience);
        c.Parameters.AddWithValue("$experience", total);
        c.CommandText = "UPDATE character_progression SET experience=$experience WHERE character_id=$character";
        c.ExecuteNonQuery();
        c.Parameters.AddWithValue("$template", 0); c.Parameters.AddWithValue("$quantity", 0); c.Parameters.AddWithValue("$effects", "");
        foreach (var item in award.Items)
        {
            c.Parameters["$template"].Value = item.Template; c.Parameters["$quantity"].Value = item.Quantity; c.Parameters["$effects"].Value = item.Effects;
            c.CommandText = """
                INSERT INTO character_items(character_id,template_id,quantity,effects) VALUES($character,$template,$quantity,$effects)
                ON CONFLICT(character_id,template_id,effects) DO UPDATE SET quantity=quantity+excluded.quantity;
                """;
            c.ExecuteNonQuery();
        }
        c.Parameters.AddWithValue("$earned", award.Experience); c.Parameters.AddWithValue("$items", JsonSerializer.Serialize(award.Items));
        c.CommandText = "INSERT INTO combat_awards VALUES($id,$earned,$items)"; c.ExecuteNonQuery();
    }
}
