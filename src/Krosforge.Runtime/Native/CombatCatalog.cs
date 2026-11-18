using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record CombatMonster(int Definition, int Grade, int Level, int Life, int ActionPoints, int MovementPoints, EntityLookWire Look)
{
    internal long Experience { get; init; }
    internal CombatStatistics Statistics { get; init; } = CombatStatistics.Empty;
    internal ImmutableArray<int> Spells { get; init; } = [];
    internal ImmutableDictionary<int, int> SpellGrades { get; init; } = ImmutableDictionary<int, int>.Empty;
}
internal sealed record CombatReward(long Currency, bool RestoreFullLife);
internal sealed record CombatOpponent(long Actor, int Cell, CombatMonster Monster);
internal sealed record CombatEncounter(long Map, long GroupId, int GroupCell, int Direction, int AttackDistance,
    int FightType, int PlayerCell, int MonsterCell, CombatMonster Monster, CombatBoard Board)
{
    internal CombatReward Reward { get; init; } = new(0, true);
    internal ImmutableArray<CombatOpponent> Companions { get; init; } = [];
    internal IEnumerable<CombatOpponent> Opponents => new[] { new CombatOpponent(-1, MonsterCell, Monster) }.Concat(Companions);
    internal CombatMonster MonsterFor(long actor) => Opponents.Single(opponent => opponent.Actor == actor).Monster;
}

internal sealed class CombatCatalog
{
    private sealed record Source(string Path, string Sha256);
    private sealed record Companion(int Cell, int MonsterId, int Grade);
    private sealed record Definition(long Map, long GroupId, int GroupCell, int Direction, int AttackDistance,
        int FightType, int PlayerCell, int MonsterCell, int MonsterId, int Grade, CombatReward Reward, Companion[]? Companions = null);
    private sealed record Configuration(string Build, Source Monsters, Source Calendar, Definition[] Encounters);
    internal IReadOnlyList<CombatEncounter> Encounters { get; }
    internal int? NeutralCalendar { get; }
    internal CombatCatalog(string root, WorldEntryCatalog world)
    {
        using var resource = typeof(CombatCatalog).Assembly.GetManifestResourceStream("Krosforge.Runtime.Native.combat-encounters.json")
            ?? throw new InvalidDataException("Combat configuration missing.");
        var config = JsonSerializer.Deserialize<Configuration>(resource) ?? throw new InvalidDataException("Invalid combat configuration.");
        if (config.Build != ClientInstallation.SupportedBuild) throw new InvalidDataException("Combat build mismatch.");
        // The native monster tooltip reads calendar bonuses even when the local world has none.
        // Select an installed record with no bonuses instead of inventing a calendar identity.
        NeutralCalendar = CreationDataReader.Read(Path.Combine(root, config.Calendar.Path), config.Calendar.Sha256)
            .Where(row => ((object[])((Dictionary<string, object>)row["bonusesIds"]).Single().Value).Length == 0)
            .Select(row => Number(row, "id")).Where(id => id > 0).Order().Cast<int?>().FirstOrDefault()
            ?? throw new InvalidDataException("No neutral calendar presentation is available.");
        var monsters = CreationDataReader.Read(Path.Combine(root, config.Monsters.Path), config.Monsters.Sha256, maxOutput: 16777216,
                selectedKeys: config.Encounters.SelectMany(e => new[] { e.MonsterId }.Concat((e.Companions ?? []).Select(c => c.MonsterId)))
                    .Select(id => (long)id).ToHashSet())
            .ToDictionary(row => Number(row, "id"));
        CombatMonster ReadMonster(int id, int gradeId)
        {
            var monster = monsters[id];
            var grade = Rows(monster, "grades").Single(row => Number(row, "grade") == gradeId);
            var spellIds = ((object[])((Dictionary<string, object>)monster["spells"]).Single().Value)
                .Select(value => Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToArray();
            var spellGrades = ((object[])((Dictionary<string, object>)monster["spellGrades"]).Single().Value).Cast<string>().ToArray();
            return new(id, gradeId, Number(grade, "level"), Number(grade, "lifePoints"),
                Number(grade, "actionPoints"), Number(grade, "movementPoints"), ParseLook((string)monster["look"]))
            {
                Experience = Convert.ToInt64(grade["xp"], CultureInfo.InvariantCulture),
                Statistics = CombatSpellCatalog.Statistics(grade),
                Spells = spellIds.ToImmutableArray(),
                SpellGrades = ReadSpellGrades(spellIds, spellGrades, Number(grade, "level"))
            };
        }
        Encounters = config.Encounters.Select(definition =>
        {
            var map = world.Map(definition.Map);
            var board = map.Combat ?? throw new InvalidDataException("Combat geometry missing.");
            return new CombatEncounter(map.Id, definition.GroupId, definition.GroupCell, definition.Direction, definition.AttackDistance,
                definition.FightType, definition.PlayerCell, definition.MonsterCell,
                ReadMonster(definition.MonsterId, definition.Grade), board)
            {
                Reward = definition.Reward,
                Companions = (definition.Companions ?? []).Select((companion, index) =>
                    new CombatOpponent(-2L - index, companion.Cell, ReadMonster(companion.MonsterId, companion.Grade))).ToImmutableArray()
            };
        }).ToArray();
        Validate(world);
    }
    internal CombatCatalog(WorldEntryCatalog world, params CombatEncounter[] encounters) { Encounters = encounters; Validate(world); }
    private void Validate(WorldEntryCatalog world)
    {
        if (Encounters.Select(e => e.GroupId).Distinct().Count() != Encounters.Count) throw new InvalidDataException("Duplicate encounter identity.");
        foreach (var e in Encounters)
        {
            if (e.GroupId >= -1 || e.Direction is < 0 or > 7 || e.AttackDistance is < 1 or > 560 || e.FightType is < 0 or > 10 ||
                !world.Map(e.Map).WalkableCells.Contains(e.GroupCell) || e.Companions.IsDefault || e.Companions.Length > 7 ||
                e.Opponents.Select(m => m.Actor).Distinct().Count() != e.Companions.Length + 1 ||
                e.Opponents.Any(m => m.Actor >= 0 || m.Monster.Definition <= 0 || m.Monster.Grade <= 0 ||
                    m.Monster.Level <= 0 || m.Monster.Life <= 0 || m.Monster.ActionPoints <= 0 || m.Monster.MovementPoints < 0) ||
                e.Reward is null || e.Reward.Currency < 0 || !e.Reward.RestoreFullLife)
                throw new InvalidDataException("Invalid encounter configuration.");
            _ = new CombatPreparation(e.Board, new[] { new CombatPlacement(1, 0, e.PlayerCell, true) }
                .Concat(e.Opponents.Select(m => new CombatPlacement(m.Actor, 1, m.Cell, false, true))));
        }
    }
    private static int Number(Dictionary<string, object> row, string key) => Convert.ToInt32(row[key], CultureInfo.InvariantCulture);
    internal static ImmutableDictionary<int, int> ReadSpellGrades(int[] spells, string[] definitions, int level)
    {
        if (spells.Length != definitions.Length || spells.Distinct().Count() != spells.Length || level <= 0)
            throw new InvalidDataException("Invalid monster spell grades.");
        return spells.Select((spell, index) =>
        {
            var choices = definitions[index].Split(';').Select(pair =>
            {
                var parts = pair.Split(',');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var grade) ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var monsterLevel) || grade <= 0 || monsterLevel <= 0)
                    throw new InvalidDataException("Unsupported monster spell grade mapping.");
                return (Grade: grade, Level: monsterLevel);
            }).ToArray();
            if (choices.Select(choice => choice.Level).Distinct().Count() != choices.Length || choices.Count(choice => choice.Level == level) != 1)
                throw new InvalidDataException("Monster spell grade missing or ambiguous.");
            return new KeyValuePair<int, int>(spell, choices.Single(choice => choice.Level == level).Grade);
        }).ToImmutableDictionary();
    }
    private static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> row, string key) =>
        ((object[])((Dictionary<string, object>)row[key]).Single().Value).Cast<Dictionary<string, object>>();
    private static EntityLookWire ParseLook(string value)
    {
        if (!value.StartsWith('{') || !value.EndsWith('}')) throw new InvalidDataException("Invalid monster appearance.");
        var fields = value[1..^1].Split('|');
        if (fields.Length > 4) throw new InvalidDataException("Unsupported monster subentities.");
        ImmutableArray<int> Parts(int index) => fields.Length > index && fields[index].Length > 0
            ? fields[index].Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToImmutableArray() : [];
        var bone = int.Parse(fields[0], CultureInfo.InvariantCulture);
        // Indexed color syntax needs an explicit decoder before it can be admitted.
        if (bone <= 0 || fields.Length > 2 && fields[2].Length != 0) throw new InvalidDataException("Unsupported monster appearance.");
        return new(bone, Parts(1), [], fields.Length > 3 && fields[3].Length > 0 ? Parts(3) : [100]);
    }
    internal static CombatBoard ReadBoard(Dictionary<string, object> geometry)
    {
        var cells = Rows(geometry, "cellsData").Select(c =>
        {
            var red = Number(c, "red"); var blue = Number(c, "blue");
            if (red is < 0 or > 1 || blue is < 0 or > 1 || red + blue > 1) throw new InvalidDataException("Invalid native placement flags.");
            return new CombatCell(Number(c, "cellNumber"), Number(c, "mov") == 1 && Number(c, "visible") == 1 && Number(c, "altitude") != -128,
                Number(c, "los") == 1, Number(c, "altitude"), Number(c, "linkedZone") & 15, red == 1 ? 1 : blue == 1 ? 0 : null);
        }).ToArray();
        if (cells.Length != 560 || !cells.Select(c => c.Id).Order().SequenceEqual(Enumerable.Range(0, 560))) throw new InvalidDataException("Invalid combat cells.");
        return new(14, cells);
    }
}
