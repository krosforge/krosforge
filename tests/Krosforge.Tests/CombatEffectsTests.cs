using System.Collections.Immutable;
using Krosforge.Core;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed class CombatEffectsTests
{
    private static CombatBoard Board() => new(14, Enumerable.Range(0, 560)
        .Select(id => new CombatCell(id, true, true, 0, 1, null)));
    private static CombatSpell Spell() => new(1, 1, 1, 2, 0, 8, false, false, 0, new(CombatElement.Fire, 10, 10), null);
    private static CombatFighter Player(params CombatSpell[] spells) => new(1, 0, true, 100, 100, 100, 6, 6, 3, 3, CombatStatistics.Empty, spells.ToImmutableArray());
    private static CombatFighter Enemy(long id, int cell, int life = 100) => new(id, 1, false, cell, 100, life, 4, 4, 3, 3, CombatStatistics.Empty, []);
    [Fact]
    public void CasterPowerScalesLaterDamageReplacesItsOldestStackAndExpires()
    {
        var spell = Spell() with { Effects = [new CombatDamageEffect(new(CombatElement.Air, 10, 10), CombatTargets.Enemies, new()), new CombatPowerEffect(87, 30, 2, 2)] };
        var battle = new CombatTurns(Board(), [Player(spell), Enemy(-1, 128)]);
        CombatCast Cast() => battle.Cast(1, battle.Serial, 1, 128, (min, _) => min)!;
        Assert.Equal(10, Cast().Damage);
        Assert.Equal(13, Cast().Damage);
        var third = Cast();
        Assert.Equal(16, third.Damage);
        Assert.Equal(60, battle.Current.BonusPower);
        Assert.Equal(0, battle.Current.Ap);
        Assert.Equal(new CombatPowerGained(1, 3, 87, 30, 1, 3, 0, 1), third.Resolved[1]);

        static ProtoObject Read(byte[] bytes) => ProtoObject.Read(bytes, 128);
        var frames = CombatWire.Events(new FightAction(1, 7, third, battle.Current, 1, 110)).Select(frame =>
            Read(Read(Read(frame).Bytes(3)).Bytes(1))).ToArray();
        var removed = Read(frames.Single(e => e.Text(1) == "type.ankama.com/juu").Bytes(2));
        Assert.Equal(1UL, removed.UInt64(1));
        var entry = Read(Read(frames.Single(e => e.Text(1) == "type.ankama.com/juh").Bytes(2)).Bytes(1));
        Assert.Equal(138UL, entry.UInt64(2)); Assert.Equal(1UL, entry.UInt64(3));
        var buff = Read(entry.Bytes(1));
        Assert.Equal(3UL, buff.UInt64(5)); Assert.Equal(87UL, buff.UInt64(6)); Assert.Equal(30UL, buff.UInt64(10));
        Assert.Equal(1UL, Read(buff.Bytes(2)).UInt64(2)); Assert.Equal(3UL, Read(buff.Bytes(4)).UInt64(2));

        void NextRound() { Assert.True(battle.Next(1, battle.Serial)); Assert.True(battle.Next(-1, battle.Serial)); }
        NextRound(); Assert.Equal(60, battle.Current.BonusPower);
        NextRound(); Assert.Equal(0, battle.Current.BonusPower);
        Assert.Equal(10, Cast().Damage);
    }
    [Fact]
    public void CircleHitsEveryEligibleTargetWithDistanceAttenuationAndOneApCost()
    {
        var spell = Spell() with { MaximumRange = 0, Effects = [new CombatDamageEffect(new(CombatElement.Water, 10, 10), CombatTargets.Enemies, new(CombatAreaShape.Circle, 2, 0, 10, 4))] };
        var battle = new CombatTurns(Board(), [Player(spell), Enemy(-1, 114), Enemy(-2, 128), Enemy(-3, 142), Enemy(2, 86) with { Team = 0 }]);
        var cast = battle.Cast(1, 1, 1, 100, (min, _) => min)!;
        Assert.Equal(4, battle.Current.Ap); Assert.Equal(100, battle.Current.Life);
        Assert.Equal(new long[] { -2, -1 }, cast.Resolved.Select(hit => hit.Target));
        Assert.Equal(91, battle.Fighters.Single(f => f.Id == -1).Life);
        Assert.Equal(92, battle.Fighters.Single(f => f.Id == -2).Life);
        Assert.Equal(100, battle.Fighters.Single(f => f.Id is -3).Life); Assert.Equal(100, battle.Fighters.Single(f => f.Id == 2).Life);
        Assert.Equal(17, battle.Summary(1).DamageDealt); Assert.Equal(0, cast.Damage);
    }
    [Fact]
    public void OrientedLineIncludesTheCastCellAndContinuesThroughItsRadius()
    {
        var spell = Spell() with { InLine = true, Effects = [new CombatDamageEffect(new(CombatElement.Fire, 10, 10), CombatTargets.Enemies, new(CombatAreaShape.Line, 2, 0, 10, 4))] };
        var battle = new CombatTurns(Board(), [Player(spell), Enemy(-1, 114), Enemy(-2, 127), Enemy(-3, 141), Enemy(-4, 115)]);
        var cast = battle.Cast(1, 1, 1, 114, (min, _) => min)!;
        Assert.Equal(new long[] { -3, -2, -1 }, cast.Resolved.Select(hit => hit.Target));
        Assert.Equal(90, battle.Fighters.Single(f => f.Id == -1).Life);
        Assert.Equal(91, battle.Fighters.Single(f => f.Id == -2).Life);
        Assert.Equal(92, battle.Fighters.Single(f => f.Id == -3).Life);
        Assert.Equal(100, battle.Fighters.Single(f => f.Id == -4).Life);
    }
    [Fact]
    public void LaunchGeometryAndOccupancyRejectBeforeSpendingPoints()
    {
        var line = Spell() with { InLine = true }; var diagonal = line with { Id = 2, InLine = false, InDiagonal = true };
        var free = Spell() with { Id = 3, NeedsFreeCell = true }; var occupied = Spell() with { Id = 4, NeedsOccupiedCell = true };
        var battle = new CombatTurns(Board(), [Player(line, diagonal, free, occupied), Enemy(-1, 128)]);
        Assert.True(battle.CanCast(1, 1, 1, 127)); Assert.False(battle.CanCast(1, 1, 1, 101));
        Assert.True(battle.CanCast(1, 1, 2, 128)); Assert.False(battle.CanCast(1, 1, 2, 127));
        Assert.False(battle.CanCast(1, 1, 3, 128)); Assert.True(battle.CanCast(1, 1, 3, 114));
        Assert.True(battle.CanCast(1, 1, 4, 128)); Assert.False(battle.CanCast(1, 1, 4, 114));
        Assert.Null(battle.Cast(1, 1, 3, 128, (_, _) => throw new Exception("Rejected casts must not roll")));
        Assert.Equal(6, battle.Current.Ap);
    }
    [Fact]
    public void InitialAndRepeatedCooldownsCountRoundsAndSurviveOtherActorsTurns()
    {
        var spell = Spell() with { InitialCooldown = 1, Cooldown = 3 };
        var battle = new CombatTurns(Board(), [Player(spell), Enemy(-1, 128)]);
        void NextRound() { Assert.True(battle.Next(1, battle.Serial)); Assert.True(battle.Next(-1, battle.Serial)); }
        Assert.False(battle.CanCast(1, battle.Serial, 1, 128)); NextRound();
        Assert.NotNull(battle.Cast(1, battle.Serial, 1, 128, (min, _) => min));
        Assert.False(battle.CanCast(1, battle.Serial, 1, 128)); NextRound();
        Assert.False(battle.CanCast(1, battle.Serial, 1, 128)); NextRound();
        Assert.False(battle.CanCast(1, battle.Serial, 1, 128)); NextRound();
        Assert.Equal(5, battle.Round); Assert.True(battle.CanCast(1, battle.Serial, 1, 128));
    }
    [Fact]
    public void SelfHealingKeepsItsApCostCapsLifeAndCountsOnlyRestoredLife()
    {
        var heal = Spell() with { MaximumRange = 0, Effects = [new CombatHealEffect(100, 100, true, CombatTargets.Caster, new())] };
        var monster = Enemy(-1, 128, 40) with { Spells = [heal], Statistics = CombatStatistics.Empty with { Initiative = 1 } };
        var battle = new CombatTurns(Board(), [Player(), monster]);
        Assert.Equal((1, 128), battle.AutomaticCast());
        var cast = battle.Cast(-1, battle.Serial, 1, 128, (min, _) => min)!;
        Assert.Equal(new CombatLifeGained(-1, 60), Assert.Single(cast.Resolved));
        Assert.Equal(100, battle.Current.Life); Assert.Equal(2, battle.Current.Ap);
        Assert.Equal(60, battle.Summary(-1).HealingDone); Assert.Equal(60, battle.Summary(-1).HealingReceived);
        Assert.Null(battle.AutomaticCast());
    }
    [Fact]
    public void LaterEffectFailureRestoresLifePointsApAndSummaryBeforeRetry()
    {
        var spell = Spell() with { Effects = [new CombatDamageEffect(new(CombatElement.Fire, 10, 10), CombatTargets.Enemies, new(CombatAreaShape.Circle, 2))] };
        var battle = new CombatTurns(Board(), [Player(spell), Enemy(-1, 114), Enemy(-2, 128)]);
        var rolls = 0;
        Assert.Throws<InvalidOperationException>(() => battle.Cast(1, 1, 1, 100, (minimum, maximum) => ++rolls == 1 ? minimum : maximum + 1));
        Assert.All(battle.Fighters, fighter => Assert.Equal(100, fighter.Life));
        Assert.Equal(6, battle.Current.Ap);
        Assert.Equal(new CombatSummary { TurnsStarted = 1 }, battle.Summary(1));
        var retried = battle.Cast(1, 1, 1, 100, (minimum, _) => minimum)!;
        Assert.Equal(2, retried.Resolved.Length); Assert.Equal(20, battle.Summary(1).DamageDealt); Assert.Equal(4, battle.Current.Ap);
        Assert.Equal(2, battle.Summary(1).ActionPointsSpent);
    }
    [Fact]
    public void NativeActionsProjectAllAreaHitsAndHealingWithTheObservedUnionFields()
    {
        var spell = Spell(); var cast = new CombatCast(1, spell, 128, -1, false, 10, false)
        {
            Resolved = [new CombatLifeLost(-1, CombatElement.Water, 10, false), new CombatLifeLost(-2, CombatElement.Air, 8, true), new CombatLifeGained(1, 5)]
        };
        static ProtoObject Read(byte[] b) => ProtoObject.Read(b, 4096);
        var actions = CombatWire.Events(new FightAction(1, 7, cast, Player(spell), 1, 110)).Select(frame =>
        {
            var any = Read(Read(Read(frame).Bytes(3)).Bytes(1)); return (Type: any.Text(1), Body: Read(any.Bytes(2)));
        }).Where(value => value.Type == "type.ankama.com/jsq").Select(value => value.Body).ToArray();
        Assert.Equal(new ulong[] { 300, 102, 96, 98, 141, 108 }, actions.Select(action => action.UInt64(22)));
        Assert.Equal(unchecked((ulong)-2L), Read(actions[3].Bytes(37)).UInt64(5)); Assert.Equal(8UL, Read(actions[3].Bytes(37)).UInt64(6));
        Assert.Equal(1UL, Read(actions[5].Bytes(39)).UInt64(2)); Assert.Equal(5UL, Read(actions[5].Bytes(39)).UInt64(4));
    }
    [Fact]
    public void InstalledAreaAndHealingBooksRetainTheirGeometryCooldownsAndOperands()
    {
        var root = Environment.GetEnvironmentVariable("KROSFORGE_TEST_CLIENT");
        Assert.SkipWhen(root is null, "Requires the separately installed reference client.");
        var catalog = new CombatSpellCatalog(root!, [5965, 5874, 5861, 3453, 5853]);
        var circle = Assert.Single(catalog.Monster([5965]));
        Assert.Equal(0, circle.MaximumRange); Assert.Equal(new CombatArea(CombatAreaShape.Circle, 2, 0, 10, 4), Assert.Single(circle.Effects).Area);
        var line = Assert.Single(catalog.Monster([5874])); Assert.Equal(CombatAreaShape.Line, Assert.Single(line.Effects).Area.Shape);
        var heal = Assert.Single(catalog.Monster([5861])); Assert.Equal(15, heal.Cooldown);
        Assert.Equal(new CombatHealEffect(100, 100, true, CombatTargets.Caster, new()), Assert.Single(heal.Effects));
        var aligned = Assert.Single(catalog.Monster([5853])); Assert.True(aligned.InLine); Assert.Equal(1, aligned.Cooldown);
    }
}
