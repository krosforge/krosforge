using Krosforge.Core;
using Krosforge.Runtime.Native;
using Xunit;

namespace Krosforge.Tests;

public sealed class SurfaceTopologyTests
{
    [Theory]
    [InlineData(349, 0, 0, 336)]
    [InlineData(335, 1, 0, 336)]
    [InlineData(363, 7, 0, 336)]
    [InlineData(336, 4, 2, 349)]
    [InlineData(448, 5, 2, 447)]
    [InlineData(336, 3, 2, 363)]
    [InlineData(3, 6, 3, 535)]
    [InlineData(17, 6, 3, 549)]
    [InlineData(2, 7, 3, 548)]
    [InlineData(3, 5, 3, 548)]
    [InlineData(533, 2, 1, 1)]
    [InlineData(547, 2, 1, 15)]
    [InlineData(546, 1, 1, 1)]
    [InlineData(547, 3, 1, 1)]
    public void CrossesOneStepToTheGeometricallyCorrespondingCell(int exit, int direction, int side, int arrival)
    {
        Assert.Equal(arrival, SurfaceTopology.Cross(exit, direction, side));
        Assert.Equal(exit, SurfaceTopology.Cross(arrival, (direction + 4) % 8, (side + 2) % 4));
    }

    [Theory]
    [InlineData(27, 7, 3)] // NE crosses east here, not north.
    [InlineData(546, 3, 2)] // SW crosses south here, not west.
    [InlineData(300, 0, 0)]
    [InlineData(349, 1, 0)] // This diagonal remains inside the map.
    [InlineData(-1, 0, 0)]
    [InlineData(560, 0, 0)]
    [InlineData(349, 8, 0)]
    public void DoesNotTreatEverySharedDirectionFlagAsAnExit(int cell, int direction, int side) =>
        Assert.Null(SurfaceTopology.Cross(cell, direction, side));

    private static SurfaceMapGeometry Geometry(long id, long east = 0, long west = 0) => new(id, 1,
        Enumerable.Range(0, 560).Select(i => new SurfaceCell(i, false, 0, 1, 0)).ToArray(), [east, 0, west, 0]);
    private static (SurfaceMapDefinition[] Definitions, Dictionary<long, SurfaceMapGeometry> Maps) Pair()
    {
        var a = Geometry(100, east: 200); var b = Geometry(200, west: 100);
        a.Cells[349] = new(349, true, 0, 1, 1);
        a.Cells[363] = new(363, true, 0, 1, 131);
        b.Cells[336] = new(336, true, 0, 1, 56);
        b.Cells[350] = new(350, true, 0, 1, 16);
        b.Cells[364] = new(364, true, 0, 1, 56);
        a.Cells[377] = new(377, true, 0, 1, 1);
        return ([new(100, 0, 0), new(200, 1, 0)], new() { [100] = a, [200] = b });
    }

    [Fact]
    public void DistinctExitsKeepDistinctArrivalsAndPreferTheStraightCorrespondence()
    {
        var (definitions, maps) = Pair();
        var routes = SurfaceTopology.Build(definitions, maps);
        Assert.Equal(336, routes.Single(r => r.Source == 100 && r.ExitCell == 349).ArrivalCell);
        Assert.Equal(350, routes.Single(r => r.Source == 100 && r.ExitCell == 363).ArrivalCell);
        Assert.Equal(364, routes.Single(r => r.Source == 100 && r.ExitCell == 377).ArrivalCell);
        var catalog = new WorldEntryCatalog(new(100, 349, 0, 1), new(14, 560, 200), routes,
            maps.Values.Select(m => new EntryMap(m.Id, 1, m.Cells.Where(c => c.Walkable).Select(c => c.Id).ToHashSet())).ToArray());
        Assert.Equal(new WorldLocation(200, 350, 0, 8), catalog.Transition(new(100, 363, 7, 7), 200));
        Assert.Null(catalog.Transition(new(100, 362, 0, 1), 200));
        Assert.Null(catalog.Transition(new(100, 363, 0, 1), 999));
    }

    [Fact]
    public void ABlockedStraightArrivalUsesOnlyAnAdvertisedAdjacentDiagonal()
    {
        var (definitions, maps) = Pair();
        maps[200].Cells[350] = maps[200].Cells[350] with { Walkable = false };
        var route = SurfaceTopology.Build(definitions, maps).Single(r => r.Source == 100 && r.ExitCell == 363);
        Assert.Equal(336, route.ArrivalCell); Assert.Equal(7, route.Direction);
        maps[100].Cells[363] = maps[100].Cells[363] with { ExitDirections = 1 };
        Assert.Throws<InvalidDataException>(() => SurfaceTopology.Build(definitions, maps));
    }

    [Fact]
    public void DecodedExplorationTerrainIgnoresCombatBitsButDoesNotConnectAnotherZone()
    {
        var rows = Enumerable.Range(0, 560).Select(i => (object)new Dictionary<string, object>
        {
            ["cellNumber"] = i, ["mov"] = (byte)(i < 3 ? 1 : 0), ["visible"] = (byte)1,
            ["altitude"] = i == 1 ? -20 : 0, ["linkedZone"] = i == 0 ? 17 : i == 1 ? 16 : 32,
            ["mapChangeData"] = (byte)0
        }).ToArray();
        var geometry = new Dictionary<string, object>
        {
            ["id"] = 100, ["cellsData"] = new Dictionary<string, object> { ["common:49"] = rows },
            ["rightNeighbourId"] = 0, ["bottomNeighbourId"] = 0, ["leftNeighbourId"] = 0, ["topNeighbourId"] = 0
        };
        var map = SurfaceMapGeometry.Read(geometry, new() { ["subAreaId"] = (short)1 });
        var grid = new MovementGrid(14, map.Cells.Where(c => c.Walkable).Select(c => new MovementCell(c.Id, c.Altitude, c.RoleplayZone)));
        Assert.NotNull(grid.Validate(0, [0, 1], 0, new(14, 560, 200)));
        Assert.Null(grid.Validate(0, [0, 2], 0, new(14, 560, 200)));
        Assert.Null(grid.Validate(2, [2, 3], 0, new(14, 560, 200)));
    }

    [Theory]
    [InlineData("position")]
    [InlineData("neighbour")]
    [InlineData("blocked-arrivals")]
    [InlineData("no-return-flags")]
    [InlineData("disconnected")]
    public void RejectsContradictoryOrIncompleteGeometryInsteadOfInventingAnArrival(string fault)
    {
        var (definitions, maps) = Pair();
        switch (fault)
        {
            case "position": definitions[1] = definitions[1] with { X = 2 }; break;
            case "neighbour": maps[200].Neighbours[2] = 999; break;
            case "blocked-arrivals":
                maps[200] = maps[200] with { Cells = maps[200].Cells.Select(c => c with { Walkable = false }).ToArray() }; break;
            case "no-return-flags":
                maps[200] = maps[200] with { Cells = maps[200].Cells.Select(c => c with { ExitDirections = 0 }).ToArray() }; break;
            case "disconnected": definitions = [.. definitions, new(300, 3, 0)]; maps[300] = Geometry(300); break;
        }
        Assert.Throws<InvalidDataException>(() => SurfaceTopology.Build(definitions, maps));
    }

    [Fact]
    public void AFlaggedNeighbourOutsideTheSurfaceDoesNotEnableAccess()
    {
        var (definitions, maps) = Pair();
        maps[100].Neighbours[3] = 999;
        maps[100].Cells[3] = new(3, true, 0, 1, 224);
        Assert.DoesNotContain(SurfaceTopology.Build(definitions, maps), r => r.Destination == 999);
    }
}
