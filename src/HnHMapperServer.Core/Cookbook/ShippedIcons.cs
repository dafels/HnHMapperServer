using System.Collections.Frozen;

namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// Which game-resource icons ship under the Web app's wwwroot/gfx, as extension-less,
/// forward-slash resource names ("gfx/invobjs/lobster"). Pure: the disk scan that builds it lives
/// in Services (ShippedIconScanner) and runs once at Web startup. <see cref="Unknown"/> (the scan
/// could not run) answers "maybe" for everything, so callers keep today's local-first behaviour.
/// </summary>
public sealed class ShippedIcons
{
    private readonly FrozenSet<string>? _names;

    private ShippedIcons(FrozenSet<string>? names) => _names = names;

    /// <summary>Manifest unavailable: every icon may exist locally.</summary>
    public static ShippedIcons Unknown { get; } = new(null);

    /// <summary>Ordinal (case-sensitive) like the Linux static file server that serves them.</summary>
    public static ShippedIcons FromNames(IEnumerable<string> names) =>
        new(names.ToFrozenSet(StringComparer.Ordinal));

    public bool IsKnown => _names is not null;

    public int Count => _names?.Count ?? 0;

    /// <summary>False only when the manifest is known and the resource is not in it.</summary>
    public bool MayContain(string resourceName) => _names is null || _names.Contains(resourceName);
}
