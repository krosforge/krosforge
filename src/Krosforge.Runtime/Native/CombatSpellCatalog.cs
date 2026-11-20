using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record RoleplaySpell(int Id, int Grade, int MinimumLevel);

internal sealed class CombatSpellCatalog
{
    private sealed record Source(string Path, string Sha256);
    private sealed record Book(int Breed, int[] Spells);
    private sealed record Configuration(string Build, Source Spells, Source Levels, Source Breeds,
        int[] Common, Book[] Players, int[] Monsters, CombatTiming Timing);
    internal CombatTiming Timing { get; } = new();
    private readonly ImmutableArray<CombatSpell> common;
    private readonly Dictionary<int, ImmutableArray<CombatSpell>> players;
    private readonly Dictionary<int, ImmutableArray<RoleplaySpell>> roleplay;
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
        var breeds = CreationDataReader.Read(Path.Combine(root, config.Breeds.Path), config.Breeds.Sha256).ToDictionary(r => N(r, "id"));
        var breedBooks = breeds.ToDictionary(row => row.Key, row => Array(row.Value, "breedSpellsId")
            .Select(value => Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToArray());
        var displayIds = config.Common.Concat(breedBooks.Values.SelectMany(ids => ids)).ToHashSet();
        var wanted = displayIds.Concat(config.Players.SelectMany(p => p.Spells)).Concat(wantedMonsters).ToHashSet();
        var spells = CreationDataReader.Read(Path.Combine(root, config.Spells.Path), config.Spells.Sha256,
            maxOutput: 8388608, selectedKeys: wanted.Select(id => (long)id).ToHashSet()).ToDictionary(r => N(r, "id"));
        var levelIds = spells.Values.SelectMany(r => Array(r, "spellLevels"))
            .Select(value => Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToHashSet();
        var levels = CreationDataReader.Read(Path.Combine(root, config.Levels.Path), config.Levels.Sha256,
            maxOutput: 33554432, selectedKeys: levelIds, maxInput: 2097152).ToDictionary(r => N(r, "id"));
        IEnumerable<Dictionary<string, object>> Levels(int id)
        {
            var grades = Array(spells[id], "spellLevels").Select(value => levels[Convert.ToInt32(value, CultureInfo.InvariantCulture)]).ToArray();
            if (grades.Length == 0 || grades.Any(grade => N(grade, "spellId") != id || N(grade, "grade") <= 0) ||
                grades.Select(grade => N(grade, "grade")).Distinct().Count() != grades.Length)
                throw new InvalidDataException("Spell grades do not match their definition.");
            return grades;
        }
        common = config.Common.SelectMany(Levels).Select(Parse).ToImmutableArray();
        var display = displayIds.ToDictionary(id => id, id => Levels(id).Select(row =>
        {
            var minimum = N(row, "minPlayerLevel");
            var grade = N(row, "grade");
            if (minimum < 0) throw new InvalidDataException("Invalid roleplay spell grade.");
            return new RoleplaySpell(id, grade, minimum);
        }).ToImmutableArray());
        roleplay = breedBooks.ToDictionary(row => row.Key,
            row => config.Common.Concat(row.Value).Distinct().SelectMany(id => display[id]).ToImmutableArray());
        players = config.Players.ToDictionary(p => p.Breed, p =>
        {
            if (p.Spells.Any(id => !breedBooks[p.Breed].Contains(id))) throw new InvalidDataException("Spell does not belong to the configured class.");
            return p.Spells.SelectMany(Levels).Select(Parse).ToImmutableArray();
        });
        monsters = wantedMonsters.SelectMany(Levels).Select(Parse).ToDictionary(spell => (spell.Id, spell.Grade));
    }
    internal RoleplaySpell[] RoleplaySpells(int breed, int level) => roleplay.GetValueOrDefault(breed, [])
        .Where(spell => spell.MinimumLevel <= level).GroupBy(spell => spell.Id)
        .Select(grades => grades.MaxBy(spell => spell.Grade)!).ToArray();
    internal CombatSpellCatalog(IEnumerable<CombatSpell> common, IReadOnlyDictionary<int, ImmutableArray<CombatSpell>>? players = null,
        IReadOnlyDictionary<int, ImmutableArray<CombatSpell>>? monsters = null,
        IReadOnlyDictionary<int, ImmutableArray<RoleplaySpell>>? roleplay = null)
    {
        this.common = common.ToImmutableArray(); this.players = players?.ToDictionary() ?? [];
        this.monsters = monsters?.Values.SelectMany(book => book).ToDictionary(spell => (spell.Id, spell.Grade)) ?? [];
        this.roleplay = roleplay?.ToDictionary() ?? [];
    }
    internal ImmutableArray<CombatSpell> Player(int breed, int level) => common.Concat(players.GetValueOrDefault(breed, []))
        .Where(spell => spell.MinimumLevel <= level).GroupBy(spell => spell.Id)
        .Select(grades => grades.MaxBy(spell => spell.Grade)!).ToImmutableArray();
    internal ImmutableArray<CombatSpell> Monster(IEnumerable<int> spells, IReadOnlyDictionary<int, int>? grades = null) => spells.Select(id =>
        monsters.TryGetValue((id, grades is null ? 1 : grades.TryGetValue(id, out var grade) ? grade :
            throw new InvalidDataException("Monster spell grade missing.")), out var spell) ? spell :
            throw new InvalidDataException("Unsupported monster spell grade.")).ToImmutableArray();
    private static CombatSpell Parse(Dictionary<string, object> row)
    {
        var flags = N(row, "m_flags");
        var maxStack = N(row, "maxStack");
        var minimumLevel = N(row, "minPlayerLevel");
        // Current native getters: line/diagonal bits 0/1, LOS 2, free/taken 3/4,
        // boostable range 6 and animation 9.
        if ((flags & ~(1 | 2 | 4 | 8 | 16 | 64 | 512)) != 0 || (string)row["statesCriterion"] != "" || minimumLevel < 0 ||
            maxStack < -1 || new[] { "maxGlobalCastPerTurn", "maxGlobalCastPerTarget", "globalCooldown", "ignorePrisonGlyph" }
                .Any(k => N(row, k) != 0) || Array(row, "previewZones").Length != 0)
            throw new InvalidDataException("Unsupported spell targeting, cooldown or condition.");
        var effects = Array(row, "effects").Cast<Dictionary<string, object>>().Select(effect => Effect(effect, maxStack)).ToImmutableArray();
        if (effects.IsEmpty) throw new InvalidDataException("Spell effects missing.");
        var criticalRows = Array(row, "criticalEffect");
        var chance = N(row, "criticalHitProbability");
        var critical = criticalRows.Cast<Dictionary<string, object>>().Select(effect => Effect(effect, maxStack)).ToImmutableArray();
        if (maxStack > 0 && (effects.Count(e => e is CombatPowerEffect) != 1 || !critical.IsEmpty && critical.Count(e => e is CombatPowerEffect) != 1))
            throw new InvalidDataException("Unsupported spell stack rules.");
        if (chance is < 0 or > 100 || chance > 0 && critical.IsEmpty || N(row, "minCastInterval") < 0 || N(row, "initialCooldown") < 0)
            throw new InvalidDataException("Invalid critical effect or cooldown.");
        CombatDamage Primary(IEnumerable<CombatEffect> entries) => entries.OfType<CombatDamageEffect>().FirstOrDefault()?.Damage ?? new(CombatElement.Neutral, 0, 0);
        return new(N(row, "spellId"), N(row, "id"), N(row, "grade"), N(row, "apCost"), N(row, "minRange"), N(row, "range"),
            (flags & 4) != 0, effects.Any(effect => (effect.Targets & CombatTargets.Allies) != 0), chance,
            Primary(effects), critical.IsEmpty ? null : Primary(critical), N(row, "maxCastPerTurn"), N(row, "maxCastPerTarget"))
        {
            InLine = (flags & 1) != 0, InDiagonal = (flags & 2) != 0, NeedsFreeCell = (flags & 8) != 0, NeedsOccupiedCell = (flags & 16) != 0,
            MinimumLevel = minimumLevel, Cooldown = N(row, "minCastInterval"), InitialCooldown = N(row, "initialCooldown"),
            Effects = effects, CriticalEffects = critical
        };
    }
    private static CombatEffect Effect(Dictionary<string, object> row, int maxStack)
    {
        var zone = (Dictionary<string, object>)row["zoneDescr"];
        var mask = (string)row["targetMask"];
        var targets = mask switch
        {
            "A" => CombatTargets.Enemies, "a,A" => CombatTargets.Allies | CombatTargets.Enemies,
            "C" => CombatTargets.Caster, _ => throw new InvalidDataException("Unsupported combat target mask.")
        };
        if ((string)row["triggers"] != "I" ||
            new[] { "delay", "group", "targetId", "value", "modificator", "effectTriggerDuration" }.Any(k => N(row, k) != 0) ||
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
        var action = N(row, "actionId");
        var duration = N(row, "duration");
        if (action == 138 && duration > 0 && maxStack > 0 && targets == CombatTargets.Caster && area.Shape == CombatAreaShape.Point &&
            side == 0 && N(row, "effectUid") > 0 && N(row, "dispellable") == 1)
            return new CombatPowerEffect(N(row, "effectUid"), min, duration, maxStack);
        if (duration != 0) throw new InvalidDataException("Unsupported combat effect duration.");
        if (action == 1109)
            return max <= 100 ? new CombatHealEffect(min, max, true, targets, area) : throw new InvalidDataException("Invalid percentage heal.");
        var element = action switch
        {
            96 => CombatElement.Water, 97 => CombatElement.Earth, 98 => CombatElement.Air,
            99 => CombatElement.Fire, 100 => CombatElement.Neutral, _ => throw new InvalidDataException("Unsupported combat effect.")
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
