using Krosforge.Core;
using Xunit;
namespace Krosforge.Tests;

public sealed class WorldPathTests
{
    private static readonly MovementPolicy Policy = new(14, 560, 200);
    private static MovementGrid Grid(Func<int, bool>? include = null) => new(14, Enumerable.Range(0, 560)
        .Where(id => include?.Invoke(id) ?? true).Select(id => new MovementCell(id, 0, 1)));
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
