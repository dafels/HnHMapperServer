using System.Text.RegularExpressions;

namespace HnHMapperServer.Core.PublicMaps;

/// <summary>
/// Turns a public map's display name into its URL slug.
/// <para>
/// This lived in two places: PublicMapService (authoritative, source-generated regexes) and
/// the create dialog's live preview (a copy, with interpreted regexes). Two copies of a
/// naming rule means the preview can quietly stop matching the slug the server actually
/// assigns, so both now call this.
/// </para>
/// </summary>
public static partial class PublicMapSlug
{
    /// <summary>Fallback when a name has no slug-able characters at all.</summary>
    public const string Fallback = "public-map";

    /// <summary>
    /// Lowercases, replaces anything outside [a-z0-9-] with hyphens, collapses hyphen runs,
    /// then pads short results and truncates long ones. Blank input yields
    /// <see cref="Fallback"/> — callers that want to show nothing should check first.
    /// </summary>
    public static string Generate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback;
        }

        var slug = name.ToLowerInvariant();
        slug = InvalidChars().Replace(slug, "-");
        slug = MultipleHyphens().Replace(slug, "-");
        slug = slug.Trim('-');

        if (slug.Length < 3)
        {
            slug = $"map-{slug}";
        }

        if (slug.Length > 50)
        {
            slug = slug[..50].TrimEnd('-');
        }

        return slug;
    }

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex InvalidChars();

    [GeneratedRegex(@"-+")]
    private static partial Regex MultipleHyphens();
}
