using System.Collections.Frozen;
namespace Krosforge.Core;

public sealed record CombatCell(int Id, bool Walkable, bool LineOfSight, int Altitude, int Zone, int? PlacementTeam);
public sealed record CombatPlacement(long ActorId, int Team, int Cell, bool Controlled, bool Ready = false);

// Geometry and preparation rules do not depend on native message tags or storage.
public sealed class CombatBoard
{
    public int Columns { get; }
    public FrozenDictionary<int, CombatCell> Cells { get; }
    public FrozenDictionary<int, FrozenSet<int>> Placements { get; }
    public CombatBoard(int columns, IEnumerable<CombatCell> cells)
    {
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        Columns = columns; Cells = cells.ToFrozenDictionary(c => c.Id);
        if (Cells.Count == 0 || Cells.Values.Any(c => c.Id < 0 || c.Zone < 0 || c.PlacementTeam < 0 || c.PlacementTeam.HasValue && !c.Walkable))
            throw new ArgumentException("Invalid combat terrain or placement.");
        Placements = Cells.Values.Where(c => c.PlacementTeam.HasValue).GroupBy(c => c.PlacementTeam!.Value)
            .ToFrozenDictionary(g => g.Key, g => g.Select(c => c.Id).ToFrozenSet());
    }
}

public sealed class CombatPreparation
{
    private readonly CombatBoard board;
    private readonly Dictionary<long, CombatPlacement> fighters;
    public IReadOnlyList<CombatPlacement> Fighters => fighters.Values.OrderBy(f => f.ActorId).ToArray();
    public bool AllReady => fighters.Values.All(f => f.Ready);
    public bool Closed { get; private set; }
    public CombatPreparation(CombatBoard board, IEnumerable<CombatPlacement> fighters)
    {
        this.board = board; this.fighters = fighters.ToDictionary(f => f.ActorId);
        if (this.fighters.Count < 2 || this.fighters.Values.Select(f => f.Team).Distinct().Count() < 2 ||
            this.fighters.Values.Select(f => f.Cell).Distinct().Count() != this.fighters.Count ||
            this.fighters.Values.Any(f => f.ActorId == 0 || !Allowed(f.Team, f.Cell) || !f.Controlled && !f.Ready))
            throw new ArgumentException("Invalid combat participants.");
    }
    private bool Allowed(int team, int cell) => board.Placements.TryGetValue(team, out var cells) && cells.Contains(cell);
    public bool Place(long actor, int cell)
    {
        if (Closed || !fighters.TryGetValue(actor, out var fighter) || !fighter.Controlled || fighter.Ready ||
            !Allowed(fighter.Team, cell) || fighters.Values.Any(f => f.ActorId != actor && f.Cell == cell)) return false;
        fighters[actor] = fighter with { Cell = cell }; return true;
    }
    public bool SetReady(long actor, bool ready)
    {
        if (Closed || !fighters.TryGetValue(actor, out var fighter) || !fighter.Controlled) return false;
        fighters[actor] = fighter with { Ready = ready }; return true;
    }
    public bool Start()
    {
        if (Closed || !AllReady) return false;
        Closed = true; return true;
    }
    public void Abandon() => Closed = true;
}
