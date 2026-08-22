namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// Cross-tenant map integrity report for superadmins: where two grid ids claim the same cell
/// (the fingerprint of coordinate-frame corruption) and any legacy placeholder grid rows.
/// </summary>
public class MapIntegrityReportDto
{
    public DateTime ScannedAt { get; set; }
    public int TenantsScanned { get; set; }
    public int TotalGrids { get; set; }

    /// <summary>One entry per (tenant, map) that has at least one contested cell.</summary>
    public List<MapIntegrityIssueDto> ContestedMaps { get; set; } = new();

    /// <summary>Legacy grid rows with the placeholder id "0" (guards prevent new ones).</summary>
    public List<PlaceholderGridRowDto> PlaceholderRows { get; set; } = new();

    /// <summary>Per tenant: storage left behind by deleted maps (merge/delete leftovers).</summary>
    public List<TenantOrphanStorageDto> OrphanStorage { get; set; } = new();

    public bool IsClean => ContestedMaps.Count == 0 && PlaceholderRows.Count == 0 && OrphanStorage.Count == 0;
}

/// <summary>
/// Orphaned map storage of one tenant: tile rows whose map no longer exists, per-map tile
/// directories (legacy zoom pyramid + WebP pyramid) of dead map ids, and grid PNGs in the shared
/// pool referenced by no tile row and no grid. All of it is invisible to viewers but counts
/// against the tenant's storage quota. Feeds the purge-orphans repair action.
/// </summary>
public class TenantOrphanStorageDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;

    /// <summary>Dead map ids that still have tile rows and/or directories on disk.</summary>
    public List<int> DeadMapIds { get; set; } = new();

    /// <summary>Tile rows (all zoom levels) on dead map ids. DB bloat, no disk cost.</summary>
    public int OrphanedTileRows { get; set; }

    /// <summary>Dead-map tile directories found on disk (legacy + WebP trees).</summary>
    public int DeadMapDirectories { get; set; }
    public long DeadMapDirectoryBytes { get; set; }

    /// <summary>Pool PNGs in grids/ referenced by no tile row and no grid row.</summary>
    public int UnreferencedGridFiles { get; set; }
    public long UnreferencedGridFileBytes { get; set; }

    public double ReclaimableMB =>
        Math.Round((DeadMapDirectoryBytes + UnreferencedGridFileBytes) / 1024.0 / 1024.0, 1);
}

/// <summary>
/// Request body for purging a tenant's orphaned map storage. <see cref="ConfirmTenantId"/> must
/// echo the route tenant id — the same deliberate speed bump the other destructive tools use.
/// </summary>
public class PurgeOrphanedMapDataRequestDto
{
    public string ConfirmTenantId { get; set; } = string.Empty;
}

/// <summary>What an orphan purge actually removed. Only provably-dead data is ever touched.</summary>
public class OrphanPurgeResultDto
{
    public string TenantId { get; set; } = string.Empty;

    public int TileRowsDeleted { get; set; }
    public int DirectoriesDeleted { get; set; }
    public long DirectoryBytesFreed { get; set; }
    public int GridFilesDeleted { get; set; }
    public long GridFileBytesFreed { get; set; }

    public double MegabytesFreed =>
        Math.Round((DirectoryBytesFreed + GridFileBytesFreed) / 1024.0 / 1024.0, 1);

    /// <summary>Tenant storage usage after the post-purge recalculation.</summary>
    public double StorageAfterMB { get; set; }

    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// One map with contested cells: how many, where (bounding box — feeds the wipe-region tool),
/// and a capped sample of the cells with the grid ids fighting over each.
/// </summary>
public class MapIntegrityIssueDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;

    public int ContestedCellCount { get; set; }

    // Bounding box of ALL contested cells on this map (inclusive grid coords).
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }

    /// <summary>A capped sample of contested cells, each with the grid ids fighting over it.</summary>
    public List<ContestedCellDto> SampleCells { get; set; } = new();
}

public class ContestedCellDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public List<string> GridIds { get; set; } = new();
}

public class PlaceholderGridRowDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}
