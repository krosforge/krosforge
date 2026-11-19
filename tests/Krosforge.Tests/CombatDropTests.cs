using Krosforge.Core;
using Xunit;
namespace Krosforge.Tests;

public sealed class CombatDropTests
{
    private static readonly ProspectingRules Rules = new(85, 150, 117, 1.5, 0.26, 2.5);
    [Fact]
    public void ProspectingUsesTheInstalledNonlinearCurveAndCaps()
    {
        Assert.Equal(1, CombatDrops.ProspectingCoefficient(0, Rules));
        Assert.Equal(1.6175, CombatDrops.ProspectingCoefficient(150, Rules), 6);
        Assert.InRange(CombatDrops.ProspectingCoefficient(300, Rules), 2.23, 2.25);
        Assert.Equal(1, CombatDrops.ProspectingCoefficient(int.MinValue, Rules));
        Assert.InRange(CombatDrops.ProspectingCoefficient(int.MaxValue, Rules), 2.49, 2.5);
    }
    [Fact]
    public void ServerRatesChangeProbabilityWithAnExplicitHundredPercentCap()
    {
        Assert.Equal(59, CombatDrops.Chance(59, 1, 1));
        // Reference UI: 59% base becomes 61.95% with its stated 5% account bonus.
        Assert.Equal(61.95, CombatDrops.Chance(59, 1, 1.05m), 6);
        Assert.Equal(100, CombatDrops.Chance(59, 1, 2));
        Assert.Equal(29.5, CombatDrops.Chance(59, 1, 0.5m));
        Assert.Equal(0, CombatDrops.Chance(59, 1, 0));
        Assert.False(CombatDrops.Roll(59, () => 0.59));
        Assert.True(CombatDrops.Roll(59, () => 0.589));
        Assert.False(CombatDrops.Roll(0, () => throw new InvalidOperationException()));
        Assert.True(CombatDrops.Roll(100, () => throw new InvalidOperationException()));
        Assert.Throws<ArgumentException>(() => CombatDrops.Roll(50, () => double.NaN));
        Assert.Throws<ArgumentException>(() => CombatDrops.Chance(double.NaN, 1, 1));
    }
}
