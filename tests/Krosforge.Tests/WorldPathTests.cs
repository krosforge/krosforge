using Krosforge.Core;
using Xunit;
namespace Krosforge.Tests;

public sealed class WorldPathTests
{
    private static readonly MovementPolicy Policy = new(14, 560, 200);
    private static MovementGrid Grid(Func<int, bool>? include = null) => new(14, Enumerable.Range(0, 560)
        .Where(id => include?.Invoke(id) ?? true).Select(id => new MovementCell(id, 0, 1)));
    [Fact]
    public void DirectionalTimingAdmitsOnlyTheActiveSegmentForNativeInterruption()
    {
        var policy = Policy with { Timing = new(136, 204, 120), CancellationLeadSteps = 1 };
        var path = Grid().Validate(356, [356, 313, 257], 0, policy)!;
        Assert.Equal(new[] { 356, 342, 327, 313, 285, 257 }, path.Cells);
        Assert.Equal(new[] { 0d, 136, 272, 408, 528, 648 }, path.ArrivalTimes.Select(t => t.TotalMilliseconds));
        Assert.True(path.CanCancel(285, TimeSpan.FromMilliseconds(500)));
        Assert.False(path.CanCancel(257, TimeSpan.FromMilliseconds(500)));
        Assert.False(path.CanCancel(342, TimeSpan.Zero));
        Assert.False(path.CanCancel(342, TimeSpan.FromMilliseconds(-1)));
        Assert.Equal(TimeSpan.FromMilliseconds(204), Grid().Validate(0, [0, 1], 0, policy)!.Duration);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void PredictionAllowanceMustRemainBounded(int milliseconds) =>
        Assert.Throws<ArgumentException>(() => Grid().Validate(0, [0, 1], 0, Policy with { PredictionLeadMilliseconds = milliseconds }));
    [Fact]
    public void ExpandsTurnsAndChecksEveryCellAndElapsedArrival()
    {
        var path = Grid().Validate(0, [0, 3], 7, Policy);
        Assert.NotNull(path); Assert.Equal(new[] { 0, 1, 2, 3 }, path.Cells); Assert.Equal(0, path.Direction);
        Assert.Equal(TimeSpan.FromMilliseconds(600), path.Duration);
        Assert.False(path.CanCancel(2, TimeSpan.FromMilliseconds(399)));
        Assert.True(path.CanCancel(2, TimeSpan.FromMilliseconds(400)));
        Assert.False(path.CanCancel(9, TimeSpan.FromSeconds(10)));
        Assert.Null(Grid(id => id != 1).Validate(0, [0, 3], 0, Policy));
    }
    [Fact]
    public void RejectsFalseOriginBoundsBentSegmentsLoopsAndUnknownTerrainLinks()
    {
        var grid = Grid();
        foreach (var points in new int[][] { [1, 2], [0, 560], [0, -1], [0, 16], [0, 1, 0, 2], [0, 0], [0] })
            Assert.Null(grid.Validate(0, points, 0, Policy));
        Assert.Null(grid.Validate(0, [0, 3], 8, Policy));
        Assert.Null(grid.Validate(0, [0, 3], 0, Policy with { MaximumCells = 3 }));
        foreach (var changed in new[] { new MovementCell(1, 10, 1), new MovementCell(1, 0, 2) })
            Assert.Null(new MovementGrid(14, [new(0, 0, 1), changed, new(2, 0, 1)]).Validate(0, [0, 2], 0, Policy));
    }
}
