using Krosforge.Core;

namespace Krosforge.Runtime.Native;

internal sealed record SurfaceMapDefinition(long Id, int X, int Y);
internal sealed record SurfaceCell(int Id, bool Walkable, int Altitude, int RoleplayZone, int ExitDirections);
internal sealed record SurfaceMapGeometry(long Id, int Subarea, SurfaceCell[] Cells, long[] Neighbours)
{
    // Neighbours are ordered east, south, west, north, as the even native directions.
    internal static SurfaceMapGeometry Read(Dictionary<string, object> geometry, Dictionary<string, object> information)
    {
        var cells = ((object[])((Dictionary<string, object>)geometry["cellsData"])["common:49"])
            .Cast<Dictionary<string, object>>().Select(c => new SurfaceCell((int)c["cellNumber"],
                (byte)c["mov"] == 1 && (byte)c["visible"] == 1 && (int)c["altitude"] != -128,
                (int)c["altitude"], (Convert.ToInt32(c["linkedZone"]) >> 4) & 15,
                Convert.ToInt32(c["mapChangeData"]))).OrderBy(c => c.Id).ToArray();
        if (cells.Length != 560 || !cells.Select(c => c.Id).SequenceEqual(Enumerable.Range(0, 560)))
            throw new InvalidDataException("Invalid cell geometry.");
        return new((int)geometry["id"], Convert.ToInt32(information["subAreaId"]), cells,
            [Convert.ToInt64(geometry["rightNeighbourId"]), Convert.ToInt64(geometry["bottomNeighbourId"]),
             Convert.ToInt64(geometry["leftNeighbourId"]), Convert.ToInt64(geometry["topNeighbourId"])]);
    }
}

internal static class SurfaceTopology
{
    private static readonly (int X, int Y)[] Steps = [(1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1)];
    private static readonly (int X, int Y)[] MapSteps = [(1, 0), (0, 1), (-1, 0), (0, -1)];
    private static readonly string[] Sides = ["right", "bottom", "left", "top"];

    // Advance one native grid step, then translate only across the requested map edge.
    // A diagonal at a corner must actually cross this edge; a shared flag alone is insufficient.
    internal static int? Cross(int cell, int direction, int side)
    {
        if (cell is < 0 or >= 560 || direction is < 0 or > 7 || side is < 0 or > 3) return null;
        var (dx, dy) = Steps[direction];
        var row = cell / 14 + dx - dy;
        var parity = (row % 2 + 2) % 2;
        var column = (2 * (cell % 14) + cell / 14 % 2 + dx + dy - parity) / 2;
        switch (side)
        {
            case 0 when column >= 14: column -= 14; break;
            case 1 when row >= 40: row -= 40; break;
            case 2 when column < 0: column += 14; break;
            case 3 when row < 0: row += 40; break;
            default: return null;
        }
        return row is >= 0 and < 40 && column is >= 0 and < 14 ? row * 14 + column : null;
    }

    internal static MapTransition[] Build(SurfaceMapDefinition[] definitions, IReadOnlyDictionary<long, SurfaceMapGeometry> maps)
    {
        if (definitions.Length == 0 || definitions.Select(m => m.Id).Distinct().Count() != definitions.Length ||
            definitions.Select(m => (m.X, m.Y)).Distinct().Count() != definitions.Length ||
            !definitions.Select(m => m.Id).Order().SequenceEqual(maps.Keys.Order()))
            throw new InvalidDataException("Invalid surface map catalog.");
        var positions = definitions.ToDictionary(m => m.Id);
        var routes = new List<MapTransition>();
        foreach (var map in maps.Values)
            if (map.Neighbours.Length != 4 || map.Cells.Length != 560 ||
                !map.Cells.Select(c => c.Id).SequenceEqual(Enumerable.Range(0, 560)))
                throw new InvalidDataException("Invalid surface geometry.");
        foreach (var source in maps.Values)
        {
            for (var side = 0; side < 4; side++)
            {
                if (!maps.TryGetValue(source.Neighbours[side], out var destination)) continue;
                var from = positions[source.Id]; var to = positions[destination.Id];
                var (dx, dy) = MapSteps[side];
                if (to.X != from.X + dx || to.Y != from.Y + dy || destination.Neighbours[(side + 2) % 4] != source.Id)
                    throw new InvalidDataException("Surface neighbours disagree with world coordinates.");
                foreach (var cell in source.Cells.Where(c => c.Walkable))
                {
                    var hasExit = false;
                    MapTransition? route = null;
                    // Preserve the perpendicular coordinate whenever a straight exit exists.
                    // Diagonals are exact adjacent grid steps, never a nearest-walkable search.
                    foreach (var direction in new[] { side * 2, (side * 2 + 7) % 8, (side * 2 + 1) % 8 })
                    {
                        if ((cell.ExitDirections & (1 << direction)) == 0 || Cross(cell.Id, direction, side) is not { } arrival) continue;
                        hasExit = true;
                        var target = destination.Cells[arrival];
                        if (!target.Walkable || (target.ExitDirections & (1 << ((direction + 4) % 8))) == 0) continue;
                        route = new(source.Id, destination.Id, Sides[side], cell.Id, arrival, direction);
                        break;
                    }
                    if (hasExit && route is null) throw new InvalidDataException("Surface exit has no corresponding arrival.");
                    if (route is not null) routes.Add(route);
                }
            }
        }
        var reached = new HashSet<long> { definitions[0].Id };
        var pending = new Queue<long>(reached);
        while (pending.TryDequeue(out var id))
            foreach (var destination in routes.Where(r => r.Source == id).Select(r => r.Destination))
                if (reached.Add(destination)) pending.Enqueue(destination);
        if (reached.Count != maps.Count) throw new InvalidDataException("Surface contains unreachable maps.");
        return routes.ToArray();
    }
}
