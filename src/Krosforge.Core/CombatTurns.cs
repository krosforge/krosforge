using System.Collections.Immutable;
namespace Krosforge.Core;

public enum CombatElement { Neutral, Earth, Fire, Water, Air }
public sealed record CombatDamage(CombatElement Element, int Minimum, int Maximum);
public sealed record CombatSpell(int Id, int LevelId, int Grade, int Cost, int MinimumRange, int MaximumRange,
    bool LineOfSight, bool Allies, int CriticalChance, CombatDamage Damage, CombatDamage? Critical,
    int PerTurn = 0, int PerTarget = 0);
public sealed record CombatStatistics(int Initiative, int Strength, int Intelligence, int Chance, int Agility,
    ImmutableDictionary<CombatElement, int> Resistance)
{
    public static CombatStatistics Empty { get; } = new(0, 0, 0, 0, 0, ImmutableDictionary<CombatElement, int>.Empty);
    public int Power(CombatElement element) => element switch
    {
        CombatElement.Neutral or CombatElement.Earth => Strength, CombatElement.Fire => Intelligence,
        CombatElement.Water => Chance, CombatElement.Air => Agility, _ => throw new ArgumentOutOfRangeException(nameof(element))
    };
}
public sealed record CombatFighter(long Id, int Team, bool Controlled, int Cell, int MaximumLife, int Life,
    int MaximumAp, int Ap, int MaximumMp, int Mp, CombatStatistics Statistics, ImmutableArray<CombatSpell> Spells)
{
    public bool Alive => Life > 0;
}
public abstract record CombatAction(long Actor);
public sealed record CombatWalk(long Actor, IReadOnlyList<int> Cells) : CombatAction(Actor);
public sealed record CombatCast(long Actor, CombatSpell Spell, int Cell, long? Target, bool Critical,
    int Damage, bool Killed) : CombatAction(Actor);

// The owner serializes commands and animation acknowledgements. No wall clock, network or storage lives here.
public sealed class CombatTurns
{
    private readonly CombatBoard board;
    private readonly MovementGrid grid;
    private readonly Dictionary<long, CombatFighter> fighters;
    private readonly Dictionary<(long Actor, int Spell, long? Target), int> uses = [];
    private readonly long[] order;
    private int current;
    public IReadOnlyList<long> Order => order.ToArray();
    public IReadOnlyList<CombatFighter> Fighters => order.Select(id => fighters[id]).ToArray();
    public CombatFighter Current => fighters[order[current]];
    public long Serial { get; private set; } = 1;
    public int Round { get; private set; } = 1;
    public int OrderIndex => current;
    public bool Finished => fighters.Values.Where(f => f.Alive).Select(f => f.Team).Distinct().Count() <= 1;
    public int? WinningTeam => Finished ? fighters.Values.Where(f => f.Alive).Select(f => (int?)f.Team).FirstOrDefault() : null;
    public CombatTurns(CombatBoard board, IEnumerable<CombatFighter> participants)
    {
        this.board = board;
        fighters = participants.ToDictionary(f => f.Id);
        if (fighters.Count < 2 || fighters.Values.Select(f => f.Team).Distinct().Count() < 2 ||
            fighters.Values.Select(f => f.Cell).Distinct().Count() != fighters.Count || fighters.Values.Any(f =>
                f.Id == 0 || f.Team < 0 || f.MaximumLife <= 0 || f.Life <= 0 || f.Life > f.MaximumLife ||
                f.MaximumAp <= 0 || f.Ap != f.MaximumAp || f.MaximumMp < 0 || f.Mp != f.MaximumMp ||
                !board.Cells.TryGetValue(f.Cell, out var cell) || !cell.Walkable || f.Spells.IsDefault ||
                f.Spells.Select(s => s.Id).Distinct().Count() != f.Spells.Length))
            throw new ArgumentException("Invalid combat participants.");
        foreach (var spell in fighters.Values.SelectMany(f => f.Spells)) Validate(spell);
        grid = new(board.Columns, board.Cells.Values.Where(c => c.Walkable).Select(c => new MovementCell(c.Id, c.Altitude, c.Zone)));
        // Stable local tie break: controlled actors first, then identity. Initiative remains authoritative.
        order = fighters.Values.OrderByDescending(f => f.Statistics.Initiative).ThenByDescending(f => f.Controlled).ThenBy(f => f.Id).Select(f => f.Id).ToArray();
    }
    private static void Validate(CombatSpell spell)
    {
        bool Invalid(CombatDamage damage) => !Enum.IsDefined(damage.Element) || damage.Minimum < 0 || damage.Maximum < damage.Minimum || damage.Maximum == int.MaxValue;
        if (spell.Id < 0 || spell.LevelId <= 0 || spell.Grade <= 0 || spell.Cost <= 0 || spell.MinimumRange < 0 ||
            spell.MaximumRange < spell.MinimumRange || spell.MaximumRange > 560 || spell.CriticalChance is < 0 or > 100 ||
            spell.PerTurn < 0 || spell.PerTarget < 0 || Invalid(spell.Damage) ||
            spell.CriticalChance > 0 && (spell.Critical is null || Invalid(spell.Critical))) throw new ArgumentException("Invalid combat spell.");
    }
    private bool Owns(long actor, long serial) => !Finished && Serial == serial && Current.Id == actor && Current.Alive;
    public CombatWalk? Move(long actor, long serial, IReadOnlyList<int> turns)
    {
        if (!Owns(actor, serial)) return null;
        var fighter = Current;
        var path = grid.Validate(fighter.Cell, turns, 0, new(board.Columns, 560, 100));
        if (path is null || path.Cells.Count - 1 > fighter.Mp ||
            path.Cells.Zip(path.Cells.Skip(1)).Any(pair => grid.Distance(pair.First, pair.Second) != 1) ||
            path.Cells.Skip(1).Any(cell => fighters.Values.Any(f => f.Alive && f.Id != actor && f.Cell == cell))) return null;
        fighters[actor] = fighter with { Cell = path.Cells[^1], Mp = fighter.Mp - path.Cells.Count + 1 };
        return new(actor, path.Cells.ToArray());
    }
    public bool CanCast(long actor, long serial, int spellId, int cell)
    {
        if (!Owns(actor, serial) || !board.Cells.ContainsKey(cell)) return false;
        var fighter = Current; var spell = fighter.Spells.SingleOrDefault(s => s.Id == spellId);
        if (spell is null || fighter.Ap < spell.Cost) return false;
        var distance = grid.Distance(fighter.Cell, cell);
        if (distance < spell.MinimumRange || distance > spell.MaximumRange || spell.LineOfSight && !Sight(fighter.Cell, cell)) return false;
        var target = fighters.Values.SingleOrDefault(f => f.Alive && f.Cell == cell);
        return (spell.PerTurn == 0 || uses.Where(u => u.Key.Actor == actor && u.Key.Spell == spellId).Sum(u => u.Value) < spell.PerTurn) &&
            (spell.PerTarget == 0 || target is null || uses.GetValueOrDefault((actor, spellId, target.Id)) < spell.PerTarget);
    }
    public CombatCast? Cast(long actor, long serial, int spellId, int cell, Func<int, int, int> roll)
    {
        if (!CanCast(actor, serial, spellId, cell)) return null;
        var fighter = Current; var spell = fighter.Spells.Single(s => s.Id == spellId);
        var critical = spell.CriticalChance > 0 && Roll(roll, 1, 100) <= spell.CriticalChance;
        var effect = critical ? spell.Critical! : spell.Damage;
        var target = fighters.Values.SingleOrDefault(f => f.Alive && f.Cell == cell);
        var damage = 0;
        // The admitted masks exclude the caster. A cast on an empty or ineligible cell still spends AP.
        if (target is not null && target.Id != actor && (target.Team != fighter.Team || spell.Allies))
        {
            var baseDamage = Roll(roll, effect.Minimum, effect.Maximum);
            var powered = (long)baseDamage * Math.Max(0L, 100L + fighter.Statistics.Power(effect.Element)) / 100;
            var resisted = powered * (100 - Math.Clamp(target.Statistics.Resistance.GetValueOrDefault(effect.Element), 0, 100)) / 100;
            damage = (int)Math.Min(target.Life, resisted);
            fighters[target.Id] = target with { Life = target.Life - damage };
        }
        fighters[actor] = fighter with { Ap = fighter.Ap - spell.Cost };
        var key = (actor, spellId, target?.Id); uses[key] = uses.GetValueOrDefault(key) + 1;
        return new(actor, spell, cell, target?.Id, critical, damage, target is not null && target.Life == damage);
    }
    private static int Roll(Func<int, int, int> roll, int min, int max)
    {
        var result = roll(min, max);
        return result >= min && result <= max ? result : throw new InvalidOperationException("Random source returned an out-of-range value.");
    }
    public bool Next(long actor, long serial)
    {
        if (!Owns(actor, serial)) return false;
        do { current = (current + 1) % order.Length; if (current == 0) Round++; } while (!Current.Alive);
        Serial++; uses.Clear();
        fighters[Current.Id] = Current with { Ap = Current.MaximumAp, Mp = Current.MaximumMp };
        return true;
    }
    public IReadOnlyList<int>? Approach(long actor, long serial, int target)
    {
        if (!Owns(actor, serial) || Current.Mp == 0 || !board.Cells.ContainsKey(target)) return null;
        var start = Current.Cell; var previous = new Dictionary<int, int> { [start] = start }; var queue = new Queue<int>(); queue.Enqueue(start);
        var occupied = fighters.Values.Where(f => f.Alive && f.Id != actor).Select(f => f.Cell).ToHashSet();
        var coordinates = board.Cells.Values.Where(c => c.Walkable).ToDictionary(c => grid.Coordinate(c.Id), c => c.Id);
        var found = start;
        while (queue.TryDequeue(out var cell))
        {
            if (grid.Distance(cell, target) == 1) { found = cell; break; }
            var p = grid.Coordinate(cell);
            foreach (var delta in new[] { (1, 0), (0, 1), (-1, 0), (0, -1) })
            {
                if (!coordinates.TryGetValue((p.X + delta.Item1, p.Y + delta.Item2), out var next) || previous.ContainsKey(next) || occupied.Contains(next) ||
                    grid.Validate(cell, [cell, next], 0, new(board.Columns, 560, 100)) is null) continue;
                previous.Add(next, cell); queue.Enqueue(next);
            }
        }
        if (found == start) return null;
        var route = new List<int> { found }; while (route[^1] != start) route.Add(previous[route[^1]]);
        route.Reverse(); return route.Take(Current.Mp + 1).ToArray();
    }
    private bool Sight(int start, int end)
    {
        var source = grid.Coordinate(start); var target = grid.Coordinate(end);
        var dx = target.X - source.X; var dy = target.Y - source.Y; var nx = Math.Abs(dx); var ny = Math.Abs(dy);
        var coordinates = board.Cells.Values.ToDictionary(c => grid.Coordinate(c.Id), c => c);
        var occupied = fighters.Values.Where(f => f.Alive).Select(f => f.Cell).ToHashSet();
        var x = source.X; var y = source.Y; var ix = 0; var iy = 0;
        while (ix < nx || iy < ny)
        {
            var decision = (1L + 2L * ix) * ny - (1L + 2L * iy) * nx;
            if (decision == 0) { x += Math.Sign(dx); y += Math.Sign(dy); ix++; iy++; }
            else if (decision < 0) { x += Math.Sign(dx); ix++; }
            else { y += Math.Sign(dy); iy++; }
            if (!coordinates.TryGetValue((x, y), out var cell) || cell.Id != end && (!cell.LineOfSight || occupied.Contains(cell.Id))) return false;
        }
        return true;
    }
}
