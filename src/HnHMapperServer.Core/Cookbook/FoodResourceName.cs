using System.Text;

namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// Canonicalizes the game-resource name a food is stored under (e.g.
/// "gfx/invobjs/leaf-brassica") so it can be used to build an icon URL.
///
/// Resource names arrive verbatim from game-client uploads, and some clients send
/// them with a scheme-like prefix ("f:gfx/invobjs/leaf-brassica"). A prefixed name
/// resolves neither against the icons under wwwroot nor against the game's own
/// resource renderer, so that food's icon is permanently broken. Normalizing on the
/// way in stops new rows from carrying a prefix; normalizing again when building the
/// URL repairs rows that already do, without a data migration.
/// </summary>
public static class FoodResourceName
{
    /// <summary>Scheme-like prefixes seen on uploaded resource names.</summary>
    private static readonly string[] StrippedPrefixes =
    {
        "file:",
        "f:"
    };

    /// <summary>
    /// Returns the bare resource path, or an empty string when nothing usable remains.
    /// </summary>
    public static string Normalize(string? resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return string.Empty;
        }

        var value = resourceName.Trim();

        // Looped: a value may have picked up more than one prefix ("f:file:gfx/...").
        bool stripped;
        do
        {
            stripped = false;
            foreach (var prefix in StrippedPrefixes)
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value[prefix.Length..].TrimStart();
                    stripped = true;
                    break;
                }
            }
        }
        while (stripped);

        // A leading slash would turn the "/" + name the local icon URL is built from
        // into a protocol-relative "//host/..." pointing at a foreign origin.
        value = value.TrimStart('/');

        // Real resource names are bare paths — every one of the ~2500 icons shipped
        // under wwwroot/gfx matches [a-z0-9/-], so dropping anything else is lossless
        // for legitimate data. It also keeps the name safe to interpolate into the
        // onerror JS string literal that the icon fallbacks build, which uploads would
        // otherwise be able to break out of with a quote.
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '/' or '.' or '_' or '-')
            {
                sb.Append(c);
            }
        }

        // A dot segment would escape the icon directory once the browser normalizes
        // the URL; no real resource name contains one, so treat it as unusable.
        var result = sb.ToString();
        return result.Contains("..", StringComparison.Ordinal) ? string.Empty : result;
    }
}
