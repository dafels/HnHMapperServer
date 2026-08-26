using HnHMapperServer.Core.Cookbook;

namespace HnHMapperServer.Tests;

/// <summary>
/// Quality scaling follows the game client's own exponents (Hurricane FoodService:
/// <c>multiplier = sqrt(q/10)</c> for FEPs, <c>multiplier2 = sqrt(multiplier)</c> for
/// hunger). Hunger used to be displayed unscaled and FEP/hunger scaled by the FEP
/// factor, which overstated efficiency by 2x at q160 — on the column read for efficiency.
/// </summary>
public class FoodQualityTests
{
    [Theory]
    [InlineData(10, 1.0)]
    [InlineData(40, 2.0)]
    [InlineData(90, 3.0)]
    [InlineData(160, 4.0)]
    public void FepMultiplier_IsTheSquareRootOfRelativeQuality(int quality, double expected) =>
        Assert.Equal(expected, FoodQuality.FepMultiplier(quality), 6);

    [Theory]
    [InlineData(10, 1.0)]
    [InlineData(40, 1.414214)]
    [InlineData(160, 2.0)]
    [InlineData(810, 3.0)]
    public void HungerMultiplier_IsTheFourthRootOfRelativeQuality(int quality, double expected) =>
        Assert.Equal(expected, FoodQuality.HungerMultiplier(quality), 6);

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(160)]
    [InlineData(999)]
    public void PerHungerMultiplier_EqualsTheHungerFactor(int quality) =>
        // FEP / hunger = sqrt(x) / x^0.25 = x^0.25 — both sides scale, so efficiency
        // grows by the fourth root, not the square root.
        Assert.Equal(FoodQuality.HungerMultiplier(quality), FoodQuality.PerHungerMultiplier(quality), 6);

    [Fact]
    public void QualityBelowOne_IsFlooredInsteadOfZeroingEverything()
    {
        Assert.Equal(FoodQuality.FepMultiplier(1), FoodQuality.FepMultiplier(0), 6);
        Assert.True(FoodQuality.FepMultiplier(0) > 0);
    }

    [Fact]
    public void Scale_CarriesAllThreeFactorsTogether()
    {
        var scale = FoodQualityScale.For(160);

        Assert.Equal(4.0, scale.Fep, 6);
        Assert.Equal(2.0, scale.Hunger, 6);
        Assert.Equal(2.0, scale.PerHunger, 6);
    }

    [Fact]
    public void OfFepMultiplier_DerivesTheGameRelationship()
    {
        var scale = FoodQualityScale.OfFepMultiplier(4.0);

        Assert.Equal(4.0, scale.Fep, 6);
        Assert.Equal(2.0, scale.Hunger, 6);
        Assert.Equal(2.0, scale.PerHunger, 6);
    }
}
