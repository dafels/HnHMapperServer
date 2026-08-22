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

    /// <summary>Live maps whose WebP tile pyramid disagrees with their zoom-0 data.</summary>
    public List<WebpPyramidDriftDto> WebpDrift { get; set; } = new();

    public bool IsClean => ContestedMaps.Count == 0 && PlaceholderRows.Count == 0
        && OrphanStorage.Count == 0 && WebpDrift.Count == 0;
}

/// <summary>
/// WebP pyramid drift of one live map, measured file-by-file against the map's zoom-0 rows
/// (the pyramid's ground truth): ghost files whose footprint holds no data anymore (they render
/// imagery of deleted/moved terrain) and stale files older than the newest data in their
/// footprint (they miss content added since). Either kind means the map needs a WebP rebuild.
/// Missing zoom-0 files are informational only — on-the-fly generation heals those on view.
/// </summary>
public class WebpPyramidDriftDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;

    /// <summary>Files (any zoom) whose footprint contains no zoom-0 rows at all.</summary>
    public int GhostFiles { get; set; }
    public long GhostBytes { get; set; }

    /// <summary>Files (any zoom) older than the newest zoom-0 row in their footprint.</summary>
    public int StaleFiles { get; set; }

    /// <summary>Cells with zoom-0 rows but no zoom-0 WebP file (heals on view; informational).</summary>
    public int MissingZoom0Cells { get; set; }

    /// <summary>Total pyramid files checked for this map.</summary>
    public int FilesChecked { get; set; }

    /// <summary>Up to a handful of "z{zoom} (x,y)" examples for orientation.</summary>
    public List<string> SampleDriftTiles { get; set; } = new();
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

/// <summary>Request body for rebuilding the WebP pyramids of ALL currently drifted maps.</summary>
public class RebuildAllDriftRequestDto
{
    public bool ConfirmAll { get; set; }
}

/// <summary>Result of a rebuild-all-drift run: one entry aggregated across every drifted map.</summary>
public class RebuildAllDriftResultDto
{
    public int MapsRebuilt { get; set; }
    public int FilesDeleted { get; set; }
    public double MegabytesFreed { get; set; }
    public int CellsMarked { get; set; }

    /// <summary>Cells pushed onto the fast queue; the rest follow via the 5-minute scan.</summary>
    public int CellsEnqueued { get; set; }

    /// <summary>"tenant/mapId (name)" per rebuilt map.</summary>
    public List<string> Maps { get; set; } = new();
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
