namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// Registry of known Haven &amp; Hearth game worlds. Game clients tag cookbook food uploads
/// with an opaque per-world "genus" hash (the server passes it as the third gameui widget
/// argument); this maps the hashes seen in the wild to display names and a release order.
/// Unknown hashes still flow through ingestion and filtering — they just render shortened
/// until they're added here.
/// </summary>
public static class GameWorlds
{
    /// <summary>Longest genus value accepted from clients — real hashes are 16 hex chars.</summary>
    public const int MaxGenusLength = 64;

    /// <summary>
    /// Reserved world key meaning "no world tag" in API world parameters
    /// (safe: real genus values are hex hashes, never this word).
    /// </summary>
    public const string UntaggedSentinel = "untagged";

    public sealed record KnownWorld(string Genus, string Name, int Order);

    /// <summary>Known worlds, oldest first. Newest = highest <see cref="KnownWorld.Order"/>.</summary>
    public static readonly IReadOnlyList<KnownWorld> Known = new[]
    {
        new KnownWorld("c646473983afec09", "W16", 1),
        new KnownWorld("b7c199a4557503a8", "W16.1", 2),
        // W16.2: add its genus hash + Order 3 when the world launches.
    };

    /// <summary>
    /// Sanitizes a client-supplied genus value for storage: trimmed, or null when
    /// missing, whitespace, or implausibly long.
    /// </summary>
    public static string? Normalize(string? genus)
    {
        if (string.IsNullOrWhiteSpace(genus))
        {
            return null;
        }

        var trimmed = genus.Trim();
        return trimmed.Length > MaxGenusLength ? null : trimmed;
    }

    /// <summary>Display name for a genus hash — "W16.1" when known, else a shortened hash.</summary>
    public static string DisplayName(string genus)
    {
        foreach (var world in Known)
        {
            if (world.Genus == genus)
            {
                return world.Name;
            }
        }

        return genus.Length > 8 ? genus[..8] + "…" : genus;
    }

    /// <summary>Release order of a genus hash; -1 for unknown worlds (sorts after known ones).</summary>
    public static int OrderOf(string genus)
    {
        foreach (var world in Known)
        {
            if (world.Genus == genus)
            {
                return world.Order;
            }
        }

        return -1;
    }
}
