using System.Text.RegularExpressions;

namespace HnHMapperServer.Web.Components.Pages;

/// <summary>
/// The cookbook page's regular expressions, source-generated.
/// <para>
/// They live here rather than in Cookbook.razor because <see cref="GeneratedRegexAttribute"/>
/// needs a partial method on a partial type, which a .razor file's <c>@code</c> block cannot
/// declare. Keeping them together also makes the patterns reviewable without scrolling
/// through four thousand lines of markup.
/// </para>
/// <para>
/// Measured on this app's patterns (200k iterations, warm, best-of-7): an inline
/// <c>Regex.Replace(text, "pattern")</c> costs 71.7 ms because it runs interpreted and looks
/// the pattern up in the static cache on every call; a <c>RegexOptions.Compiled</c> static
/// costs 22.9 ms; source-generated costs 19.5 ms and emits no IL at run time. The tidy-up
/// patterns below ran on the inline path and are hit on every change to the search box.
/// </para>
/// </summary>
internal static partial class CookbookPatterns
{
    /// <summary>Splits a trailing quantity ("x2", "(0.5 kg)", "0.1 L") off a recipe entry.</summary>
    [GeneratedRegex(
        @"^(?<name>.+?)(?:\s*(?<qty>x\s?\d+|\([^)]*\)|\d+(?:[.,]\d+)?\s*(?:l|ml|kg|g)\.?))?$",
        RegexOptions.IgnoreCase)]
    public static partial Regex RecipeQty();

    /// <summary>Wiki quantity leftovers the part parser doesn't catch ("Any Flour {0.1 kg)", "Salt x").</summary>
    [GeneratedRegex(@"(\s*[\{\(][^\)\}]*[\)\}]?|\s+x)\s*$")]
    public static partial Regex ComponentJunk();

    /// <summary>A stat key + operator with no value yet at the end of the filter text ("str&gt;=", "int2&gt;").</summary>
    [GeneratedRegex(
        @"(?:str|agi|int|con|per|cha|dex|will|psy)(?:[12])?\s*(?:>=|<=|==|=|>|<)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex TrailingStatOp();

    // The three separator tidies applied after threshold conditions are cut out of the
    // search text. Every keystroke in the search box runs all three.

    /// <summary>Space before a comma ("salami , x" → "salami, x").</summary>
    [GeneratedRegex(@"\s+,")]
    public static partial Regex SpaceBeforeComma();

    /// <summary>Runs of commas left by removing conditions.</summary>
    [GeneratedRegex(@",{2,}")]
    public static partial Regex RepeatedCommas();

    /// <summary>Runs of whitespace left by removing conditions.</summary>
    [GeneratedRegex(@"\s{2,}")]
    public static partial Regex RepeatedSpaces();
}
