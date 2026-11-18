using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed class CombatSpellCatalog
{
    private sealed record Source(string Path, string Sha256);
    private sealed record Book(int Breed, int[] Spells);
    private sealed record Configuration(string Build, Source Spells, Source Levels, Source Breeds,
        int[] Common, Book[] Players, int[] Monsters, CombatTiming Timing);
    internal CombatTiming Timing { get; } = new();
    private readonly ImmutableArray<CombatSpell> common;
    private readonly Dictionary<int, ImmutableArray<CombatSpell>> players;
    private readonly Dictionary<(int Id, int Grade), CombatSpell> monsters;
    internal CombatSpellCatalog(string root)
    {
        using var resource = typeof(CombatSpellCatalog).Assembly.GetManifestResourceStream("Krosforge.Runtime.Native.combat-spells.json")
            ?? throw new InvalidDataException("Combat spell configuration missing.");
        var config = JsonSerializer.Deserialize<Configuration>(resource) ?? throw new InvalidDataException("Invalid spell configuration.");
        if (config.Build != ClientInstallation.SupportedBuild) throw new InvalidDataException("Spell build mismatch.");
        Timing = config.Timing;
        if (Timing.TurnMilliseconds is < 100 or > 300000 || Timing.TurnMilliseconds % 100 != 0 ||
            Timing.AnimationMilliseconds is < 100 or > 60000 || Timing.ReadyMilliseconds is < 100 or > 60000 ||
            Timing.AutomaticMilliseconds is < 1 or > 5000) throw new InvalidDataException("Invalid combat timing.");
        var wanted = config.Common.Concat(config.Players.SelectMany(p => p.Spells)).Concat(config.Monsters).ToHashSet();
        var spells = CreationDataReader.Read(Path.Combine(root, config.Spells.Path), config.Spells.Sha256,
            maxOutput: 8388608, selectedKeys: wanted.Select(id => (long)id).ToHashSet()).ToDictionary(r => N(r, "id"));
        var levelIds = spells.Values.SelectMany(r => config.Monsters.Contains(N(r, "id")) ? Array(r, "spellLevels") : Array(r, "spellLevels").Take(1))
            .Select(value => Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToHashSet();
        var levels = CreationDataReader.Read(Path.Combine(root, config.Levels.Path), config.Levels.Sha256,
            maxOutput: 33554432, selectedKeys: levelIds, maxInput: 2097152).ToDictionary(r => N(r, "id"));
        CombatSpell Read(int id)
        {
            var level = levels[Convert.ToInt32(Array(spells[id], "spellLevels")[0], CultureInfo.InvariantCulture)];
            if (N(level, "spellId") != id || N(level, "grade") != 1) throw new InvalidDataException("Spell grade does not match its definition.");
            return Parse(level);
        }
        common = config.Common.Select(Read).ToImmutableArray();
        var breeds = CreationDataReader.Read(Path.Combine(root, config.Breeds.Path), config.Breeds.Sha256,
            selectedKeys: config.Players.Select(p => (long)p.Breed).ToHashSet()).ToDictionary(r => N(r, "id"));
        players = config.Players.ToDictionary(p => p.Breed, p =>
        {
            var native = Array(breeds[p.Breed], "breedSpellsId").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToHashSet();
            if (p.Spells.Any(id => !native.Contains(id))) throw new InvalidDataException("Spell does not belong to the configured class.");
            return p.Spells.Select(Read).ToImmutableArray();
        });
        monsters = config.Monsters.SelectMany(id => Array(spells[id], "spellLevels").Select(value =>
        {
            var row = levels[Convert.ToInt32(value, CultureInfo.InvariantCulture)];
            if (N(row, "spellId") != id) throw new InvalidDataException("Monster spell identity mismatch.");
            return Parse(row);
        })).ToDictionary(spell => (spell.Id, spell.Grade));
    }
    internal CombatSpellCatalog(IEnumerable<CombatSpell> common, IReadOnlyDictionary<int, ImmutableArray<CombatSpell>>? players = null,
        IReadOnlyDictionary<int, ImmutableArray<CombatSpell>>? monsters = null)
    {
        this.common = common.ToImmutableArray(); this.players = players?.ToDictionary() ?? [];
        this.monsters = monsters?.Values.SelectMany(book => book).ToDictionary(spell => (spell.Id, spell.Grade)) ?? [];
    }
    internal ImmutableArray<CombatSpell> Player(int breed) => common.AddRange(players.GetValueOrDefault(breed, []));
    internal ImmutableArray<CombatSpell> Monster(IEnumerable<int> spells, IReadOnlyDictionary<int, int>? grades = null) => spells.Select(id =>
        monsters.TryGetValue((id, grades is null ? 1 : grades.TryGetValue(id, out var grade) ? grade :
            throw new InvalidDataException("Monster spell grade missing.")), out var spell) ? spell :
            throw new InvalidDataException("Unsupported monster spell grade.")).ToImmutableArray();
    internal static CombatSpell Parse(Dictionary<string, object> row)
    {
        var flags = N(row, "m_flags");
        // Current native getters: LOS bit 2, boostable range bit 6, animation bit 9.
        // No equipment/range bonuses are admitted yet, so base range is authoritative.
        if ((flags & ~(4 | 64 | 512)) != 0 || (string)row["statesCriterion"] != "" || N(row, "minPlayerLevel") > 1 ||
            new[] { "maxStack", "maxGlobalCastPerTurn", "maxGlobalCastPerTarget", "minCastInterval", "initialCooldown", "globalCooldown", "ignorePrisonGlyph" }
                .Any(k => N(row, k) != 0) || Array(row, "previewZones").Length != 0)
            throw new InvalidDataException("Unsupported spell targeting, cooldown or condition.");
        var effect = Effect((Dictionary<string, object>)Array(row, "effects").Single());
        var criticalRows = Array(row, "criticalEffect");
        var chance = N(row, "criticalHitProbability");
        (CombatDamage Damage, bool Allies)? critical = chance == 0 && criticalRows.Length == 0 ? null : Effect((Dictionary<string, object>)criticalRows.Single());
        if (chance is < 0 or > 100 || critical is not null && (critical.Value.Allies != effect.Allies || critical.Value.Damage.Element != effect.Damage.Element))
            throw new InvalidDataException("Unsupported critical effect.");
        return new(N(row, "spellId"), N(row, "id"), N(row, "grade"), N(row, "apCost"), N(row, "minRange"), N(row, "range"),
            (flags & 4) != 0, effect.Allies, chance, effect.Damage, critical?.Damage, N(row, "maxCastPerTurn"), N(row, "maxCastPerTarget"));
    }
    private static (CombatDamage Damage, bool Allies) Effect(Dictionary<string, object> row)
    {
        var zone = (Dictionary<string, object>)row["zoneDescr"];
        var mask = (string)row["targetMask"];
        if (mask is not ("a,A" or "A") || (string)row["triggers"] != "I" ||
            new[] { "duration", "delay", "group", "targetId", "value", "modificator", "effectTriggerDuration" }.Any(k => N(row, k) != 0) ||
            Convert.ToDouble(row["random"], CultureInfo.InvariantCulture) != 0 || N(zone, "shape") != 80 || N(zone, "param1") != 1 ||
            N(zone, "param2") != 0 || Array(zone, "cellIds").Length != 0 || N(zone, "onlyAffectIfInSightLine") != 0)
            throw new InvalidDataException("Unsupported spell effect or area.");
        var element = N(row, "actionId") switch
        {
            96 => CombatElement.Water, 97 => CombatElement.Air, 98 => CombatElement.Fire,
            99 => CombatElement.Earth, 100 => CombatElement.Neutral, _ => throw new InvalidDataException("Unsupported combat effect.")
        };
        var min = N(row, "diceNum"); var max = N(row, "diceSide");
        if (min <= 0 || max < min) throw new InvalidDataException("Invalid damage dice.");
        return (new(element, min, max), mask == "a,A");
    }
    internal static CombatStatistics Statistics(Dictionary<string, object> grade)
    {
        var bonus = (Dictionary<string, object>)grade["bonusCharacteristics"];
        int Sum(string key) => checked(N(grade, key) + N(bonus, key));
        var strength = Sum("strength"); var intelligence = Sum("intelligence"); var chance = Sum("chance"); var agility = Sum("agility");
        // Reject unimplemented damage modifiers instead of discarding them.
        foreach (var key in new[] { "rangeBonus", "damageBonus", "percentDamageBonus", "criticalDamageBonus", "neutralDamageBonus", "earthDamageBonus",
            "fireDamageBonus", "waterDamageBonus", "airDamageBonus", "reductionEarthFlat", "reductionFireFlat", "reductionWaterFlat", "reductionAirFlat",
            "reductionNeutralFlat", "criticalDamageReduction", "criticalHitBonus", "damageReflect", "tackleBonus", "tackleEvade" })
            if (Sum(key) != 0) throw new InvalidDataException("Unsupported combat statistic: " + key);
        var resistances = Enum.GetValues<CombatElement>().ToImmutableDictionary(e => e, e => Sum("reduction" + e));
        return new(checked(strength + intelligence + chance + agility + Sum("initiativeBonus")), strength, intelligence, chance, agility, resistances);
    }
    private static int N(Dictionary<string, object> row, string key) => Convert.ToInt32(row[key], CultureInfo.InvariantCulture);
    private static object[] Array(Dictionary<string, object> row, string key) => (object[])((Dictionary<string, object>)row[key]).Single().Value;
}
