using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class CombatLootTests
{
    private static DropFacts Facts => new(1, 100, 0, 0, 0, new HashSet<int>(), new HashSet<int>(), new HashSet<int>(), []);
    private static LootDefinition Drop(int template, string condition = "", double percent = 50, bool fixedChance = false) =>
        new(template, [percent, percent, percent, percent, percent], 63, DropCondition.Parse(condition), fixedChance, []);
    [Fact]
    public void LootRollsOncePerEligibleMonsterAndScalesChanceWithoutMultiplyingStackSize()
    {
        var catalog = new CombatLootCatalog(new() { [1] = [Drop(10), Drop(11, "PL>2"), Drop(12, percent: 10, fixedChance: true)] },
            new() { [10] = new(10, []), [11] = new(11, []), [12] = new(12, []) });
        var calls = 0;
        var loot = catalog.Roll(1, 1, Facts, 2, 1, () => { calls++; return 0.15; }, (a, _) => a);
        Assert.Equal(10, Assert.Single(loot).Template); Assert.Equal(1, calls);
        Assert.Equal(1, loot[0].Quantity);
        Assert.Empty(catalog.Roll(1, 1, Facts, 2, 0, () => throw new InvalidOperationException(), (a, _) => a));
        Assert.Equal(new[] { 10, 12 }, catalog.Roll(1, 1, Facts, 1, 10, () => 0.9, (a, _) => a).Select(item => item.Template));
    }
    [Fact]
    public void GradeAndConditionalCoefficientSelectTheApplicableChance()
    {
        var drop = Drop(10) with { Percentages = [10, 20, 30, 40, 50], Adjustments = [new(2, 0.5, DropCondition.Parse("ST=2"))] };
        var catalog = new CombatLootCatalog(new() { [1] = [drop] }, new() { [10] = new(10, []) });
        Assert.Single(catalog.Roll(1, 2, Facts, 1, 1, () => 0.15, (a, _) => a));
        Assert.Empty(catalog.Roll(1, 2, Facts with { Season = 2 }, 1, 1, () => 0.15, (a, _) => a));
        Assert.Throws<InvalidDataException>(() => catalog.Roll(1, 6, Facts, 1, 1, () => 0, (a, _) => a));
    }
    [Fact]
    public void VariableStatsAreRolledWhileFixedEffectDiceArePreserved()
    {
        var item = new LootTemplate(10, [new(100, 2, 5, 0, true), new(125, 11, 15, 0, false)]);
        var calls = 0; var rolled = item.Create((a, b) => { calls++; return b; });
        var effects = System.Text.Json.JsonSerializer.Deserialize<InventoryEffect[]>(rolled.Effects)!;
        Assert.Equal(new InventoryEffect(100, 2, 5, 0), effects[0]);
        Assert.Equal(new InventoryEffect(125, 15, 0, 0), effects[1]); Assert.Equal(1, calls);
    }
    [Fact]
    public void EligibleUnresolvedRulesFailInsteadOfGrantingOrDiscardingLoot()
    {
        var catalog = new CombatLootCatalog(new() { [1] = [Drop(10, "ZZ=1")] }, new() { [10] = new(10, []) });
        Assert.Throws<InvalidDataException>(() => catalog.Roll(1, 1, Facts, 1, 1, () => 0, (a, _) => a));
        var variable = new CombatLootCatalog(new() { [1] = [Drop(10) with { Percentages = [1, 20] }] }, new() { [10] = new(10, []) });
        Assert.Throws<InvalidDataException>(() => variable.Roll(1, 1, Facts, 1, 1, () => 0, (a, _) => a));
    }
}
