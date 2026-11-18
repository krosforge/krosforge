using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record CombatMonster(int Definition, int Grade, int Level, int Life, int ActionPoints, int MovementPoints, EntityLookWire Look)
{
    internal CombatStatistics Statistics { get; init; } = CombatStatistics.Empty;
    internal ImmutableArray<int> Spells { get; init; } = [];
}
internal sealed record CombatEncounter(long Map, long GroupId, int GroupCell, int Direction, int AttackDistance,
    int FightType, int PlayerCell, int MonsterCell, CombatMonster Monster, CombatBoard Board);

internal sealed class CombatCatalog
{
    private sealed record Source(string Path, string Sha256);
    private sealed record Definition(long Map, long GroupId, int GroupCell, int Direction, int AttackDistance,
        int FightType, int PlayerCell, int MonsterCell, int MonsterId, int Grade);
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
                selectedKeys: config.Encounters.Select(e => (long)e.MonsterId).ToHashSet())
            .ToDictionary(row => Number(row, "id"));
        Encounters = config.Encounters.Select(definition =>
        {
            var map = world.Map(definition.Map);
            var board = map.Combat ?? throw new InvalidDataException("Combat geometry missing.");
            var monster = monsters[definition.MonsterId];
            var grade = Rows(monster, "grades").Single(row => Number(row, "grade") == definition.Grade);
            return new CombatEncounter(map.Id, definition.GroupId, definition.GroupCell, definition.Direction, definition.AttackDistance,
                definition.FightType, definition.PlayerCell, definition.MonsterCell,
                new(definition.MonsterId, definition.Grade, Number(grade, "level"), Number(grade, "lifePoints"),
                    Number(grade, "actionPoints"), Number(grade, "movementPoints"), ParseLook((string)monster["look"]))
                {
                    Statistics = CombatSpellCatalog.Statistics(grade),
                    Spells = ((object[])((Dictionary<string, object>)monster["spells"]).Single().Value)
                        .Select(value => Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToImmutableArray()
                }, board);
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
                !world.Map(e.Map).WalkableCells.Contains(e.GroupCell) || e.Monster.Definition <= 0 || e.Monster.Grade <= 0 ||
                e.Monster.Level <= 0 || e.Monster.Life <= 0 || e.Monster.ActionPoints <= 0 || e.Monster.MovementPoints < 0)
                throw new InvalidDataException("Invalid encounter configuration.");
            _ = new CombatPreparation(e.Board, [new(1, 0, e.PlayerCell, true), new(-1, 1, e.MonsterCell, false, true)]);
        }
    }
    private static int Number(Dictionary<string, object> row, string key) => Convert.ToInt32(row[key], CultureInfo.InvariantCulture);
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
