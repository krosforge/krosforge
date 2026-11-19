using Krosforge.Core;
using Xunit;
namespace Krosforge.Tests;

public sealed class CombatExperienceTests
{
    private static readonly GroupExperienceRules GroupRules = new([1, 1.1, 1.5, 2.3, 3.1, 3.6, 4.2, 4.7, 5.15, 5.55, 5.9, 6.2], 5, 10);
    private static readonly PlayerExperienceRules PlayerRules = new(2.5, 2_000_000_000);
    [Theory]
    [InlineData(false, 73)]
    [InlineData(true, 262)]
    public void OfficialIncarnamTooltipsMatchForLevelTwoWithFiveWisdom(bool group, long expected)
    {
        // Observed independently through the original 3.7.14.18 UI in [-2,-3].
        // One level-3 monster, or four monsters at levels 3, 3, 2, 1.
        ExperienceMonster[] monsters = group ? [new(3, 70), new(3, 70), new(2, 60), new(1, 50)] : [new(3, 70)];
        var shared = CombatExperience.Group([new(2)], monsters, true, 1, GroupRules);
        Assert.Equal(expected, CombatExperience.Individual(shared, monsters, 2, 5, 2, 1, 1, 1, PlayerRules));
    }
    [Fact]
    public void CurveHandlesExactThresholdsMultipleLevelsAndInvalidInputs()
    {
        var curve = new ExperienceCurve([0, 110, 650, 1500]);
        Assert.Equal(new ExperienceBand(1, 0, 110), curve.At(109));
        Assert.Equal(new ExperienceBand(2, 110, 650), curve.At(110));
        Assert.Equal(3, curve.At(1000).Level); Assert.Equal(4, curve.At(2000).Level);
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.At(-1));
        Assert.Throws<ArgumentException>(() => new ExperienceCurve([0, 110, 110]));
    }
    [Theory]
    [InlineData(1, 1, 50, 50)]
    [InlineData(1, 20, 100, 55)]
    [InlineData(20, 1, 100, 5)]
    [InlineData(6, 1, 100, 100)]
    [InlineData(7, 1, 100, 14)]
    public void GroupLevelRatiosRetainTheirBoundaryAndFloor(int player, int monster, long xp, long expected)
        => Assert.Equal(expected, CombatExperience.Group([new(player)], [new(monster, xp)], true, 1, GroupRules));
    [Fact]
    public void OnlyEligibleHumansContributeToTheGroupBonusAndOnlyDeadMonstersPay()
    {
        ExperienceParticipant[] players = [new(9), new(3), new(9, Companion: true), new(9, Present: false), new(9)];
        ExperienceMonster[] monsters = [new(40, 100), new(1, 999, Alive: true)];
        Assert.Equal(110L, CombatExperience.Group(players, monsters, true, 1, GroupRules));
        Assert.Equal(0L, CombatExperience.Group(players, monsters, false, 1, GroupRules));
    }
    [Fact]
    public void IndividualStageAppliesLevelCapWisdomBonusesAndMaximum()
    {
        Assert.Equal(12L, CombatExperience.Individual(100, [new(1, 100)], 20, 0, 20, 1, 1, 1, PlayerRules));
        Assert.Equal(150L, CombatExperience.Individual(100, [new(20, 100)], 20, 50, 20, 1, 1, 1, PlayerRules));
        Assert.Equal(600L, CombatExperience.Individual(100, [new(20, 100)], 20, 0, 20, 1.5, 2, 2, PlayerRules));
        Assert.Equal(2_000_000_000L, CombatExperience.Individual(4_000_000_000L, [new(20, 100)], 20, 0, 20, 1, 1, 1, PlayerRules));
    }
    [Fact]
    public void ServerMultiplierIsAppliedAfterNativeRoundingAndCanDisableExperience()
    {
        Assert.Equal(12L, CombatExperience.Multiply(5, 2.5m));
        Assert.Equal(0L, CombatExperience.Multiply(50, 0));
        Assert.Throws<OverflowException>(() => CombatExperience.Multiply(long.MaxValue, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatExperience.Multiply(1, -1));
    }
}
