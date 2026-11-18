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
        var e = Assert.Single(catalog.Encounters);
        Assert.Equal(153878787, e.Map); Assert.Equal(363, e.GroupCell);
        Assert.Equal(new[] { 338,339,340,365,366,367,393,394,395,421 }, e.Board.Placements[0].Order());
        Assert.Equal(new[] { 455,456,457,469,483,484,498,499,511,526 }, e.Board.Placements[1].Order());
        Assert.Equal(3270, e.Monster.Definition); Assert.Equal(1, e.Monster.Grade); Assert.Equal(1, e.Monster.Level);
        Assert.Equal(15, e.Monster.Life); Assert.Equal(2, e.Monster.ActionPoints); Assert.Equal(3, e.Monster.MovementPoints);
        Assert.Equal(3253, e.Monster.Look.BoneId);
        Assert.NotNull(world.Map(e.Map).Movement!.Approach(world.Initial.CellId, e.GroupCell, e.AttackDistance, world.Movement));
    }
}
