namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// Where a food's headline (canonical) Energy/Hunger/FEP values came from.
///
/// The game client is the source of truth: it reports what the running world actually
/// gives, while the ringofbrodgar wiki is community-maintained and is not re-checked
/// every world — its numbers can be several worlds old (measured: 553 of 568 wiki-sourced
/// foods disagreed with the values the same tenant already had on file). Wiki data is
/// therefore only a last resort for values, and stays authoritative for descriptive
/// fields (recipe text, cooking station, satiation groups, page URL).
/// </summary>
public static class FoodValueSource
{
    /// <summary>ringofbrodgar page values — used only when nothing was ever observed.</summary>
    public const string Wiki = "Wiki";

    /// <summary>A game-data dump imported by an admin, or a restored cookbook export.</summary>
    public const string Import = "Import";

    /// <summary>A game client upload — a real observation, with a known world.</summary>
    public const string Upload = "Upload";

    /// <summary>
    /// Precedence: a real client observation beats an imported file, which beats the
    /// wiki. Unknown/absent values rank as wiki so any real data replaces them.
    /// </summary>
    public static int Rank(string? source) => source switch
    {
        Upload => 2,
        Import => 1,
        _ => 0
    };

    /// <summary>True when the value came from a game client rather than a file or the wiki.</summary>
    public static bool IsObserved(string? source) =>
        string.Equals(source, Upload, StringComparison.Ordinal);
}
