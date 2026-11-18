using System.Collections.Frozen;
namespace Krosforge.Core;

public sealed record MovementCell(int Id, int Altitude, int Zone);
public sealed record MovementTiming(int DiagonalMilliseconds, int HorizontalMilliseconds, int VerticalMilliseconds)
{
    public bool IsValid => DiagonalMilliseconds is > 0 and <= 5000 && HorizontalMilliseconds is > 0 and <= 5000 && VerticalMilliseconds is > 0 and <= 5000;
}
public sealed record MovementPolicy(int Columns, int MaximumCells, int StepMilliseconds)
{
    public MovementTiming? Timing { get; init; }
    public int CancellationLeadSteps { get; init; }
    public int PredictionLeadMilliseconds { get; init; }
}

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
        if (policy.Columns != columns || policy.MaximumCells < 2 || policy.StepMilliseconds <= 0 ||
            policy.CancellationLeadSteps is < 0 or > 1 || policy.PredictionLeadMilliseconds is < 0 or > 100 || policy.Timing is { IsValid: false })
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
                // A declared exploration zone connects its terrain, including elevation changes.
                // Without a zone, changing altitude would infer an unknown terrain link.
                if (terrain.Zone != origin.Zone || origin.Zone == 0 && terrain.Altitude != origin.Altitude || path.Contains(cell)) return null;
                path.Add(cell);
            }
        }
        var arrivals = new TimeSpan[path.Count];
        for (var i = 1; i < path.Count; i++)
        {
            var facing = Facing(path[i - 1], path[i]);
            var milliseconds = policy.Timing is not { } timing ? policy.StepMilliseconds
                : facing % 2 != 0 ? timing.DiagonalMilliseconds
                : facing is 0 or 4 ? timing.HorizontalMilliseconds : timing.VerticalMilliseconds;
            arrivals[i] = arrivals[i - 1] + TimeSpan.FromMilliseconds(milliseconds);
        }
        return new WorldPath(path.ToArray(), Facing(path[^2], path[^1]), TimeSpan.FromMilliseconds(policy.StepMilliseconds))
        { ArrivalTimes = arrivals, CancellationLeadSteps = policy.CancellationLeadSteps };
    }
}

public sealed record WorldPath(IReadOnlyList<int> Cells, int Direction, TimeSpan StepDuration)
{
    public IReadOnlyList<TimeSpan> ArrivalTimes { get; init; } = [];
    public int CancellationLeadSteps { get; init; }
    public TimeSpan ArrivalAt(int index) => ArrivalTimes.Count == 0 ? StepDuration * index : ArrivalTimes[index];
    public TimeSpan Duration => ArrivalAt(Cells.Count - 1);
    public WorldPath ReverseFromPartialSegment(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero || remaining > ArrivalAt(1)) throw new ArgumentOutOfRangeException(nameof(remaining));
        return this with { ArrivalTimes = Enumerable.Range(0, Cells.Count).Select(i => i == 0 ? TimeSpan.Zero : ArrivalAt(i) - remaining).ToArray() };
    }
    public int ReachedIndex(TimeSpan elapsed)
    {
        var index = 0;
        while (index + 1 < Cells.Count && ArrivalAt(index + 1) <= elapsed) index++;
        return index;
    }
    public bool CanCancel(int cell, TimeSpan elapsed)
    {
        // The native interruption names the destination of the segment in progress.
        // Before a deferred start, no new segment has earned that allowance.
        var maximum = Math.Min(Cells.Count - 1, ReachedIndex(elapsed) + (elapsed > TimeSpan.Zero ? CancellationLeadSteps : 0));
        for (var i = 0; i <= maximum; i++) if (Cells[i] == cell) return true;
        return false;
    }
}
