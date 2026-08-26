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

    /// <summary>A quality scale from its FEP factor: hunger is its square root (the game's law).</summary>
    private static FoodQualityScale Scale(double fepMultiplier) =>
        FoodQualityScale.OfFepMultiplier(fepMultiplier);

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
    public void WeightedTotal_CountsPlusTwoFepsDouble()
    {
        // Big Bear Banger: STR+1 6 and STR+2 4 — 10 raw, 14 weighted.
        var bbb = Target(feps: new[] { ("STR", 1, 6m), ("STR", 2, 4m) });
        // Delicious Deer Dog: AGI+2 6, INT+1 2, PER+1 2 — also 10 raw, but 16 weighted.
        var ddd = Target(feps: new[] { ("AGI", 2, 6m), ("INT", 1, 2m), ("PER", 1, 2m) });

        Assert.Equal(10m, bbb.TotalFep);
        Assert.Equal(10m, ddd.TotalFep);
        Assert.Equal(14m, bbb.WeightedTotalFep);
        Assert.Equal(16m, ddd.WeightedTotalFep);

        // The two tie on "total" and separate on "wtotal" — the whole point of the column.
        Assert.True(FepConditionEvaluator.Matches(bbb, Parse("total=10"), FoodQualityScale.Base));
        Assert.True(FepConditionEvaluator.Matches(ddd, Parse("total=10"), FoodQualityScale.Base));
        Assert.True(FepConditionEvaluator.Matches(ddd, Parse("wtotal>=16"), FoodQualityScale.Base));
        Assert.False(FepConditionEvaluator.Matches(bbb, Parse("wtotal>=16"), FoodQualityScale.Base));
    }

    [Fact]
    public void WeightedEfficiency_IsExpectedStatPointsPerHunger()
    {
        // A +2 FEP fills the bar exactly like a +1 and wins the roll with the same odds,
        // but pays two stat points — so bar-fill efficiency and leveling efficiency differ.
        // 4 STR+2 over 2 hunger: 2/h of bar fill, 4/h of expected stat points.
        var t = Target(hunger: 2m, feps: new[] { ("STR", 2, 4m) });

        Assert.Equal(2m, t.FepPerHunger);
        Assert.Equal(4m, t.WeightedFepPerHunger);
        Assert.True(FepConditionEvaluator.Matches(t, Parse("eff=2 weff=4"), FoodQualityScale.Base));

        // A +1-only food reads the same in both columns.
        var plain = Target(hunger: 2m, feps: new[] { ("STR", 1, 4m) });
        Assert.Equal(plain.FepPerHunger, plain.WeightedFepPerHunger);
    }

    [Fact]
    public void WeightedEfficiency_ScalesByTheRatio_LikePlainEfficiency()
    {
        var t = Target(hunger: 2m, feps: new[] { ("STR", 2, 4m) });   // 4/h weighted at base

        // FEP factor 4 (q160) means hunger factor 2, so the ratio doubles — not quadruples.
        Assert.True(FepConditionEvaluator.Matches(t, Parse("weff=8"), Scale(4.0)));
    }

    [Fact]
    public void WeightedTotal_ScalesWithTheFepFactor()
    {
        var t = Target(feps: new[] { ("STR", 2, 4m) });   // 8 weighted at base

        Assert.True(FepConditionEvaluator.Matches(t, Parse("wtotal=16"), Scale(2.0)));
        Assert.True(FepConditionEvaluator.Matches(t, Parse("wtotal=8"), FoodQualityScale.Base));
    }

    [Fact]
    public void Percent_IsShareOfOwnTotal_AndQualityInvariant()
    {
        var t = Target(feps: new[] { ("STR", 1, 6m), ("INT", 1, 4m) }); // STR = 60%

        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>50%"), Scale(1.0)));
        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>50%"), Scale(2.0)));
        Assert.False(FepConditionEvaluator.Matches(t, Parse("str>60%"), Scale(1.0))); // exactly 60, not >
        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>=60%"), Scale(1.0)));
    }

    [Fact]
    public void AbsoluteValues_ScaleWithQualityMultiplier()
    {
        var t = Target(feps: new[] { ("INT", 2, 15m) });

        Assert.False(FepConditionEvaluator.Matches(t, Parse("int2>15"), Scale(1.0))); // 15 > 15 is false
        Assert.True(FepConditionEvaluator.Matches(t, Parse("int2>15"), Scale(2.0)));  // 30 > 15
    }

    [Fact]
    public void Hunger_ScalesWithQuality_EnergyDoesNot()
    {
        var t = Target(energy: 150, hunger: 1m, feps: new[] { ("STR", 1, 5m) });

        // Hunger grows with the fourth root of relative quality: a FEP factor of 5
        // means a hunger factor of sqrt(5) = 2.24, so a base 1 reads as 2.24.
        Assert.True(FepConditionEvaluator.Matches(t, Parse("hunger>=2.24 energy>=150"), Scale(5.0)));
        Assert.False(FepConditionEvaluator.Matches(t, Parse("hunger<=1"), Scale(5.0)));

        // Energy is quality-independent in the game, so it never scales.
        Assert.True(FepConditionEvaluator.Matches(t, Parse("energy=150"), Scale(5.0)));

        // At base quality both read exactly as stored.
        Assert.True(FepConditionEvaluator.Matches(t, Parse("hunger<=1 energy>=150"), FoodQualityScale.Base));
    }

    [Fact]
    public void Efficiency_ScalesByTheRatio_NotTheFepFactor()
    {
        // 10 FEP over 2 hunger = 5/h at base. At a FEP factor of 4 (q160) the FEPs are
        // 4x and the hunger 2x, so efficiency is 10/h — not the 20/h that scaling the
        // ratio by the FEP factor produced.
        var t = Target(hunger: 2m, feps: new[] { ("STR", 1, 10m) });

        Assert.True(FepConditionEvaluator.Matches(t, Parse("eff=10"), Scale(4.0)));
        Assert.False(FepConditionEvaluator.Matches(t, Parse("eff>=20"), Scale(4.0)));
        Assert.True(FepConditionEvaluator.Matches(t, Parse("eff=5"), FoodQualityScale.Base));
    }

    [Fact]
    public void MissingStat_ScoresZero()
    {
        var t = Target(feps: new[] { ("STR", 1, 5m) });

        Assert.False(FepConditionEvaluator.Matches(t, Parse("int>0"), Scale(1.0)));
        Assert.True(FepConditionEvaluator.Matches(t, Parse("int<10"), Scale(1.0)));
    }

    [Fact]
    public void ComparedValues_RoundToTwoDecimals()
    {
        // 1.005 * 10 = 10.05; a threshold typed as the displayed value must match.
        var t = Target(hunger: 1m, feps: new[] { ("STR", 1, 1.005m) });

        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>=10.05"), Scale(10.0)));
    }

    [Fact]
    public void MultipleConditions_AndTogether()
    {
        var t = Target(hunger: 1m, feps: new[] { ("STR", 1, 8m), ("INT", 1, 2m) });

        Assert.True(FepConditionEvaluator.Matches(t, Parse("str>=8 hunger<=1"), Scale(1.0)));
        Assert.False(FepConditionEvaluator.Matches(t, Parse("str>=8 hunger<0.5"), Scale(1.0)));
        Assert.False(FepConditionEvaluator.Matches(t, Parse("str>=8 hunger<=1"), Scale(4.0))); // hunger reads 2
    }

    [Fact]
    public void ZeroTotal_PercentIsZero()
    {
        var t = Target(feps: Array.Empty<(string, int, decimal)>());

        Assert.False(FepConditionEvaluator.Matches(t, Parse("str>=1%"), Scale(1.0)));
        Assert.True(FepConditionEvaluator.Matches(t, Parse("str<1%"), Scale(1.0)));
    }
}
