using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Service for constructing and validating tenant-specific file paths
/// Format: gridStorage/tenants/{tenantId}/grids/{gridId}.png
///         gridStorage/tenants/{tenantId}/{mapId}/{zoom}/{coord}.png
/// </summary>
public class TenantFilePathService : ITenantFilePathService
{
    /// <summary>
    /// Gets the tenant-specific grid file path
    /// </summary>
    public string GetGridFilePath(string tenantId, string gridId, string gridStorage)
    {
        var relativePath = GetGridRelativePath(tenantId, gridId);
        var fullPath = Path.Combine(gridStorage, relativePath);
        ValidatePath(tenantId, fullPath, gridStorage);
        return fullPath;
    }

    /// <summary>
    /// Gets the tenant-specific tile file path
    /// </summary>
    public string GetTileFilePath(string tenantId, int mapId, int zoom, string coordName, string gridStorage)
    {
        var relativePath = GetTileRelativePath(tenantId, mapId, zoom, coordName);
        var fullPath = Path.Combine(gridStorage, relativePath);
        ValidatePath(tenantId, fullPath, gridStorage);
        return fullPath;
    }

    /// <summary>
    /// Gets the relative path for database storage. Throws for a grid id that is not a plain
    /// file name (<see cref="IsSafeGridId"/>).
    /// </summary>
    public string GetGridRelativePath(string tenantId, string gridId)
    {
        return PoolRelativePath(tenantId, gridId)
            ?? throw new UnauthorizedAccessException($"Grid id is not a plain file name: '{gridId}'");
    }

    /// <summary>
    /// Grid ids come from game clients and become pool file names. A safe one is a single path
    /// segment: no separators, no "..", no drive colon, nothing the file system rejects.
    /// <see cref="ValidatePath"/> alone is not enough — "../5/0/1_1" stays inside the tenant
    /// directory while naming another map's file.
    /// </summary>
    public static bool IsSafeGridId(string? gridId) =>
        !string.IsNullOrWhiteSpace(gridId)
        && gridId.IndexOfAny(UnsafeGridIdChars) < 0
        && !gridId.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// The pool path tenants/{t}/grids/{gridId}.png (relative to GridStorage), or null for an
    /// unsafe grid id — for callers that must skip such ids rather than fail.
    /// </summary>
    public static string? PoolRelativePath(string tenantId, string gridId) =>
        IsSafeGridId(gridId) ? Path.Combine("tenants", tenantId, "grids", $"{gridId}.png") : null;

    private static readonly char[] UnsafeGridIdChars =
        Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).Distinct().ToArray();

    /// <summary>
    /// Gets the relative tile path for database storage
    /// </summary>
    public string GetTileRelativePath(string tenantId, int mapId, int zoom, string coordName)
    {
        return Path.Combine("tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{coordName}.png");
    }

    /// <summary>
    /// Gets the tenant directory path
    /// </summary>
    public string GetTenantDirectory(string tenantId, string gridStorage)
    {
        return Path.Combine(gridStorage, "tenants", tenantId);
    }

    /// <summary>
    /// Validates that a path is within the tenant directory (prevents traversal attacks)
    /// </summary>
    public void ValidatePath(string tenantId, string path, string gridStorage)
    {
        var tenantDir = GetTenantDirectory(tenantId, gridStorage);
        var fullTenantDir = Path.GetFullPath(tenantDir);
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(fullTenantDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Path traversal detected: Path '{fullPath}' is outside tenant directory '{fullTenantDir}'");
        }
    }

    /// <summary>
    /// Ensures tenant directories exist
    /// </summary>
    public void EnsureTenantDirectoriesExist(string tenantId, string gridStorage)
    {
        var tenantDir = GetTenantDirectory(tenantId, gridStorage);
        var gridsDir = Path.Combine(tenantDir, "grids");

        Directory.CreateDirectory(tenantDir);
        Directory.CreateDirectory(gridsDir);
    }
}
