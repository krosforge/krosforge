using System.Collections.Immutable;
namespace Krosforge.Core;

[Flags]
public enum CombatTargets { None = 0, Caster = 1, Allies = 2, Enemies = 4 }
public enum CombatAreaShape { Point, Circle, Line }
public sealed record CombatArea(CombatAreaShape Shape = CombatAreaShape.Point, int Radius = 0,
    int MinimumRadius = 0, int DecreasePercent = 0, int MaximumDecreaseSteps = 0);
public abstract record CombatEffect(CombatTargets Targets, CombatArea Area);
public sealed record CombatDamageEffect(CombatDamage Damage, CombatTargets Targets, CombatArea Area) : CombatEffect(Targets, Area);
public sealed record CombatHealEffect(int Minimum, int Maximum, bool PercentMaximumLife,
    CombatTargets Targets, CombatArea Area) : CombatEffect(Targets, Area);
public sealed record CombatPowerEffect(int EffectId, int Amount, int Turns, int MaximumStacks)
    : CombatEffect(CombatTargets.Caster, new());
internal sealed record CombatPowerBonus(int Id, int SpellId, int Amount, int EndRound);
public abstract record CombatEffectResult(long Target);
public sealed record CombatLifeLost(long Target, CombatElement Element, int Amount, bool Killed) : CombatEffectResult(Target);
public sealed record CombatLifeGained(long Target, int Amount) : CombatEffectResult(Target);
public sealed record CombatPowerGained(long Target, int Id, int EffectId, int Amount, int Round, int EndRound,
    int Order, int? Replaced) : CombatEffectResult(Target);

public sealed partial class CombatTurns
{
    private int powerIdentity;
    private static IEnumerable<CombatEffect> Effects(CombatSpell spell, bool critical)
    {
        var configured = critical ? spell.CriticalEffects : spell.Effects;
        return configured.IsDefaultOrEmpty
            ? [new CombatDamageEffect(critical ? spell.Critical! : spell.Damage,
                spell.Allies ? CombatTargets.Allies | CombatTargets.Enemies : CombatTargets.Enemies, new())]
            : configured;
    }
    private static bool Matches(CombatTargets relations, CombatFighter source, CombatFighter target) =>
        (relations & (source.Id == target.Id ? CombatTargets.Caster : source.Team == target.Team ? CombatTargets.Allies : CombatTargets.Enemies)) != 0;

    private int? AreaDistance(CombatArea area, int sourceCell, int castCell, int targetCell)
    {
        var center = grid.Coordinate(castCell); var target = grid.Coordinate(targetCell);
        var dx = target.X - center.X; var dy = target.Y - center.Y;
        int distance;
        switch (area.Shape)
        {
            case CombatAreaShape.Point: return castCell == targetCell ? 0 : null;
            case CombatAreaShape.Circle: distance = Math.Abs(dx) + Math.Abs(dy); break;
            case CombatAreaShape.Line:
                var source = grid.Coordinate(sourceCell);
                var aimX = center.X - source.X; var aimY = center.Y - source.Y;
                if (aimX == 0 && aimY == 0 || aimX != 0 && aimY != 0 && Math.Abs(aimX) != Math.Abs(aimY)) return null;
                var stepX = Math.Sign(aimX); var stepY = Math.Sign(aimY);
                distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                if (dx != stepX * distance || dy != stepY * distance) return null;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(area));
        }
        return distance >= area.MinimumRadius && distance <= area.Radius ? distance : null;
    }
    private static int Attenuation(CombatArea area, int distance) =>
        Math.Max(0, 100 - Math.Min(Math.Max(0, distance - area.MinimumRadius), area.MaximumDecreaseSteps) * area.DecreasePercent);

    private ImmutableArray<CombatEffectResult> Resolve(CombatFighter caster, CombatSpell spell, int cell, bool critical, Func<int, int, int> roll)
    {
        var result = ImmutableArray.CreateBuilder<CombatEffectResult>();
        // Freeze each effect's target set before applying it; a preceding effect can
        // change life/position without repeating or skipping a participant.
        foreach (var effect in Effects(spell, critical))
        {
            if (effect is CombatPowerEffect power)
            {
                var source = fighters[caster.Id];
                if (!source.Alive) continue;
                var bonuses = source.PowerBonuses;
                var matching = bonuses.Where(bonus => bonus.SpellId == spell.Id).ToArray();
                var replaced = matching.Length < power.MaximumStacks ? null : matching[0];
                if (replaced is not null) bonuses = bonuses.Remove(replaced);
                var bonus = new CombatPowerBonus(checked(++powerIdentity), spell.Id, power.Amount, checked(Round + power.Turns));
                fighters[source.Id] = source with { PowerBonuses = bonuses.Add(bonus) };
                result.Add(new CombatPowerGained(source.Id, bonus.Id, power.EffectId, bonus.Amount, Round, bonus.EndRound, OrderIndex, replaced?.Id));
                continue;
            }
            var targets = fighters.Values.Where(f => f.Alive && Matches(effect.Targets, caster, f))
                .Select(f => (Fighter: f, Distance: AreaDistance(effect.Area, caster.Cell, cell, f.Cell)))
                .Where(value => value.Distance.HasValue).OrderBy(value => value.Fighter.Id).ToArray();
            foreach (var (selected, distance) in targets)
            {
                var target = fighters[selected.Id];
                var factor = Attenuation(effect.Area, distance!.Value);
                switch (effect)
                {
                    case CombatDamageEffect damage:
                        var basis = Roll(roll, damage.Damage.Minimum, damage.Damage.Maximum);
                        var sourcePower = fighters[caster.Id].BonusPower;
                        var powered = (long)basis * Math.Max(0L, 100L + caster.Statistics.Power(damage.Damage.Element) + sourcePower) / 100;
                        var resisted = (Int128)powered * Math.Max(0L, 100L - target.Statistics.Resistance.GetValueOrDefault(damage.Damage.Element)) / 100;
                        var amount = (int)Int128.Min(target.Life, resisted * factor / 100);
                        if (amount == 0) break;
                        fighters[target.Id] = target with { Life = target.Life - amount };
                        var dealt = Summary(caster.Id); var taken = Summary(target.Id);
                        summaries[caster.Id] = dealt with { DamageDealt = checked(dealt.DamageDealt + amount),
                            EnemiesDefeated = checked(dealt.EnemiesDefeated + (target.Team != caster.Team && amount == target.Life ? 1 : 0)) };
                        summaries[target.Id] = Summary(target.Id) with { DamageTaken = checked(taken.DamageTaken + amount) };
                        result.Add(new CombatLifeLost(target.Id, damage.Damage.Element, amount, amount == target.Life));
                        break;
                    case CombatHealEffect heal:
                        var value = Roll(roll, heal.Minimum, heal.Maximum);
                        var requested = heal.PercentMaximumLife ? (long)target.MaximumLife * value / 100 : value;
                        var restored = (int)Math.Min(target.MaximumLife - target.Life, requested * factor / 100);
                        if (restored == 0) break;
                        fighters[target.Id] = target with { Life = target.Life + restored };
                        summaries[caster.Id] = Summary(caster.Id) with { HealingDone = checked(Summary(caster.Id).HealingDone + restored) };
                        summaries[target.Id] = Summary(target.Id) with { HealingReceived = checked(Summary(target.Id).HealingReceived + restored) };
                        result.Add(new CombatLifeGained(target.Id, restored));
                        break;
                    default: throw new ArgumentException("Unsupported combat effect.");
                }
            }
        }
        return result.ToImmutable();
    }
    public (int Spell, int Cell)? AutomaticCast()
    {
        if (Finished || Current.Controlled) return null;
        var actor = Current;
        var candidates = actor.Spells.SelectMany(spell => fighters.Values.Where(f => f.Alive).Select(f =>
        {
            var score = 0L;
            if (!CanCast(actor.Id, Serial, spell.Id, f.Cell)) return (spell.Id, Cell: f.Cell, Score: score);
            foreach (var effect in Effects(spell, false))
                foreach (var target in fighters.Values.Where(t => t.Alive && Matches(effect.Targets, actor, t)))
                {
                    if (AreaDistance(effect.Area, actor.Cell, f.Cell, target.Cell) is not { } distance) continue;
                    var factor = Attenuation(effect.Area, distance);
                    if (effect is CombatDamageEffect damage)
                        score += (target.Team == actor.Team ? -1 : 1) * (long)damage.Damage.Maximum * factor;
                    else if (effect is CombatHealEffect)
                        score += (target.Team == actor.Team ? 1 : -1) * (long)(target.MaximumLife - target.Life) * factor;
                }
            return (spell.Id, Cell: f.Cell, Score: score);
        }));
        var best = candidates.Where(c => c.Score > 0).OrderByDescending(c => c.Score).ThenBy(c => c.Id).ThenBy(c => c.Cell).FirstOrDefault();
        return best.Score > 0 ? (best.Id, best.Cell) : null;
    }
}
