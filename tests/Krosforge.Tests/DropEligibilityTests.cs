using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class DropEligibilityTests
{
    private static DropFacts Facts(int level = 1) => new(level, 100, 0, 0, 0, new HashSet<int>(), new HashSet<int>(), new HashSet<int>(), []);
    [Fact]
    public void NativeServerSeasonAndLevelConditionsUseIndependentFacts()
    {
        var condition = DropCondition.Parse("(SC!5|ST!5)&PL>0&PL<59");
        Assert.True(condition.Matches(Facts().Evaluate)); Assert.False(condition.Matches(Facts(59).Evaluate));
        Assert.False(condition.Matches((Facts() with { ServerType = 5, Season = 5 }).Evaluate));
        Assert.True(condition.Matches((Facts() with { ServerType = 5, Season = 2 }).Evaluate));
        Assert.True(DropCondition.Parse("ST=2").Matches((Facts() with { Season = 2 }).Evaluate));
        Assert.False(DropCondition.Parse("SC=5").Matches(Facts().Evaluate));
    }
    [Fact]
    public void AbsentHunterWeaponAndAlterationsDetermineIneligibilityWithoutInventingUnknownFacts()
    {
        Assert.False(DropCondition.Parse("Pj>41,0&CU>0").Matches(Facts().Evaluate));
        Assert.Throws<InvalidOperationException>(() => DropCondition.Parse("Pj>41,0&CU>0").Matches((Facts() with { Hunter = 1 }).Evaluate));
        Assert.False(DropCondition.Parse("(HA=50|HS=3383)&Az=1&Pm!100").Matches(Facts().Evaluate));
        Assert.Throws<InvalidOperationException>(() => DropCondition.Parse("ZZ=1").Matches(Facts().Evaluate));
    }
    [Fact]
    public void QuestAndInventoryConditionsUseOwnedCharacterState()
    {
        var facts = Facts() with { ActiveQuests = new HashSet<int> { 7 }, Inventory = [new(1, 10, 3, "[]")] };
        Assert.True(DropCondition.Parse("Qa=7&PO=10&PO<10,4").Matches(facts.Evaluate));
        Assert.False(DropCondition.Parse("Qa!7|PO<10,3").Matches(facts.Evaluate));
        Assert.True(DropCondition.Parse("PO!11").Matches(facts.Evaluate));
    }
    [Theory]
    [InlineData("PL>")]
    [InlineData("PL>=1")]
    [InlineData("(PL=1")]
    [InlineData("PL=1)")]
    [InlineData("PL=1&&PL=2")]
    public void UnsupportedOrMalformedSyntaxIsRejected(string input) => Assert.Throws<ArgumentException>(() => DropCondition.Parse(input));
}
