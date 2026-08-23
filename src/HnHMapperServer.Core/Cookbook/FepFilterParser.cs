using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using System.Collections.Frozen;

namespace HnHMapperServer.Core.Cookbook;

/// <summary>Kind of value a cookbook threshold condition compares against.</summary>
public enum FepFilterKey
{
    /// <summary>A FEP stat (STR..PSY), optionally tier-specific.</summary>
    Stat,

    /// <summary>Total FEP of the food.</summary>
    Total,

    Hunger,

    Energy,

    /// <summary>FEP per hunger.</summary>
    Eff
}

public enum FepFilterOp
{
    Gt,
    Ge,
    Lt,
    Le,
    Eq
}

/// <summary>
/// One parsed threshold condition from the cookbook search box (e.g. "str&gt;50%", "int2&gt;=15").
/// Start/Length/RawText locate the exact span in the original search string, so the UI can
/// remove a condition by deleting its text.
/// </summary>
public sealed record FepFilterCondition(
    FepFilterKey Key,
    string? Attribute,
    int? Tier,
    FepFilterOp Op,
    decimal Value,
    bool IsPercent,
    int Start,
    int Length,
    string RawText);

/// <summary>
/// Extracts cediner-style threshold expressions ("str&gt;50%", "int2&gt;15", "hunger&lt;2") from
/// a free-text search string. Anything that does not form a valid condition stays in the residual
/// text untouched, so typos degrade to ordinary (0-result) text search instead of erroring.
/// </summary>
public static partial class FepFilterParser
{
    private static readonly FrozenSet<string> StatKeys = FrozenSet.ToFrozenSet(
        ["STR", "AGI", "INT", "CON", "PER", "CHA", "DEX", "WILL", "PSY"],
        StringComparer.OrdinalIgnoreCase);

    // \b before the key, a mandatory operator, and the trailing separator lookahead give token
    // semantics: "straw", "beefstr>5", "int21>5" and "str>50x" never match and stay ordinary text.
    [GeneratedRegex(
        @"\b(?<key>str|agi|int|con|per|cha|dex|will|psy|total|hunger|energy|eff)" +
        @"(?<tier>[12])?\s*(?<op>>=|<=|==|=|>|<)\s*(?<val>\d+(?:\.\d+)?)\s*(?<pct>%)?(?=$|[\s,])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConditionRegex();

    [GeneratedRegex(@"[\s,]+")]
    private static partial Regex SeparatorRegex();

    /// <summary>
    /// Splits a raw search string into accepted threshold conditions and the residual free text.
    /// With zero accepted conditions the input is returned byte-identical; otherwise leftover
    /// separators (commas, doubled spaces) around the extracted spans are collapsed.
    /// </summary>
    public static (IReadOnlyList<FepFilterCondition> Conditions, string Residual) Parse(string? search)
    {
        if (string.IsNullOrEmpty(search))
        {
            return (Array.Empty<FepFilterCondition>(), string.Empty);
        }

        List<FepFilterCondition>? conditions = null;
        StringBuilder? residual = null;
        var consumedTo = 0;

        foreach (Match match in ConditionRegex().Matches(search))
        {
            var condition = TryCreateCondition(match);
            if (condition == null)
            {
                // Invalid combination (e.g. "hunger>50%") — the span stays in the residual text.
                continue;
            }

            conditions ??= new List<FepFilterCondition>();
            residual ??= new StringBuilder(search.Length);
            conditions.Add(condition);
            residual.Append(search, consumedTo, match.Index - consumedTo);
            consumedTo = match.Index + match.Length;
        }

        if (conditions == null)
        {
            return (Array.Empty<FepFilterCondition>(), search);
        }

        residual!.Append(search, consumedTo, search.Length - consumedTo);
        var text = SeparatorRegex().Replace(residual.ToString(), " ").Trim();
        return (conditions, text);
    }

    private static FepFilterCondition? TryCreateCondition(Match match)
    {
        var keyText = match.Groups["key"].Value;
        var isStat = StatKeys.Contains(keyText);
        var hasTier = match.Groups["tier"].Success;
        var isPercent = match.Groups["pct"].Success;

        // Tiers and % shares only make sense on stat keys.
        if (!isStat && (hasTier || isPercent))
        {
            return null;
        }

        if (!decimal.TryParse(match.Groups["val"].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var key = isStat ? FepFilterKey.Stat : keyText.ToLowerInvariant() switch
        {
            "total" => FepFilterKey.Total,
            "hunger" => FepFilterKey.Hunger,
            "energy" => FepFilterKey.Energy,
            _ => FepFilterKey.Eff
        };

        var op = match.Groups["op"].Value switch
        {
            ">" => FepFilterOp.Gt,
            ">=" => FepFilterOp.Ge,
            "<" => FepFilterOp.Lt,
            "<=" => FepFilterOp.Le,
            _ => FepFilterOp.Eq // "=" and "=="
        };

        return new FepFilterCondition(
            key,
            isStat ? keyText.ToUpperInvariant() : null,
            hasTier ? match.Groups["tier"].Value[0] - '0' : (int?)null,
            op,
            value,
            isPercent,
            match.Index,
            match.Length,
            match.Value);
    }
}
