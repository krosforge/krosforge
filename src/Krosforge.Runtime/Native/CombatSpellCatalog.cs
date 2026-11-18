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
    internal CombatSpellCatalog(string root, IEnumerable<int>? monsterSpells = null)
    {
        using var resource = typeof(CombatSpellCatalog).Assembly.GetManifestResourceStream("Krosforge.Runtime.Native.combat-spells.json")
            ?? throw new InvalidDataException("Combat spell configuration missing.");
        var config = JsonSerializer.Deserialize<Configuration>(resource) ?? throw new InvalidDataException("Invalid spell configuration.");
        if (config.Build != ClientInstallation.SupportedBuild) throw new InvalidDataException("Spell build mismatch.");
        Timing = config.Timing;
        if (Timing.TurnMilliseconds is < 100 or > 300000 || Timing.TurnMilliseconds % 100 != 0 ||
            Timing.AnimationMilliseconds is < 100 or > 60000 || Timing.ReadyMilliseconds is < 100 or > 60000 ||
            Timing.AutomaticMilliseconds is < 1 or > 5000) throw new InvalidDataException("Invalid combat timing.");
        var wantedMonsters = config.Monsters.Concat(monsterSpells ?? []).Distinct().ToArray();
        var wanted = config.Common.Concat(config.Players.SelectMany(p => p.Spells)).Concat(wantedMonsters).ToHashSet();
        var spells = CreationDataReader.Read(Path.Combine(root, config.Spells.Path), config.Spells.Sha256,
            maxOutput: 8388608, selectedKeys: wanted.Select(id => (long)id).ToHashSet()).ToDictionary(r => N(r, "id"));
        var levelIds = spells.Values.SelectMany(r => wantedMonsters.Contains(N(r, "id")) ? Array(r, "spellLevels") : Array(r, "spellLevels").Take(1))
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
        monsters = wantedMonsters.SelectMany(id => Array(spells[id], "spellLevels").Select(value =>
        {
            var row = levels[Convert.ToInt32(value, CultureInfo.InvariantCulture)];
            if (N(row, "spellId") != id) throw new InvalidDataException("Monster spell identity mismatch.");
            return Parse(row, monster: true);
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
    internal static CombatSpell Parse(Dictionary<string, object> row, bool monster = false)
    {
        var flags = N(row, "m_flags");
        // Current native getters: line/diagonal bits 0/1, LOS 2, free/taken 3/4,
        // boostable range 6 and animation 9. Player unlock levels do not gate monster books.
        if ((flags & ~(1 | 2 | 4 | 8 | 16 | 64 | 512)) != 0 || (string)row["statesCriterion"] != "" || !monster && N(row, "minPlayerLevel") > 1 ||
            N(row, "maxStack") is not (-1 or 0) || new[] { "maxGlobalCastPerTurn", "maxGlobalCastPerTarget", "globalCooldown", "ignorePrisonGlyph" }
                .Any(k => N(row, k) != 0) || Array(row, "previewZones").Length != 0)
            throw new InvalidDataException("Unsupported spell targeting, cooldown or condition.");
        var effects = Array(row, "effects").Cast<Dictionary<string, object>>().Select(Effect).ToImmutableArray();
        if (effects.IsEmpty) throw new InvalidDataException("Spell effects missing.");
        var criticalRows = Array(row, "criticalEffect");
        var chance = N(row, "criticalHitProbability");
        var critical = criticalRows.Cast<Dictionary<string, object>>().Select(Effect).ToImmutableArray();
        if (chance is < 0 or > 100 || chance > 0 && critical.IsEmpty || N(row, "minCastInterval") < 0 || N(row, "initialCooldown") < 0)
            throw new InvalidDataException("Invalid critical effect or cooldown.");
        CombatDamage Primary(IEnumerable<CombatEffect> entries) => entries.OfType<CombatDamageEffect>().FirstOrDefault()?.Damage ?? new(CombatElement.Neutral, 0, 0);
        return new(N(row, "spellId"), N(row, "id"), N(row, "grade"), N(row, "apCost"), N(row, "minRange"), N(row, "range"),
            (flags & 4) != 0, effects.Any(effect => (effect.Targets & CombatTargets.Allies) != 0), chance,
            Primary(effects), critical.IsEmpty ? null : Primary(critical), N(row, "maxCastPerTurn"), N(row, "maxCastPerTarget"))
        {
            InLine = (flags & 1) != 0, InDiagonal = (flags & 2) != 0, NeedsFreeCell = (flags & 8) != 0, NeedsOccupiedCell = (flags & 16) != 0,
            Cooldown = N(row, "minCastInterval"), InitialCooldown = N(row, "initialCooldown"), Effects = effects, CriticalEffects = critical
        };
    }
    private static CombatEffect Effect(Dictionary<string, object> row)
    {
        var zone = (Dictionary<string, object>)row["zoneDescr"];
        var mask = (string)row["targetMask"];
        var targets = mask switch
        {
            "A" => CombatTargets.Enemies, "a,A" => CombatTargets.Allies | CombatTargets.Enemies,
            "C" => CombatTargets.Caster, _ => throw new InvalidDataException("Unsupported combat target mask.")
        };
        if ((string)row["triggers"] != "I" ||
            new[] { "duration", "delay", "group", "targetId", "value", "modificator", "effectTriggerDuration" }.Any(k => N(row, k) != 0) ||
            Convert.ToDouble(row["random"], CultureInfo.InvariantCulture) != 0 || Array(zone, "cellIds").Length != 0 ||
            N(zone, "onlyAffectIfInSightLine") != 0 || N(zone, "isStopAtTarget") != 0 || N(zone, "forcedDirection") != -1 || N(zone, "includeCarried") is < 0 or > 1)
            throw new InvalidDataException("Unsupported spell effect or area.");
        var area = N(zone, "shape") switch
        {
            80 when N(zone, "param1") == 1 && N(zone, "param2") == 0 => new CombatArea(),
            67 => new CombatArea(CombatAreaShape.Circle, N(zone, "param1"), N(zone, "param2"), N(zone, "damageDecreaseStepPercent"), N(zone, "maxDamageDecreaseApplyCount")),
            76 => new CombatArea(CombatAreaShape.Line, N(zone, "param1"), N(zone, "param2"), N(zone, "damageDecreaseStepPercent"), N(zone, "maxDamageDecreaseApplyCount")),
            _ => throw new InvalidDataException("Unsupported combat area shape.")
        };
        if (area.Radius < area.MinimumRadius || area.MinimumRadius < 0 || area.Radius > 560 || area.DecreasePercent is < 0 or > 100 || area.MaximumDecreaseSteps is < 0 or > 560)
            throw new InvalidDataException("Invalid combat area.");
        var min = N(row, "diceNum"); var side = N(row, "diceSide"); var max = side == 0 ? min : side;
        if (min <= 0 || max < min || max == int.MaxValue) throw new InvalidDataException("Invalid combat effect dice.");
        if (N(row, "actionId") == 1109)
            return max <= 100 ? new CombatHealEffect(min, max, true, targets, area) : throw new InvalidDataException("Invalid percentage heal.");
        var element = N(row, "actionId") switch
        {
            96 => CombatElement.Water, 97 => CombatElement.Air, 98 => CombatElement.Fire,
            99 => CombatElement.Earth, 100 => CombatElement.Neutral, _ => throw new InvalidDataException("Unsupported combat effect.")
        };
        return new CombatDamageEffect(new(element, min, max), targets, area);
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
