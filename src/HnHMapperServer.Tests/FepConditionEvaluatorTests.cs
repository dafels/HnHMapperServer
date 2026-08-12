using HnHMapperServer.Core.Cookbook;

namespace HnHMapperServer.Tests;

/// <summary>
/// Unit tests for FepConditionEvaluator — the shared client/server semantics for
/// threshold conditions (quality scaling, percent shares, tiers, 2-dp rounding).
/// </summary>
public class FepConditionEvaluatorTests
{
    private static FepConditionTarget Target(
        int energy = 100,
        decimal hunger = 2m,
        params (string Attribute, int Tier, decimal Value)[] feps) =>
        FepConditionEvaluator.BuildTarget(energy, hunger, feps);

    private static IReadOnlyList<FepFilterCondition> Parse(string expr) =>
        FepFilterParser.Parse(expr).Conditions;

    [Fact]
    public void BuildTarget_AggregatesTotalsAndTiers()
    {
        var t = Target(feps: new[] { ("STR", 1, 6m), ("STR", 2, 4m), ("INT", 1, 2m) });

        Assert.Equal(12m, t.TotalFep);
        Assert.Equal(6m, t.FepPerHunger);
        Assert.Equal(10m, t.StatTotals["STR"]);
        Assert.Equal(6m, t.StatTierTotals["STR1"]);
        Assert.Equal(4m, t.StatTierTotals["STR2"]);
    }

    [Fact]
    public void Percent_IsShareOfOwnTotal_AndQualityInvariant()
    {
        var t = Target(feps: new[] { ("STR", 1, 6m), ("INT", 1, 4m) }); // STR = 60%

        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>50%"), 1.0));
        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>50%"), 2.0));
        Assert.False(FepConditionEvaluator.Matches(t, Parse("str>60%"), 1.0)); // exactly 60, not >
        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>=60%"), 1.0));
    }

    [Fact]
    public void AbsoluteValues_ScaleWithQualityMultiplier()
    {
        var t = Target(feps: new[] { ("INT", 2, 15m) });

        Assert.False(FepConditionEvaluator.Matches(t, Parse("int2>15"), 1.0)); // 15 > 15 is false
        Assert.True(FepConditionEvaluator.Matches(t, Parse("int2>15"), 2.0));  // 30 > 15
    }

    [Fact]
    public void HungerAndEnergy_AreUnscaled()
    {
        var t = Target(energy: 150, hunger: 1m, feps: new[] { ("STR", 1, 5m) });

        Assert.True(FepConditionEvaluator.Matches(t, Parse("hunger<=1 energy>=150"), 5.0));
        Assert.False(FepConditionEvaluator.Matches(t, Parse("hunger<1"), 5.0));
    }

    [Fact]
    public void MissingStat_ScoresZero()
    {
        var t = Target(feps: new[] { ("STR", 1, 5m) });

        Assert.False(FepConditionEvaluator.Matches(t, Parse("int>0"), 1.0));
        Assert.True(FepConditionEvaluator.Matches(t, Parse("int<10"), 1.0));
    }

    [Fact]
    public void ComparedValues_RoundToTwoDecimals()
    {
        // 1.005 * 10 = 10.05; a threshold typed as the displayed value must match.
        var t = Target(hunger: 1m, feps: new[] { ("STR", 1, 1.005m) });

        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>=10.05"), 10.0));
    }

    [Fact]
    public void MultipleConditions_AndTogether()
    {
        var t = Target(hunger: 1m, feps: new[] { ("STR", 1, 8m), ("INT", 1, 2m) });

        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>=8 hunger<=1"), 1.0));
        Assert.False(FepConditionEvaluator.Matches(t, Parse("str>=8 hunger<0.5"), 1.0));
    }

    [Fact]
    public void ZeroTotal_PercentIsZero()
    {
        var t = Target(feps: Array.Empty<(string, int, decimal)>());

        Assert.False(FepConditionEvaluator.Matches(t, Parse("str>=1%"), 1.0));
        Assert.True(FepConditionEvaluator.Matches(t, Parse("str<1%"), 1.0));
    }
}
