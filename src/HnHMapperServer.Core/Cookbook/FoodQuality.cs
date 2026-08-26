namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// How a food's stored base-q10 values scale with item quality.
///
/// <c>multiplier = sqrt(q/10)</c> for FEPs and <c>multiplier2 = sqrt(multiplier)</c>
/// for hunger (FoodService.checkFood), so FEPs scale with the square root of relative
/// quality and hunger with its fourth root. Displaying hunger unscaled — or scaling
/// FEP/hunger by the FEP multiplier — overstates efficiency at high quality
/// (2x at q160), which is exactly what the efficiency ranking is read for.
/// </summary>
public static class FoodQuality
{
    /// <summary>Quality every stored value is normalized to.</summary>
    public const int BaseQuality = 10;

    /// <summary>Relative quality, floored at q1 so a stray 0 cannot zero every value.</summary>
    private static double Relative(int quality) => Math.Max(1, quality) / (double)BaseQuality;

    /// <summary>FEP values scale with sqrt(q/10) — q40 = 2x, q90 = 3x.</summary>
    public static double FepMultiplier(int quality) => Math.Sqrt(Relative(quality));

    /// <summary>Hunger cost scales with the fourth root of q/10 — q40 = 1.41x, q160 = 2x.</summary>
    public static double HungerMultiplier(int quality) => Math.Sqrt(FepMultiplier(quality));

    /// <summary>
    /// FEP-per-hunger therefore scales with sqrt(q/10) / (q/10)^0.25 = (q/10)^0.25 —
    /// the same factor as hunger, not the FEP one.
    /// </summary>
    public static double PerHungerMultiplier(int quality) =>
        FepMultiplier(quality) / HungerMultiplier(quality);
}

/// <summary>
/// The three quality factors of one displayed quality, passed together so no caller
/// can scale FEPs without also scaling hunger (the bug this type replaces).
/// </summary>
public readonly record struct FoodQualityScale(double Fep, double Hunger, double PerHunger)
{
    /// <summary>Base quality: everything 1x.</summary>
    public static readonly FoodQualityScale Base = new(1.0, 1.0, 1.0);

    public static FoodQualityScale For(int quality) => new(
        FoodQuality.FepMultiplier(quality),
        FoodQuality.HungerMultiplier(quality),
        FoodQuality.PerHungerMultiplier(quality));

    /// <summary>
    /// Derives the matching hunger factors from a FEP factor (hunger = sqrt(fep), the
    /// game's own relationship). For tests and for callers that already hold a FEP
    /// multiplier rather than a quality.
    /// </summary>
    public static FoodQualityScale OfFepMultiplier(double fep)
    {
        var hunger = Math.Sqrt(Math.Max(double.Epsilon, fep));
        return new FoodQualityScale(fep, hunger, fep / hunger);
    }
}
