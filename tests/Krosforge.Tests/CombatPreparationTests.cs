using Krosforge.Core;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class CombatPreparationTests
{
    internal static CombatBoard Board() => new(14, Enumerable.Range(0, 6)
        .Select(id => new CombatCell(id, true, true, 0, 1, id < 3 ? 0 : 1)));
    [Fact]
    public void PlacementReadinessAndClosureKeepOwnershipAndTeams()
    {
        var preparation = new CombatPreparation(Board(), [new(1, 0, 0, true), new(-1, 1, 5, false, true)]);
        Assert.False(preparation.Start());
        Assert.False(preparation.Place(99, 1)); Assert.False(preparation.Place(-1, 4));
        Assert.False(preparation.Place(1, 3)); Assert.False(preparation.Place(1, 6));
        Assert.True(preparation.Place(1, 2)); Assert.Equal(2, preparation.Fighters.Single(f => f.ActorId == 1).Cell);
        Assert.True(preparation.SetReady(1, true)); Assert.True(preparation.AllReady);
        Assert.False(preparation.Place(1, 1));
        Assert.True(preparation.SetReady(1, false)); Assert.False(preparation.AllReady);
        Assert.True(preparation.Place(1, 1)); Assert.True(preparation.SetReady(1, true));
        Assert.True(preparation.Start()); Assert.False(preparation.Start());
        Assert.False(preparation.Place(1, 0)); Assert.False(preparation.SetReady(1, false));
    }
    [Fact]
    public void InvalidInitialPlacementAndAbandonmentCannotCreateAReadyFight()
    {
        Assert.Throws<ArgumentException>(() => new CombatPreparation(Board(), [new(1, 0, 0, true), new(-1, 1, 0, false, true)]));
        var preparation = new CombatPreparation(Board(), [new(1, 0, 0, true), new(-1, 1, 5, false, true)]);
        preparation.Abandon(); Assert.False(preparation.SetReady(1, true)); Assert.False(preparation.Start());
    }
    [Fact]
    public void ApproachUsesConnectedTerrainAndStopsWithinRange()
    {
        var grid = new MovementGrid(14, Enumerable.Range(0, 6).Select(id => new MovementCell(id, 0, 1)));
        var path = grid.Approach(0, 5, 2, new(14, 560, 100));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, path);
        Assert.Equal(new[] { 4 }, grid.Approach(4, 5, 2, new(14, 560, 100)));
        var blocked = new MovementGrid(14, [new(0, 0, 1), new(1, 0, 2), new(2, 0, 1)]);
        Assert.Null(blocked.Approach(0, 2, 1, new(14, 560, 100)));
    }
    [Fact]
    public void InstalledEncounterUsesNativeMonsterAndBothPlacementZones()
    {
        var root = Environment.GetEnvironmentVariable("KROSFORGE_TEST_CLIENT");
        Assert.SkipWhen(root is null, "Requires the separately installed reference client.");
        var world = new WorldEntryCatalog(root!); var catalog = new CombatCatalog(root!, world);
        Assert.Equal(10, catalog.NeutralCalendar);
        var events = WorldEntryWire.Events(new MapSnapshot(world.Map(153878787), [], catalog.Encounters, catalog.NeutralCalendar));
        // Independent envelope: event(3), Any(1), type URL(1), payload(2), calendar id(3).
        Assert.Equal(Convert.FromHexString("1a1b0a190a13747970652e616e6b616d612e636f6d2f6c6f761202180a"), events[0]);
        var e = Assert.Single(catalog.Encounters);
        Assert.Equal(153878787, e.Map); Assert.Equal(363, e.GroupCell);
        Assert.Equal(153883910, e.Battle.Id); Assert.Equal(450, e.Battle.Subarea);
        Assert.Same(e.Battle, world.Map(e.Map).Battle);
        Assert.All(world.Maps.Where(map => map.Id != e.Map), map => Assert.Equal(153888768, map.Battle!.Id));
        Assert.DoesNotContain(world.Maps, map => map.Id == e.Battle.Id);
        Assert.Equal(65, e.Board.Cells.Values.Count(cell => cell.Walkable));
        Assert.Equal(new[] { 270,271,284,285,299,300,313,314 }, e.Board.Placements[0].Order());
        Assert.Equal(new[] { 261,262,274,275,290,291,303,304 }, e.Board.Placements[1].Order());
        Assert.Equal(3270, e.Monster.Definition); Assert.Equal(1, e.Monster.Grade); Assert.Equal(1, e.Monster.Level);
        Assert.Equal(15, e.Monster.Life); Assert.Equal(2, e.Monster.ActionPoints); Assert.Equal(3, e.Monster.MovementPoints);
        Assert.Equal(3253, e.Monster.Look.BoneId);
        Assert.Equal(50, e.Monster.Experience);
        Assert.Equal(4, e.Monster.SpellGrades[5866]);
        var nativeSpell = Assert.Single(new CombatSpellCatalog(root!).Monster(e.Monster.Spells, e.Monster.SpellGrades));
        Assert.Equal(CombatElement.Air, nativeSpell.Damage.Element); Assert.Equal(4, nativeSpell.Grade);
        Assert.Equal(2, world.Experience.At(110).Level);
        var rewards = new CombatRewardCalculator(root!, new("Local", 2.5m));
        Assert.Equal(125, rewards.Calculate(e, 1, 0, true).Experience);
        Assert.Equal(0, rewards.Calculate(e, 1, 0, false).Experience);
        rewards.Configure(new("Local", 0m));
        Assert.Equal(0, rewards.Calculate(e, 1, 0, true).Experience);
        var loot = new CombatRewardCalculator(root!, new("Local", 1, 1000), [e.Monster.Definition]);
        var award = loot.Calculate(e, 1, 0, true);
        Assert.Equal(new[] { 1984, 2473, 16504, 16518, 34618 }, award.Items.Select(item => item.Template).Order());
        Assert.All(award.Items, item => Assert.Equal(1, item.Quantity));
        loot.Configure(new("Local", 1, 0));
        Assert.Empty(loot.Calculate(e, 1, 0, true).Items);
        // Species in the six subareas intersecting the supported surface maps.
        int[] surface = [4108, 4109, 4110, 982, 4105, 4106, 970, 979, 980, 981, 971, 972, 973, 984, 4046, 4047, 4048, 4049, 3270, 3271, 3272, 3273];
        var surfaceLoot = new CombatRewardCalculator(root!, new("Local", 1, 1000), surface);
        foreach (var definition in surface)
            for (var grade = 1; grade <= 5; grade++)
                Assert.NotEmpty(surfaceLoot.Calculate(e with { Monster = e.Monster with { Definition = definition, Grade = grade } }, 1, 0, true).Items);
        Assert.NotNull(world.Map(e.Map).Movement!.Approach(world.Initial.CellId, e.GroupCell, e.AttackDistance, world.Movement));
    }
}
