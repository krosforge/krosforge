using Krosforge.Core;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

internal static partial class CombatWire
{
    internal static (int Spell, int Cell) CastRequest(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(1, 2);
        var spell = body.UInt64(1, false); var cell = body.UInt64(2, false);
        if (spell > int.MaxValue || cell >= 560) throw new ProtocolException("Invalid combat cast.");
        return ((int)spell, (int)cell);
    }
    internal static int SequenceAcknowledgement(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(1, 2, 3);
        var sequence = body.UInt64(2); var actor = body.UInt64(1, false);
        if (sequence is 0 or > int.MaxValue || body.UInt64(3) != 1 || actor > long.MaxValue)
            throw new ProtocolException("Invalid combat animation acknowledgement.");
        return (int)sequence;
    }
    private static byte[] Book(CombatView view)
    {
        var spells = view.Spells ?? [];
        var shortcuts = view.Shortcuts is { } saved
            ? saved.Where(row => spells.Any(spell => spell.Id == row.Spell))
            : spells.Select((spell, slot) => new SpellShortcut(slot, spell.Id));
        return J(shortcuts.Select(row => B(2, SpellShortcutWire.Encode(row)))
            .Concat(spells.Select(spell => B(5, J(N(1, 0), N(2, spell.Id), N(4, spell.Grade)))))
            .Append(N(6, view.Character.Id)).ToArray());
    }
    private static byte[] Simple(int id, int value) => J(N(2, id), B(3, N(2, value)));
    private static byte[] Usable(int id, int basis, int current, int used = 0) =>
        J(N(2, id), B(1, J(N(4, basis), N(6, current - basis), N(7, used))));
    // The native character sheet looks up all six primary attributes, including zeroes.
    internal static IEnumerable<byte[]> Attributes(CombatStatistics stats)
    {
        yield return Usable(10, stats.Strength, stats.Strength);
        yield return Usable(11, stats.Vitality, stats.Vitality);
        yield return Usable(12, stats.Wisdom, stats.Wisdom);
        yield return Usable(13, stats.Chance, stats.Chance);
        yield return Usable(14, stats.Agility, stats.Agility);
        yield return Usable(15, stats.Intelligence, stats.Intelligence);
        yield return Usable(44, stats.Initiative, stats.Initiative);
        foreach (var (element, id) in new[] { (CombatElement.Earth, 33), (CombatElement.Fire, 34), (CombatElement.Water, 35), (CombatElement.Air, 36), (CombatElement.Neutral, 37) })
            yield return Usable(id, stats.Resistance.GetValueOrDefault(element), stats.Resistance.GetValueOrDefault(element));
        // Neutral multiplicative factors and no erosion are explicit local combat rules.
        foreach (var id in new[] { 107, 120, 121, 122, 123, 124, 125, 141, 142, 150 }) yield return Usable(id, 100, 100);
        yield return Usable(75, 0, 0);
    }
    private static byte[] Statistics(CombatFighter player, int level, long nextExperience, EntryAttributes? attributes)
    {
        var lifeBasis = player.MaximumLife - player.Statistics.Vitality;
        var entries = new[] { Usable(0, lifeBasis, lifeBasis), Simple(95, player.MaximumLife),
            Simple(96, 0), Simple(97, player.Life - player.MaximumLife), Simple(102, 0), Simple(5, level),
            Usable(1, player.MaximumAp, player.Ap, player.MaximumAp - player.Ap),
            Usable(23, player.MaximumMp, player.Mp, player.MaximumMp - player.Mp),
            Usable(3, attributes?.AvailablePoints ?? 0, attributes?.AvailablePoints ?? 0),
            Simple(29, attributes?.Energy ?? 10000), Simple(48, attributes?.Prospecting ?? 100), Simple(109, attributes?.Hunter ?? 0) };
        return B(1, J(entries.Concat(Attributes(player.Statistics)).Select(entry => B(1, entry))
            .Concat([N(11, attributes?.Experience ?? 0), N(8, attributes?.ExperienceFloor ?? 0), N(2, nextExperience), N(6, attributes?.Currency ?? 0), B(10, [])]).ToArray()));
    }
    private static byte[] Action(long source, int id, int variant, byte[] payload) => E("jsq", J(N(22, id), N(36, source), B(variant, payload)));
    private static byte[] Points(long actor, bool movement, int delta) => Action(actor, movement ? 129 : 102, 13, J(N(2, delta), N(3, actor)));
    private static byte[][] ActionEvents(FightAction notice)
    {
        var action = notice.Action;
        var result = new List<byte[]> { E("jqh", J(N(1, 0), N(2, action.Actor))) };
        if (action is CombatWalk movement)
        {
            result.Add(E("jnz", J(Packed(1, movement.Cells), N(4, action.Actor))));
            result.Add(Points(action.Actor, true, -(movement.Cells.Count - 1)));
        }
        else if (action is CombatCast cast)
        {
            var identity = B(2, J(N(1, cast.Spell.LevelId), N(2, cast.Spell.Id)));
            result.Add(Action(action.Actor, 300, 2, J(B(1, []), identity, N(5, cast.Cell),
                cast.Target is { } target ? N(6, target) : [], cast.Critical ? N(8, 1) : [])));
            result.Add(Points(action.Actor, false, -cast.Spell.Cost));
            IEnumerable<CombatEffectResult> resolved = cast.Resolved.IsDefaultOrEmpty
                ? cast.Damage > 0 && cast.Target is { } victim ? [new CombatLifeLost(victim, cast.Spell.Damage.Element, cast.Damage, cast.Killed)] : []
                : cast.Resolved;
            foreach (var effect in resolved)
            {
                if (effect is CombatLifeGained gain)
                { result.Add(Action(action.Actor, 108, 39, J(N(2, gain.Target), N(4, gain.Amount)))); continue; }
                if (effect is not CombatLifeLost hit) throw new InvalidDataException("Unsupported resolved combat effect.");
                var (damageAction, wireElement) = hit.Element switch
                {
                    CombatElement.Neutral => (100, 0), CombatElement.Earth => (97, 1), CombatElement.Fire => (99, 2),
                    CombatElement.Water => (96, 3), CombatElement.Air => (98, 4), _ => throw new ArgumentOutOfRangeException(nameof(hit.Element))
                };
                result.Add(Action(action.Actor, damageAction, 37, J(N(1, 0), N(4, wireElement), N(5, hit.Target), N(6, hit.Amount))));
                if (hit.Killed) result.Add(Action(action.Actor, 141, 3, N(2, hit.Target)));
            }
        }
        else throw new ArgumentOutOfRangeException(nameof(action));
        // Address the observing player; monster identities do not acknowledge animations.
        result.Add(E("jsy", J(N(2, notice.Sequence), N(3, 0), N(4, notice.Observer))));
        result.Add(E("krg", Statistics(notice.Player, notice.Level, notice.NextExperience, notice.Attributes)));
        return result.ToArray();
    }
    private static byte[][] TurnEvents(FightTurn turn) =>
    [
        E("krg", Statistics(turn.Player, turn.Level, turn.NextExperience, turn.Attributes)),
        E("jvi", J(N(1, turn.Round), N(2, turn.Order))),
        E("jvj", J(N(2, turn.Milliseconds / 100), N(3, turn.Actor), N(6, turn.Order))),
        .. turn.Confirmed ? new[] { E("jte", []) } : Array.Empty<byte[]>()
    ];
}
