using System.Collections.Frozen;
namespace Krosforge.Core;

public sealed record MovementCell(int Id, int Altitude, int Zone);
public sealed record MovementPolicy(int Columns, int MaximumCells, int StepMilliseconds);

// Geometry and elapsed time are independent of the client protocol and storage.
public sealed class MovementGrid
{
    private readonly int columns;
    private readonly FrozenDictionary<int, MovementCell> cells;
    private readonly FrozenDictionary<(int X, int Y), int> coordinates;
    public MovementGrid(int columns, IEnumerable<MovementCell> cells)
    {
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        this.columns = columns;
        this.cells = cells.ToFrozenDictionary(c => c.Id);
        if (this.cells.Keys.Any(id => id < 0)) throw new ArgumentException("Invalid cell identity.");
        coordinates = this.cells.Keys.ToFrozenDictionary(Coordinate, id => id);
    }
    public (int X, int Y) Coordinate(int cell)
    {
        var row = cell / columns; var column = cell % columns;
        return ((row + 1) / 2 + column, column - row / 2);
    }
    public int Facing(int source, int destination)
    {
        var a = Coordinate(source); var b = Coordinate(destination);
        return (Math.Sign(b.X - a.X), Math.Sign(b.Y - a.Y)) switch
        {
            (1, 1) => 0, (1, 0) => 1, (1, -1) => 2, (0, -1) => 3,
            (-1, -1) => 4, (-1, 0) => 5, (-1, 1) => 6, (0, 1) => 7,
            _ => throw new ArgumentException("Facing requires distinct cells.")
        };
    }
    public WorldPath? Validate(int source, IReadOnlyList<int> turns, int direction, MovementPolicy policy)
    {
        if (policy.Columns != columns || policy.MaximumCells < 2 || policy.StepMilliseconds <= 0)
            throw new ArgumentException("Invalid movement policy.");
        if (direction is < 0 or > 7 || turns.Count < 2 || turns.Count > policy.MaximumCells ||
            turns[0] != source || turns[^1] == source || !cells.TryGetValue(source, out var origin) ||
            turns.Any(id => !cells.ContainsKey(id))) return null;
        List<int> path = [source];
        for (var i = 1; i < turns.Count; i++)
        {
            var (x, y) = Coordinate(turns[i - 1]); var (endX, endY) = Coordinate(turns[i]);
            var dx = endX - x; var dy = endY - y;
            if (dx != 0 && dy != 0 && Math.Abs(dx) != Math.Abs(dy)) return null;
            var count = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (count == 0 || path.Count + count > policy.MaximumCells) return null;
            for (var step = 1; step <= count; step++)
            {
                if (!coordinates.TryGetValue((x + Math.Sign(dx) * step, y + Math.Sign(dy) * step), out var cell)) return null;
                var terrain = cells[cell];
                // The local exploration policy does not infer links between distinct
                // altitude/zone regions. Every intermediate cell must be admissible.
                if (terrain.Altitude != origin.Altitude || terrain.Zone != origin.Zone || path.Contains(cell)) return null;
                path.Add(cell);
            }
        }
        return new WorldPath(path.ToArray(), Facing(path[^2], path[^1]), TimeSpan.FromMilliseconds(policy.StepMilliseconds));
    }
}

public sealed record WorldPath(IReadOnlyList<int> Cells, int Direction, TimeSpan StepDuration)
{
    public TimeSpan Duration => StepDuration * (Cells.Count - 1);
    public int ReachedIndex(TimeSpan elapsed) => (int)Math.Clamp(elapsed.Ticks / StepDuration.Ticks, 0, Cells.Count - 1);
    public bool CanCancel(int cell, TimeSpan elapsed)
    {
        for (var i = 0; i <= ReachedIndex(elapsed); i++) if (Cells[i] == cell) return true;
        return false;
    }
}
