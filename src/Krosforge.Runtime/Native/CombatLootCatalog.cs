using System.Globalization;
using System.Text.Json;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record LootSource(string Path, string Sha256);
internal sealed record LootSources(LootSource Monsters, LootSource Items, LootSource Effects, LootSource Servers, int Season);
internal sealed record DropAdjustment(int Grade, double Coefficient, DropCondition Condition);
internal sealed record LootDefinition(int Template, double[] Percentages, int Maximum, DropCondition Condition,
    bool DisableModifiers, DropAdjustment[] Adjustments);
internal sealed record LootEffect(int Action, int Minimum, int Maximum, int Constant, bool Fixed);
internal sealed record LootTemplate(int Id, LootEffect[] Effects)
{
    internal ItemAward Create(Func<int, int, int> roll)
    {
        var effects = Effects.Select(effect =>
        {
            if (effect.Fixed || effect.Maximum == 0) return new InventoryEffect(effect.Action, effect.Minimum, effect.Maximum, effect.Constant);
            var value = roll(effect.Minimum, effect.Maximum);
            if (value < effect.Minimum || value > effect.Maximum) throw new InvalidOperationException("Invalid item effect roll.");
            return new InventoryEffect(effect.Action, value, 0, effect.Constant);
        }).ToArray();
        return new(Id, 1, JsonSerializer.Serialize(effects));
    }
}

internal sealed class CombatLootCatalog
{
    private readonly Dictionary<int, LootDefinition[]> drops;
    private readonly Dictionary<int, LootTemplate> items;
    internal int ServerType { get; }
    internal int Season { get; }
    internal CombatLootCatalog(string root, LootSources sources, IEnumerable<int> monsterIds)
    {
        var selected = monsterIds.Select(id => (long)id).ToHashSet();
        var monsters = Read(root, sources.Monsters, selected, 16777216);
        var definitions = monsters.ToDictionary(row => N(row, "id"), row =>
        {
            var ordinary = Rows(row, "drops").Select(drop => new LootDefinition(N(drop, "objectId"),
                Enumerable.Range(1, 5).Select(grade => D(drop, "percentDropForGrade" + grade)).ToArray(), N(drop, "count"),
                DropCondition.Parse((string)drop["criterions"]), Flag(drop, "disableDropModificator"),
                Rows(drop, "specificDropCoefficient").Where(adjustment => N(adjustment, "monsterId") == N(row, "id"))
                    .Select(adjustment => new DropAdjustment(N(adjustment, "monsterGrade"), D(adjustment, "dropCoefficient"),
                        DropCondition.Parse((string)adjustment["criterions"]))).ToArray()));
            var global = Rows(row, "globalDrops").Select(drop => N(drop, "alterationId") == -1 ? new LootDefinition(N(drop, "objectId"),
                [D(drop, "minPercentDrop"), D(drop, "maxPercentDrop")], 1,
                DropCondition.Parse((string)drop["receiverCriterion"]), Flag(drop, "disableDropModificator"), [])
                : throw new InvalidDataException("Unsupported alteration drop."));
            return ordinary.Concat(global).ToArray();
        });
        var templates = Read(root, sources.Items, definitions.Values.SelectMany(rows => rows).Select(drop => (long)drop.Template).ToHashSet(),
            33554432, references: true);
        var effects = Read(root, sources.Effects, templates.SelectMany(row => Rows(row, "possibleEffects")).Select(effect => (long)N(effect, "actionId")).ToHashSet(), 4194304)
            .ToDictionary(row => N(row, "id"));
        items = templates.ToDictionary(row => N(row, "id"), row => new LootTemplate(N(row, "id"), Rows(row, "possibleEffects").Select(effect =>
        {
            var action = N(effect, "actionId");
            if (new[] { "duration", "delay", "group", "modificator", "targetId", "effectTriggerDuration" }.Any(key => N(effect, key) != 0) || D(effect, "random") != 0)
                throw new InvalidDataException("Unsupported item effect timing or selection.");
            return new LootEffect(action, N(effect, "diceNum"), N(effect, "diceSide"), N(effect, "value"), Flag(effects[action], "parametersFixed"));
        }).ToArray()));
        var server = Read(root, sources.Servers, new HashSet<long> { ServerPresentation.RealmId }, 4194304).Single();
        ServerType = N(server, "gameTypeId"); Season = sources.Season;
        drops = definitions;
        Validate();
    }
    internal CombatLootCatalog(Dictionary<int, LootDefinition[]> drops, Dictionary<int, LootTemplate> items, int serverType = 0, int season = 0)
    { this.drops = drops; this.items = items; ServerType = serverType; Season = season; Validate(); }
    internal ItemAward[] Roll(int monster, int grade, DropFacts facts, double prospecting, decimal multiplier,
        Func<double> random, Func<int, int, int> dice)
    {
        if (!drops.TryGetValue(monster, out var definitions)) throw new InvalidDataException("Monster loot missing.");
        var result = new List<ItemAward>();
        foreach (var drop in definitions)
        {
            if (!Eligible(drop.Condition, facts)) continue;
            double percent;
            if (drop.Percentages.Length == 5)
            {
                if (grade is < 1 or > 5) throw new InvalidDataException("Unsupported monster loot grade.");
                percent = drop.Percentages[grade - 1];
            }
            else if (drop.Percentages[0] == drop.Percentages[1]) percent = drop.Percentages[0];
            else throw new InvalidDataException("Variable global drop formula is unresolved.");
            var coefficient = drop.DisableModifiers ? 1 : prospecting;
            foreach (var adjustment in drop.Adjustments.Where(row => row.Grade == grade))
                if (Eligible(adjustment.Condition, facts)) coefficient *= adjustment.Coefficient;
            if (CombatDrops.Roll(CombatDrops.Chance(percent, coefficient, multiplier), random)) result.Add(items[drop.Template].Create(dice));
        }
        return result.ToArray();
    }
    private void Validate()
    {
        foreach (var drop in drops.Values.SelectMany(rows => rows))
            if (drop.Template <= 0 || drop.Maximum <= 0 || !items.ContainsKey(drop.Template) || drop.Percentages.Length is not (2 or 5) ||
                drop.Percentages.Any(value => !double.IsFinite(value) || value is < 0 or > 100) ||
                drop.Adjustments.Any(row => row.Grade is < 1 or > 5 || !double.IsFinite(row.Coefficient) || row.Coefficient < 0))
                throw new InvalidDataException("Invalid monster loot.");
        foreach (var item in items.Values)
            if (item.Id <= 0 || item.Effects.Any(effect => effect.Action <= 0 || effect.Maximum != 0 && effect.Maximum < effect.Minimum))
                throw new InvalidDataException("Invalid loot item.");
    }
    private static bool Eligible(DropCondition condition, DropFacts facts)
    {
        try { return condition.Matches(facts.Evaluate); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Monster drop eligibility is unresolved.", error); }
    }
    private static Dictionary<string, object>[] Read(string root, LootSource source, IReadOnlySet<long> keys, int output, bool references = false) =>
        CreationDataReader.Read(Path.Combine(root, source.Path), source.Sha256, selectedKeys: keys, maxOutput: output, resolveReferences: references);
    private static int N(Dictionary<string, object> row, string key) => Convert.ToInt32(row[key], CultureInfo.InvariantCulture);
    private static double D(Dictionary<string, object> row, string key) => Convert.ToDouble(row[key], CultureInfo.InvariantCulture);
    private static bool Flag(Dictionary<string, object> row, string key) => N(row, key) switch
    { 0 => false, 1 => true, _ => throw new InvalidDataException("Unknown loot flag: " + key) };
    private static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> row, string key) =>
        ((object[])((Dictionary<string, object>)row[key]).Single().Value).Cast<Dictionary<string, object>>();
}
