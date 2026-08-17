namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// DTO for listing all tenants (superadmin view)
/// </summary>
public class TenantListDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int StorageQuotaMB { get; set; }
    public double CurrentStorageMB { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public int UserCount { get; set; }
    public int TokenCount { get; set; }
    public DateTime? LastActivityAt { get; set; }
}

/// <summary>
/// DTO for viewing tenant details (superadmin view)
/// </summary>
public class TenantDetailsDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int StorageQuotaMB { get; set; }
    public double CurrentStorageMB { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public int UserCount { get; set; }
    public int TokenCount { get; set; }
    public List<TenantUserDto> Users { get; set; } = new();
    public double StorageUsagePercent => StorageQuotaMB > 0 ? (CurrentStorageMB / StorageQuotaMB) * 100 : 0;
}

/// <summary>
/// DTO for updating storage quota
/// </summary>
public class UpdateStorageQuotaDto
{
    public int StorageQuotaMB { get; set; }
}

/// <summary>
/// DTO for suspending/activating a tenant
/// </summary>
public class UpdateTenantStatusDto
{
    public bool IsActive { get; set; }
}

/// <summary>
/// DTO for cross-tenant map listing (superadmin view)
/// </summary>
public class GlobalMapDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public bool Hidden { get; set; }
    public int Priority { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TileCount { get; set; }
    public int MarkerCount { get; set; }
    public int CustomMarkerCount { get; set; }
}

/// <summary>
/// DTO for cross-tenant marker listing (superadmin view)
/// </summary>
public class GlobalMarkerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string GridId { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public string Image { get; set; } = string.Empty;
    public bool Hidden { get; set; }
    public bool Ready { get; set; }
    public long MaxReady { get; set; }
    public long MinReady { get; set; }
}

/// <summary>
/// DTO for cross-tenant custom marker listing (superadmin view)
/// </summary>
public class GlobalCustomMarkerDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public string GridId { get; set; } = string.Empty;
    public int CoordX { get; set; }
    public int CoordY { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string Icon { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime PlacedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>
/// DTO for enhanced tenant statistics (superadmin view)
/// </summary>
public class TenantStatisticsDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapCount { get; set; }
    public int GridCount { get; set; }
    public int TileCount { get; set; }
    public int MarkerCount { get; set; }
    public int CustomMarkerCount { get; set; }
    public int RoadCount { get; set; }
    public int TimerCount { get; set; }
    public int FoodCount { get; set; }
    public int FoodVariantCount { get; set; }
    public int UserCount { get; set; }
    public int TokenCount { get; set; }
    public double StorageUsageMB { get; set; }
    public int StorageQuotaMB { get; set; }
    public double StorageUsagePercent => StorageQuotaMB > 0 ? (StorageUsageMB / StorageQuotaMB) * 100 : 0;
}

/// <summary>
/// Request body for purging a tenant's content. <see cref="ConfirmTenantId"/> must match the
/// route tenant id — a deliberate speed bump on an irreversible, destructive operation.
/// </summary>
public class PurgeTenantDataRequestDto
{
    public string ConfirmTenantId { get; set; } = string.Empty;
}

/// <summary>
/// What a tenant content purge actually removed. Every count is rows/files deleted,
/// so a caller can show "freed X MB" without a second round trip.
/// </summary>
public class PurgeTenantDataResultDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;

    // Map data
    public int Maps { get; set; }
    public int Grids { get; set; }
    public int Tiles { get; set; }
    public int Markers { get; set; }
    public int CustomMarkers { get; set; }
    public int Roads { get; set; }
    public int Pings { get; set; }
    public int Overlays { get; set; }
    public int DirtyZoomTiles { get; set; }

    // Activity data derived from the map
    public int Timers { get; set; }
    public int TimerHistory { get; set; }
    public int Notifications { get; set; }

    // Public map source references that pointed at the wiped maps
    public int PublicMapSources { get; set; }

    // Filesystem
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public double MegabytesFreed => Math.Round(BytesFreed / 1024.0 / 1024.0, 2);

    /// <summary>Map ids that were removed, so the caller can invalidate caches / notify clients.</summary>
    public List<int> DeletedMapIds { get; set; } = new();

    /// <summary>Non-fatal problems (e.g. a locked file that could not be deleted).</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Preview of what a map-region wipe would remove, so a superadmin can see the blast radius
/// (counts + percent of the map) before committing. Read-only.
/// </summary>
public class MapRegionWipePreviewDto
{
    public string TenantId { get; set; } = string.Empty;
    public int MapId { get; set; }

    // Normalized inclusive box (X1 <= X2, Y1 <= Y2), in grid coordinates.
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }

    public int Grids { get; set; }
    public int Markers { get; set; }
    public int Zoom0Tiles { get; set; }
    public int Overlays { get; set; }

    public int MapTotalGrids { get; set; }
    public double PercentOfMap { get; set; }

    // Full extent of the map's grids (null when the map has no grids).
    public int? MapExtentMinX { get; set; }
    public int? MapExtentMinY { get; set; }
    public int? MapExtentMaxX { get; set; }
    public int? MapExtentMaxY { get; set; }
}

/// <summary>
/// Request body for wiping a map region. <see cref="ConfirmMapId"/> must match the route map id —
/// the same deliberate speed bump the tenant purge uses.
/// </summary>
public class WipeMapRegionRequestDto
{
    public int X1 { get; set; }
    public int X2 { get; set; }
    public int Y1 { get; set; }
    public int Y2 { get; set; }
    public int ConfirmMapId { get; set; }
}

/// <summary>
/// What a map-region wipe actually removed. Zoom 1-6 tile rows are deliberately kept (they heal
/// as later uploads regenerate the pyramid), so they are not counted here.
/// </summary>
public class MapRegionWipeResultDto
{
    public string TenantId { get; set; } = string.Empty;
    public int MapId { get; set; }

    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }

    public int Grids { get; set; }
    public int Markers { get; set; }
    public int Timers { get; set; }
    public int Zoom0Tiles { get; set; }
    public int Overlays { get; set; }

    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public double MegabytesFreed => Math.Round(BytesFreed / 1024.0 / 1024.0, 2);

    /// <summary>Non-fatal problems (e.g. a tile file that could not be deleted).</summary>
    public List<string> Warnings { get; set; } = new();
}
