namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// The values threshold conditions evaluate against — one shape for base foods and
/// recipe variations, on both the client (table/variant filtering) and the server
/// (variant-aware match endpoint).
/// </summary>
public readonly record struct FepConditionTarget(
    decimal TotalFep,
    decimal WeightedTotalFep,
    decimal FepPerHunger,
    decimal WeightedFepPerHunger,
    decimal Hunger,
    int Energy,
    IReadOnlyDictionary<string, decimal> StatTotals,
    IReadOnlyDictionary<string, decimal> StatTierTotals);

/// <summary>
/// Evaluates parsed <see cref="FepFilterCondition"/>s against a target. Semantics:
/// percent = share of the target's own total FEP (quality-invariant); absolute values
/// are quality-scaled by <see cref="FoodQualityScale"/> (WYSIWYG with the table at the
/// current Q input) — FEP totals and stats by the FEP factor, hunger by its own fourth-root
/// factor, FEP/hunger by the ratio of the two; energy does not scale with quality at all;
/// every compared value is rounded to 2 decimals so '=' and boundary values match what
/// the UI displays.
/// </summary>
public static class FepConditionEvaluator
{
    public static bool Matches(
        in FepConditionTarget t,
        IReadOnlyList<FepFilterCondition> conditions,
        FoodQualityScale scale)
    {
        foreach (var c in conditions)
        {
            var actual = Math.Round(Value(t, c, scale), 2);
            var ok = c.Op switch
            {
                FepFilterOp.Gt => actual > c.Value,
                FepFilterOp.Ge => actual >= c.Value,
                FepFilterOp.Lt => actual < c.Value,
                FepFilterOp.Le => actual <= c.Value,
                _ => actual == c.Value
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What one condition measures on a target — also the value chip-click sorting uses.</summary>
    public static decimal Value(in FepConditionTarget t, FepFilterCondition c, FoodQualityScale scale) =>
        c.Key switch
        {
            // Percent = share of the target's own total FEP; base values on both sides, so quality cancels.
            FepFilterKey.Stat when c.IsPercent =>
                t.TotalFep > 0 ? StatValue(t, c) / t.TotalFep * 100m : 0m,
            FepFilterKey.Stat => StatValue(t, c) * (decimal)scale.Fep,
            FepFilterKey.Total => t.TotalFep * (decimal)scale.Fep,
            FepFilterKey.WeightedTotal => t.WeightedTotalFep * (decimal)scale.Fep,
            FepFilterKey.Eff => t.FepPerHunger * (decimal)scale.PerHunger,
            FepFilterKey.WeightedEff => t.WeightedFepPerHunger * (decimal)scale.PerHunger,
            FepFilterKey.Hunger => t.Hunger * (decimal)scale.Hunger,
            _ => t.Energy
        };

    /// <summary>A target without the stat scores 0 ("str>0" excludes it, "str&lt;10" includes it).</summary>
    private static decimal StatValue(in FepConditionTarget t, FepFilterCondition c) =>
        c.Tier is int tier
            ? t.StatTierTotals.GetValueOrDefault(c.Attribute + tier.ToString())
            : t.StatTotals.GetValueOrDefault(c.Attribute!);

    /// <summary>Builds a target from raw FEP lines (base-q10 values), the shared aggregation.</summary>
    public static FepConditionTarget BuildTarget(
        int energy,
        decimal hunger,
        IEnumerable<(string Attribute, int Tier, decimal Value)> feps)
    {
        var lines = feps as IReadOnlyCollection<(string Attribute, int Tier, decimal Value)> ?? feps.ToList();
        var total = lines.Sum(f => f.Value);
        // +2 lines count double: winning the bar roll on one raises the stat by 2.
        var weightedTotal = lines.Sum(f => f.Value * f.Tier);
        var statTotals = lines
            .GroupBy(f => f.Attribute, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Value), StringComparer.OrdinalIgnoreCase);
        var statTierTotals = lines
            .GroupBy(f => f.Attribute.ToUpperInvariant() + f.Tier)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Value), StringComparer.OrdinalIgnoreCase);

        return new FepConditionTarget(
            total,
            weightedTotal,
            hunger > 0 ? total / hunger : 0m,
            hunger > 0 ? weightedTotal / hunger : 0m,
            hunger,
            energy,
            statTotals,
            statTierTotals);
    }
}
