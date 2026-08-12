namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// The values threshold conditions evaluate against — one shape for base foods and
/// recipe variations, on both the client (table/variant filtering) and the server
/// (variant-aware match endpoint).
/// </summary>
public readonly record struct FepConditionTarget(
    decimal TotalFep,
    decimal FepPerHunger,
    decimal Hunger,
    int Energy,
    IReadOnlyDictionary<string, decimal> StatTotals,
    IReadOnlyDictionary<string, decimal> StatTierTotals);

/// <summary>
/// Evaluates parsed <see cref="FepFilterCondition"/>s against a target. Semantics:
/// percent = share of the target's own total FEP (quality-invariant); absolute
/// stat/total/eff values are quality-scaled by <c>multiplier</c> (WYSIWYG with the
/// table at the current Q input); hunger/energy are unscaled; every compared value
/// is rounded to 2 decimals so '=' and boundary values match what the UI displays.
/// </summary>
public static class FepConditionEvaluator
{
    public static bool Matches(
        in FepConditionTarget t,
        IReadOnlyList<FepFilterCondition> conditions,
        double qualityMultiplier)
    {
        foreach (var c in conditions)
        {
            var actual = Math.Round(Value(t, c, qualityMultiplier), 2);
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
    public static decimal Value(in FepConditionTarget t, FepFilterCondition c, double qualityMultiplier) =>
        c.Key switch
        {
            // Percent = share of the target's own total FEP; base values on both sides, so quality cancels.
            FepFilterKey.Stat when c.IsPercent =>
                t.TotalFep > 0 ? StatValue(t, c) / t.TotalFep * 100m : 0m,
            FepFilterKey.Stat => StatValue(t, c) * (decimal)qualityMultiplier,
            FepFilterKey.Total => t.TotalFep * (decimal)qualityMultiplier,
            FepFilterKey.Eff => t.FepPerHunger * (decimal)qualityMultiplier,
            FepFilterKey.Hunger => t.Hunger,
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
        var statTotals = lines
            .GroupBy(f => f.Attribute, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Value), StringComparer.OrdinalIgnoreCase);
        var statTierTotals = lines
            .GroupBy(f => f.Attribute.ToUpperInvariant() + f.Tier)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Value), StringComparer.OrdinalIgnoreCase);

        return new FepConditionTarget(
            total,
            hunger > 0 ? total / hunger : 0m,
            hunger,
            energy,
            statTotals,
            statTierTotals);
    }
}
