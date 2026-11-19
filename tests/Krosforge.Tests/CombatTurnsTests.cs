using System.Collections.Immutable;
using Krosforge.Core;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class CombatTurnsTests
{
    private static CombatBoard Board(params int[] walls) => new(14, Enumerable.Range(0, 560)
        .Select(id => new CombatCell(id, !walls.Contains(id), !walls.Contains(id), 0, 1, null)));
    private static CombatSpell Spell(bool sight = true, int range = 8, int perTarget = 0) =>
        new(1, 1, 1, 2, 1, range, sight, true, 0, new(CombatElement.Air, 10, 10), null, 0, perTarget);
    private static CombatFighter Player(int cell = 100, params CombatSpell[] spells) =>
        new(1, 0, true, cell, 100, 100, 6, 6, 3, 3, CombatStatistics.Empty, spells.ToImmutableArray());
    private static CombatFighter Enemy(int cell = 156, int life = 100) =>
        new(-1, 1, false, cell, life, life, 2, 2, 3, 3, CombatStatistics.Empty, []);
    [Fact]
    public void InitiativeTurnSerialAndRefreshAreAuthoritative()
    {
        var battle = new CombatTurns(Board(), [Player(), Enemy() with { Statistics = CombatStatistics.Empty with { Initiative = 1 } }]);
        Assert.Equal(-1, battle.Current.Id);
        Assert.False(battle.Next(1, battle.Serial)); Assert.False(battle.Next(-1, 0));
        Assert.True(battle.Next(-1, 1)); Assert.Equal(1, battle.Current.Id);
        var walk = battle.Move(1, 2, [100, 114, 128]); Assert.NotNull(walk); Assert.Equal(1, battle.Current.Mp);
        Assert.False(battle.Next(1, 1)); Assert.True(battle.Next(1, 2)); Assert.Equal(2, battle.Round);
        Assert.True(battle.Next(-1, 3)); Assert.Equal(3, battle.Current.Mp); Assert.Equal(6, battle.Current.Ap);
    }
    [Fact]
    public void MovementRejectsDiagonalOccupiedBlockedAndOverspentPathsWithoutChangingState()
    {
        var battle = new CombatTurns(Board(114), [Player(), Enemy(115)]);
        var before = battle.Current;
        Assert.Null(battle.Move(1, 1, [100, 101])); // diagonal in the combat coordinate system
        Assert.Null(battle.Move(1, 1, [100, 114]));
        Assert.Null(battle.Move(1, 1, [100, 115]));
        Assert.Null(battle.Move(1, 1, [100, 86, 72, 58, 44]));
        Assert.Equal(before, battle.Current);
        Assert.Equal(0, battle.Summary(1).MovementPointsSpent);
    }
    [Fact]
    public void RangeSightAndSpellOwnershipAreCheckedBeforeSpendingAp()
    {
        var battle = new CombatTurns(Board(114), [Player(100, Spell()), Enemy(127)]);
        Assert.False(battle.CanCast(1, 1, 99, 127)); Assert.False(battle.CanCast(-1, 1, 1, 100));
        Assert.False(battle.CanCast(1, 1, 1, 127)); Assert.False(battle.CanCast(1, 1, 1, 500));
        Assert.Equal(6, battle.Current.Ap);
        var withoutSight = new CombatTurns(Board(114), [Player(100, Spell(false)), Enemy(127)]);
        Assert.True(withoutSight.CanCast(1, 1, 1, 127));
    }
    [Fact]
    public void CastLimitsResistanceAndApAreAppliedAndResetAtTheNextOwnTurn()
    {
        var enemy = Enemy(128) with { Statistics = CombatStatistics.Empty with { Resistance = ImmutableDictionary<CombatElement, int>.Empty.Add(CombatElement.Air, 10) } };
        var battle = new CombatTurns(Board(), [Player(100, Spell(perTarget: 1)), enemy]);
        var cast = battle.Cast(1, 1, 1, 128, (min, _) => min)!;
        Assert.Equal(9, cast.Damage); Assert.Equal(4, battle.Current.Ap);
        Assert.Null(battle.Cast(1, 1, 1, 128, (_, _) => throw new Exception("Must not roll a rejected cast")));
        Assert.True(battle.Next(1, 1)); Assert.True(battle.Next(-1, 2));
        Assert.NotNull(battle.Cast(1, 3, 1, 128, (min, _) => min));
    }
    [Fact]
    public void NegativeElementalResistanceIncreasesDamageWithoutOverflowing()
    {
        var weak = Enemy(128) with { Statistics = CombatStatistics.Empty with
            { Resistance = ImmutableDictionary<CombatElement, int>.Empty.Add(CombatElement.Air, -50) } };
        var battle = new CombatTurns(Board(), [Player(100, Spell()), weak]);
        Assert.Equal(15, battle.Cast(1, 1, 1, 128, (min, _) => min)!.Damage);
        Assert.Equal(85, battle.Fighters.Single(f => f.Id == -1).Life);

        var extreme = weak with { Statistics = weak.Statistics with
            { Resistance = weak.Statistics.Resistance.SetItem(CombatElement.Air, int.MinValue) } };
        var powerful = Player(100, Spell() with { Damage = new(CombatElement.Air, int.MaxValue - 1, int.MaxValue - 1) })
            with { Statistics = CombatStatistics.Empty with { Agility = int.MaxValue } };
        var capped = new CombatTurns(Board(), [powerful, extreme]);
        var lethal = capped.Cast(1, 1, 1, 128, (min, _) => min)!;
        Assert.Equal(100, lethal.Damage); Assert.True(lethal.Killed);
    }
    [Fact]
    public void CriticalDeathAndEmptyTargetsHaveDistinctResults()
    {
        var spell = Spell() with { CriticalChance = 100, Critical = new(CombatElement.Air, 20, 20) };
        var battle = new CombatTurns(Board(), [Player(100, spell), Enemy(128, 15)]);
        var empty = battle.Cast(1, 1, 1, 114, (min, _) => min)!;
        Assert.Null(empty.Target); Assert.Equal(0, empty.Damage); Assert.False(empty.Killed); Assert.Equal(4, battle.Current.Ap);
        var cast = battle.Cast(1, 1, 1, 128, (min, _) => min)!;
        Assert.True(cast.Critical); Assert.True(cast.Killed); Assert.Equal(15, cast.Damage);
        Assert.True(battle.Finished); Assert.Equal(0, battle.WinningTeam);
        Assert.False(battle.Next(1, 1)); Assert.Null(battle.Move(1, 1, [100, 114]));
    }
    [Fact]
    public void AutomaticApproachUsesCardinalCellsAndStopsBeforeOccupiedTarget()
    {
        var battle = new CombatTurns(Board(), [Player(100), Enemy(170)]);
        var path = battle.Approach(1, 1, 170)!;
        Assert.InRange(path.Count, 2, 4); Assert.DoesNotContain(170, path); Assert.NotNull(battle.Move(1, 1, path));
    }
    [Fact]
    public void SummaryCountsEffectiveDamageOnceAndExcludesEmptyRejectedAndAlliedKills()
    {
        var spell = Spell() with { PerTarget = 1 };
        var battle = new CombatTurns(Board(), [Player(100, spell), Enemy(128, 15)]);
        battle.Cast(1, 1, 1, 114, (min, _) => min);
        Assert.Equal(new CombatSummary { TurnsStarted = 1, ActionPointsSpent = 2 }, battle.Summary(1));
        battle.Cast(1, 1, 1, 128, (min, _) => min);
        Assert.Null(battle.Cast(1, 1, 1, 128, (min, _) => min));
        battle.Next(1, 1); battle.Next(-1, 2);
        battle.Cast(1, 3, 1, 128, (min, _) => min);
        var attacker = new CombatSummary(15, 0, 1) { TurnsStarted = 2, ActionPointsSpent = 6 };
        Assert.Equal(attacker, battle.Summary(1));
        Assert.Equal(new CombatSummary(0, 15, 0) { TurnsStarted = 1 }, battle.Summary(-1));
        Assert.Null(battle.Cast(1, 3, 1, 128, (min, _) => min));
        Assert.Equal(attacker, battle.Summary(1));

        var ally = Enemy(128, 5) with { Id = 2, Team = 0 };
        var friendly = new CombatTurns(Board(), [Player(100, Spell()), ally, Enemy(170)]);
        friendly.Cast(1, 1, 1, 128, (min, _) => min);
        Assert.Equal(new CombatSummary(5) { TurnsStarted = 1, ActionPointsSpent = 2 }, friendly.Summary(1));
        Assert.Equal(new CombatSummary(0, 5), friendly.Summary(2));
    }
    [Fact]
    public void InstalledBooksReadOnlyExplicitSupportedActionsAndStatistics()
    {
        var root = Environment.GetEnvironmentVariable("KROSFORGE_TEST_CLIENT");
        Assert.SkipWhen(root is null, "Requires the separately installed reference client.");
        var catalog = new CombatSpellCatalog(root!);
        Assert.Equal(new[] { 0 }, catalog.Player(8).Select(s => s.Id));
        var book = catalog.Player(3); Assert.Equal(new[] { 0, 13338, 13331 }, book.Select(s => s.Id));
        var ranged = book.Single(s => s.Id == 13338);
        Assert.Equal(41718, ranged.LevelId); Assert.Equal(2, ranged.Cost); Assert.Equal(8, ranged.MaximumRange);
        Assert.Equal(new CombatDamage(CombatElement.Water, 7, 9), ranged.Damage); Assert.True(ranged.LineOfSight);
        var enemy = Assert.Single(catalog.Monster([5866])); Assert.Equal(25445, enemy.LevelId);
        Assert.Equal(new CombatDamage(CombatElement.Fire, 6, 10), enemy.Damage);
        var ranked = Assert.Single(catalog.Monster([5866], new Dictionary<int, int> { [5866] = 4 }));
        Assert.Equal(4, ranked.Grade); Assert.Equal(new CombatDamage(CombatElement.Air, 6, 10), ranked.Damage);
        Assert.Throws<InvalidDataException>(() => catalog.Monster([5866], new Dictionary<int, int>()));
        Assert.Throws<InvalidDataException>(() => catalog.Monster([5866], new Dictionary<int, int> { [5866] = 99 }));
        Assert.Throws<InvalidDataException>(() => catalog.Monster([123456]));
    }
    [Fact]
    public void MonsterSpellRankUsesTheCatalogLevelMappingAndRejectsMissingOrAmbiguousValues()
    {
        var ranks = CombatCatalog.ReadSpellGrades([17, 18], ["4,1;3,2", "2,1;1,2"], 1);
        Assert.Equal(4, ranks[17]); Assert.Equal(2, ranks[18]);
        Assert.Equal(3, CombatCatalog.ReadSpellGrades([17], ["4,1;3,2"], 2)[17]);
        Assert.Throws<InvalidDataException>(() => CombatCatalog.ReadSpellGrades([17], [], 1));
        foreach (var malformed in new[] { "1,1;2,1", "1,2", "1", "0,1", "1,-1", "1,1;" })
            Assert.Throws<InvalidDataException>(() => CombatCatalog.ReadSpellGrades([17], [malformed], 1));
    }
}
