using HnHMapperServer.Core.Cookbook;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Builds the <see cref="ShippedIcons"/> manifest from the Web app's wwwroot, once per process
/// (the container's wwwroot is immutable; in dev a PNG added while the app runs loads from the
/// game's renderer until the next restart).
/// </summary>
public static class ShippedIconScanner
{
    /// <summary>
    /// Indexes every PNG under {webRoot}/gfx as an extension-less, forward-slash resource name.
    /// Returns <see cref="ShippedIcons.Unknown"/> (= local-first) when the path is missing,
    /// unreadable or holds no PNGs, so a broken deploy never sends every icon to the remote renderer.
    /// </summary>
    public static ShippedIcons Scan(string? webRootPath)
    {
        if (string.IsNullOrWhiteSpace(webRootPath))
            return ShippedIcons.Unknown;

        var gfx = Path.Combine(webRootPath, "gfx");
        if (!Directory.Exists(gfx))
            return ShippedIcons.Unknown;

        try
        {
            var names = Directory.EnumerateFiles(gfx, "*.png", SearchOption.AllDirectories)
                .Select(f => Path.ChangeExtension(Path.GetRelativePath(webRootPath, f), null)!
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();
            return names.Count == 0 ? ShippedIcons.Unknown : ShippedIcons.FromNames(names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ShippedIcons.Unknown;
        }
    }
}
