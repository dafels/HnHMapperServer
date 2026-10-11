using System.Text.Json.Serialization;
using HnHMapperServer.Core.Models;

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

    /// <summary>Live maps with zoom-0 tile rows whose source PNG no longer exists on disk.</summary>
    public List<MissingTileSourcesDto> MissingTileSources { get; set; } = new();

    public bool IsClean => ContestedMaps.Count == 0 && PlaceholderRows.Count == 0
        && OrphanStorage.Count == 0 && WebpDrift.Count == 0 && MissingTileSources.Count == 0;
}

/// <summary>
/// WebP pyramid drift of one live map, measured file-by-file against the map's zoom-0 rows
/// (the pyramid's ground truth): ghost files whose footprint holds no data anymore (they render
/// imagery of deleted/moved terrain), stale files older than the newest data in their
/// footprint (they miss content added since), and empty files (zero-length residue of an
/// interrupted write). Any kind means the map needs a WebP rebuild. Missing zoom-0 files are
/// informational only — generation heals those on view, but only where the cell's source PNGs
/// still exist; cells whose sources are gone show under <see cref="MissingTileSourcesDto"/>.
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

    /// <summary>
    /// Files (any zoom) shorter than a WebP header — zero-length residue of interrupted writes:
    /// served as broken images, and their parents were composed without them.
    /// </summary>
    public int CorruptFiles { get; set; }

    /// <summary>
    /// Cells with zoom-0 rows but no zoom-0 WebP file (informational: heals on view when the
    /// cell's source PNGs exist; see <see cref="MissingTileSourcesDto"/> when they do not).
    /// </summary>
    public int MissingZoom0Cells { get; set; }

    /// <summary>Total pyramid files checked for this map.</summary>
    public int FilesChecked { get; set; }

    /// <summary>Up to a handful of "z{zoom} (x,y)" examples for orientation.</summary>
    public List<string> SampleDriftTiles { get; set; } = new();
}

/// <summary>
/// Zoom-0 tile rows of one live map whose source PNG no longer exists on disk — typically a map
/// merge that deleted an imported map's per-map directory (merges no longer do), or files lost on
/// disk. Nothing can draw these cells (the WebP pyramid stays dark there at every zoom, and a
/// rebuild cannot help), but each row still names the exact original path and size, so restoring
/// the files to those paths from a backup heals them. Rows on dead maps are orphan storage, not
/// counted here.
/// </summary>
public class MissingTileSourcesDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;

    /// <summary>Zoom-0 rows whose file does not exist.</summary>
    public int MissingRows { get; set; }

    /// <summary>4x4 WebP zoom-0 cells in which EVERY row's file is missing: rendered as nothing.</summary>
    public int DarkCells { get; set; }

    /// <summary>
    /// 4x4 cells in which some rows' files are missing and others exist: drawn without the
    /// missing rows, so any regeneration (rebuild included) drops what the old WebP still shows
    /// of them.
    /// </summary>
    public int PartlyDarkCells { get; set; }

    /// <summary>
    /// Missing rows whose grid cell has exactly one owner grid with a PNG in the grid pool
    /// (tenants/{t}/grids/{gridId}.png): repointing the row there would restore the imagery.
    /// </summary>
    public int RecoverableFromPool { get; set; }

    /// <summary>Missing rows whose grid cell is claimed by more than one grid (contested).</summary>
    public int AmbiguousCells { get; set; }

    /// <summary>
    /// Missing rows whose cell has no (usable) owner grid: nothing to repoint to and nothing a
    /// client could be asked to re-upload. Only a file restore brings them back.
    /// </summary>
    public int RowsWithoutGrid { get; set; }

    /// <summary>
    /// Cells where the repair would repoint some rows while others stay missing. The repair does
    /// not redraw those (the old WebP may be the last copy of the missing rows); they are redrawn
    /// once the rest are restored or forgotten.
    /// </summary>
    public int RepairHeldCells { get; set; }

    /// <summary>
    /// Missing rows per directory, e.g. "tenants/t/17/0 (dead map) shift=(8,-4): 1200 rows" —
    /// the shift (row coord minus file-name coord) identifies the merge that moved them.
    /// </summary>
    public List<string> ByDirectory { get; set; } = new();

    /// <summary>A capped sample of the missing paths, as stored in the rows.</summary>
    public List<string> SampleFiles { get; set; } = new();
}

/// <summary>
/// Request body for repairing a tenant's missing tile sources. <see cref="ConfirmTenantId"/> must
/// echo the route tenant id. <see cref="ForgetUnrecoverable"/> (opt-in) additionally deletes the
/// rows nothing can recover — only after their restore keys were written to the audit log.
/// </summary>
public class RepairMissingTileSourcesRequestDto
{
    public string ConfirmTenantId { get; set; } = string.Empty;
    public bool ForgetUnrecoverable { get; set; }
}

/// <summary>What a missing-tile-source repair did for one tenant.</summary>
public class MissingTileSourcesRepairResultDto
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Live-map zoom-0 rows whose source PNG was missing when the repair started.</summary>
    public int MissingRows { get; set; }

    /// <summary>Rows repointed to their cell's single owner grid's pool PNG.</summary>
    public int RowsRepointed { get; set; }

    /// <summary>Owner grids made requestable again (NextUpdate in the past) so clients re-upload them.</summary>
    public int GridsRearmed { get; set; }

    /// <summary>Missing rows in contested cells (several owner grids): left to the wipe-region tool.</summary>
    public int AmbiguousSkipped { get; set; }

    /// <summary>Unrecoverable rows kept: they name the exact path + size to restore from a backup.</summary>
    public int RowsLeftForRestore { get; set; }

    /// <summary>Unrecoverable rows deleted (only with ForgetUnrecoverable).</summary>
    public int RowsForgotten { get; set; }

    /// <summary>Unrecoverable rows whose cell has no (usable) owner grid: nothing to re-arm.</summary>
    public int RowsWithoutGrid { get; set; }

    /// <summary>Repointed WebP cells handed to the fast queue now; the backstop takes the rest.</summary>
    public int CellsEnqueued { get; set; }

    /// <summary>
    /// Cells with repointed rows that were NOT redrawn because other rows in them are still
    /// missing — a redraw would drop those rows' imagery from the old WebP, possibly its last
    /// copy. Marked durably: the backstop redraws them once nothing in them is missing (restored
    /// or forgotten); later than that, the drift scan reports them stale for a rebuild.
    /// </summary>
    public int CellsHeldForRestore { get; set; }

    public List<int> RepointedMapIds { get; set; } = new();

    /// <summary>
    /// Where the missing files lived, per map and source directory:
    /// "map 12: tenants/t/17/0 (dead map) shift=(8,-4) rows=1200 bytes=20971520". With the
    /// shift (row coord minus file-name coord) the rows of a single-shift per-map directory can be
    /// rebuilt from restored files even after a forget, and a pool PNG's row from its owner grid;
    /// every other forgotten row (mixed shift, no owner grid) is listed one by one in the forget's
    /// audit entry (<see cref="UnkeyedRows"/>). Also stored in the audit entry.
    /// </summary>
    public List<string> RestoreKeys { get; set; } = new();

    /// <summary>Repointed base coords per map: all of them are marked dirty (not serialized).</summary>
    [JsonIgnore]
    public Dictionary<int, List<Coord>> RepointedBaseCoords { get; set; } = new();

    /// <summary>
    /// The repointed base coords whose cells hold no row still missing: these are redrawn now
    /// (not serialized). The others are <see cref="CellsHeldForRestore"/>.
    /// </summary>
    [JsonIgnore]
    public Dictionary<int, List<Coord>> RedrawBaseCoords { get; set; } = new();

    /// <summary>Ids of the unrecoverable rows (single owner without a pool PNG, or no owner).</summary>
    [JsonIgnore]
    public List<int> UnrecoverableRowIds { get; set; } = new();

    /// <summary>
    /// "map M (x,y) {file} bytes=N" for each unrecoverable row the restore keys cannot rebuild
    /// (mixed-shift directory, or no owner grid naming its file): written to the audit entry
    /// before a forget deletes them (not serialized).
    /// </summary>
    [JsonIgnore]
    public List<string> UnkeyedRows { get; set; } = new();
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

    /// <summary>
    /// Drifted maps NOT rebuilt because they have missing tile sources ("tenant/mapId (name)"): a
    /// rebuild deletes the whole pyramid, including the last WebP copy of their dark areas.
    /// Restore (or forget) first, then rebuild them one by one.
    /// </summary>
    public List<string> SkippedMissingSources { get; set; } = new();

    /// <summary>
    /// Drifted maps NOT rebuilt because their only drift is empty files: serving discards and
    /// regenerates those, and the batch rebuilds them — no reason to delete the whole pyramid.
    /// </summary>
    public int SkippedEmptyFilesOnly { get; set; }
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
