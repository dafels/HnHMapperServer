using HnHMapperServer.Core.Enums;

namespace HnHMapperServer.Core.Constants;

/// <summary>
/// Access presets an admin can attach to a shareable invite link. The preset decides which permissions a
/// redeemer receives — the role is always TenantUser. Stored on the invitation as the expanded permission
/// list (so renaming a preset later never changes what an existing link grants).
/// </summary>
public static class InvitationPresets
{
    /// <summary>All five permissions, including Writer (edit/delete tiles and markers). The default.</summary>
    public const string Full = "Full";

    /// <summary>View everything and upload map data, but no Writer.</summary>
    public const string Contribute = "Contribute";

    public static readonly IReadOnlyList<Permission> FullPermissions = new[]
    {
        Permission.Map, Permission.Markers, Permission.Pointer, Permission.Upload, Permission.Writer
    };

    public static readonly IReadOnlyList<Permission> ContributePermissions = new[]
    {
        Permission.Map, Permission.Markers, Permission.Pointer, Permission.Upload
    };

    /// <summary>Expands a preset name to its permissions; unknown / blank names → Full (never empty).</summary>
    public static IReadOnlyList<Permission> Expand(string? preset) =>
        string.Equals(preset, Contribute, StringComparison.OrdinalIgnoreCase)
            ? ContributePermissions
            : FullPermissions;

    /// <summary>Maps a stored permission list back to a preset name for display.</summary>
    public static string NameFor(IReadOnlyCollection<Permission> permissions) =>
        permissions.Contains(Permission.Writer) ? Full : Contribute;

    public static bool IsKnown(string? preset) =>
        string.Equals(preset, Full, StringComparison.OrdinalIgnoreCase)
        || string.Equals(preset, Contribute, StringComparison.OrdinalIgnoreCase);
}
