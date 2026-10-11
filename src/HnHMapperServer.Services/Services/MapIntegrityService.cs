using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Cross-tenant integrity scan + orphan purge. Every query uses IgnoreQueryFilters() — this runs
/// in a superadmin request whose ambient tenant context is a different tenant (or none), the same
/// rationale as TenantDataPurgeService / MapRegionWipeService.
///
/// The orphan model: merges and map deletions historically left behind (1) tile rows whose map id
/// no longer exists, (2) the dead maps' per-map tile directories (legacy zoom pyramid at
/// tenants/{t}/{mapId}/ and WebP pyramid at tenants/{t}/large/{mapId}/), and (3) pool PNGs in
/// tenants/{t}/grids/ that nothing references anymore. None of it is viewer-visible, all of it
/// counts against the tenant's storage quota.
///
/// Safety rule for deletion: only provably-dead data. A dead map's directory can still contain
/// files referenced by LIVE rows (public-map imports write zoom-0 tiles into per-map dirs and a
/// later merge copies the rows, File path unchanged, to the target map) — such directories are
/// kept and reported. Pool PNGs are kept if any tile row references them or a grid row with that
/// id exists (an upload can write the PNG before its tile row commits).
///
/// The opposite failure is reported too: live zoom-0 rows whose source PNG is gone (merges
/// used to delete imported maps' directories). Those rows are the restore key — exact
/// path and size — so the scan only reports them. The repair (RepairMissingTileSourcesAsync)
/// only repoints a row to a pool PNG of its cell's single owner grid and re-arms grids; rows are
/// deleted only by the explicit, audited forget, after their restore keys were recorded.
/// </summary>
public class MapIntegrityService : IMapIntegrityService
{
    /// <summary>Contested cells listed per map before the UI has to rely on the counts alone.</summary>
    public const int SampleCellCap = 12;

    // SQLite parameter-count safety for Contains() chunks.
    private const int ChunkSize = 500;

    private readonly ApplicationDbContext _db;
    private readonly IStorageQuotaService _quotaService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MapIntegrityService> _logger;

    public MapIntegrityService(
        ApplicationDbContext db,
        IStorageQuotaService quotaService,
        IConfiguration configuration,
        ILogger<MapIntegrityService> logger)
    {
        _db = db;
        _quotaService = quotaService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<MapIntegrityReportDto> ScanAsync(CancellationToken ct = default)
    {
        var report = new MapIntegrityReportDto
        {
            ScannedAt = DateTime.UtcNow,
            TenantsScanned = await _db.Tenants.IgnoreQueryFilters().CountAsync(ct),
            TotalGrids = await _db.Grids.IgnoreQueryFilters().CountAsync(ct)
        };

        // Cells claimed by more than one grid id — the corruption fingerprint. One aggregate
        // query over the whole Grids table; grouped per (tenant, map) afterwards.
        var contestedCells = await _db.Grids.IgnoreQueryFilters()
            .GroupBy(g => new { g.TenantId, g.Map, g.CoordX, g.CoordY })
            .Where(grp => grp.Count() > 1)
            .Select(grp => new
            {
                grp.Key.TenantId,
                grp.Key.Map,
                grp.Key.CoordX,
                grp.Key.CoordY
            })
            .ToListAsync(ct);

        var placeholderRows = await _db.Grids.IgnoreQueryFilters()
            .Where(g => g.Id == "0")
            .Select(g => new { g.TenantId, g.Map, g.CoordX, g.CoordY })
            .ToListAsync(ct);

        // Display names for everything involved.
        var tenantIds = contestedCells.Select(c => c.TenantId)
            .Concat(placeholderRows.Select(p => p.TenantId))
            .Distinct()
            .ToList();
        var mapIds = contestedCells.Select(c => c.Map).Distinct().ToList();

        var tenantNames = await _db.Tenants.IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var mapNames = await _db.Maps.IgnoreQueryFilters()
            .Where(m => mapIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, ct);

        foreach (var group in contestedCells
                     .GroupBy(c => (c.TenantId, c.Map))
                     .OrderByDescending(g => g.Count()))
        {
            ct.ThrowIfCancellationRequested();

            var cells = group.OrderBy(c => c.CoordX).ThenBy(c => c.CoordY).ToList();
            var issue = new MapIntegrityIssueDto
            {
                TenantId = group.Key.TenantId,
                TenantName = tenantNames.GetValueOrDefault(group.Key.TenantId, group.Key.TenantId),
                MapId = group.Key.Map,
                MapName = mapNames.GetValueOrDefault(group.Key.Map, $"map {group.Key.Map}"),
                ContestedCellCount = cells.Count,
                MinX = cells.Min(c => c.CoordX),
                MaxX = cells.Max(c => c.CoordX),
                MinY = cells.Min(c => c.CoordY),
                MaxY = cells.Max(c => c.CoordY)
            };

            // Owners for a capped sample of cells — indexed point lookups, a handful per issue.
            foreach (var cell in cells.Take(SampleCellCap))
            {
                var owners = await _db.Grids.IgnoreQueryFilters()
                    .Where(g => g.TenantId == group.Key.TenantId && g.Map == group.Key.Map
                                && g.CoordX == cell.CoordX && g.CoordY == cell.CoordY)
                    .Select(g => g.Id)
                    .ToListAsync(ct);
                issue.SampleCells.Add(new ContestedCellDto { X = cell.CoordX, Y = cell.CoordY, GridIds = owners });
            }

            report.ContestedMaps.Add(issue);
        }

        report.PlaceholderRows = placeholderRows
            .Select(p => new PlaceholderGridRowDto
            {
                TenantId = p.TenantId,
                TenantName = tenantNames.GetValueOrDefault(p.TenantId, p.TenantId),
                MapId = p.Map,
                X = p.CoordX,
                Y = p.CoordY
            })
            .ToList();

        await ScanOrphanStorageAsync(report, ct);
        await ScanWebpDriftAsync(report, ct);

        _logger.LogInformation(
            "Map integrity scan: {Tenants} tenants, {Grids} grids, {ContestedMaps} maps with contested cells, " +
            "{Placeholders} placeholder rows, {OrphanTenants} tenants with orphaned storage, " +
            "{DriftMaps} maps with WebP pyramid drift, {MissingSourceMaps} maps with missing tile source files",
            report.TenantsScanned, report.TotalGrids, report.ContestedMaps.Count,
            report.PlaceholderRows.Count, report.OrphanStorage.Count, report.WebpDrift.Count,
            report.MissingTileSources.Count);

        return report;
    }

    /// <summary>Drift examples listed per map before the UI has to rely on the counts alone.</summary>
    public const int DriftSampleCap = 8;

    /// <summary>
    /// A file is only stale when it predates the newest data in its footprint by more than this —
    /// the regeneration pipeline (queue + 5-minute backstop) needs a moment after an upload, and
    /// flagging that window would be pure noise.
    /// </summary>
    public static readonly TimeSpan StaleSlack = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Answers "which maps need a WebP rebuild?" by checking every pyramid file against the map's
    /// zoom-0 rows (the pyramid's ground truth). A zoom-z file at (X,Y) covers base coords
    /// [X·4·2^z, (X+1)·4·2^z): no rows in the footprint → ghost (renders deleted/moved terrain);
    /// file mtime older than the newest row in the footprint → stale (missing content added
    /// since); shorter than a WebP header → empty (residue of an interrupted write). Cells with
    /// rows but no zoom-0 file are only informational — generation heals those on view, but only
    /// where the cell's source PNGs exist. From the same per-tenant rows it also collects
    /// <see cref="MissingTileSourcesDto"/>: rows whose source PNG is gone, which no generation
    /// or rebuild can heal.
    /// </summary>
    private async Task ScanWebpDriftAsync(MapIntegrityReportDto report, CancellationToken ct)
    {
        var gridStorage = _configuration["GridStorage"] ?? "map";

        var tenantNames = await _db.Tenants.IgnoreQueryFilters()
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var liveMaps = await _db.Maps.IgnoreQueryFilters()
            .Select(m => new { m.TenantId, m.Id, m.Name })
            .ToListAsync(ct);

        foreach (var tenantGroup in liveMaps.GroupBy(m => m.TenantId))
        {
            ct.ThrowIfCancellationRequested();

            // One query per tenant: all zoom-0 rows, grouped per map in memory.
            var rows = await _db.Tiles.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantGroup.Key && t.Zoom == 0 && t.File != "")
                .Select(t => new ZoomZeroRow(t.MapId, t.CoordX, t.CoordY, t.Cache, t.File))
                .ToListAsync(ct);
            var rowsByMap = rows.GroupBy(r => r.MapId).ToDictionary(g => g.Key, g => g.ToList());

            // Source-file checks for this tenant: one directory listing per directory, not a
            // stat per row. Grid owners load only if some row is missing.
            var sourceFiles = new SourceFileIndex(gridStorage);
            var liveMapIds = tenantGroup.Select(m => m.Id).ToHashSet();
            Dictionary<(int Map, int X, int Y), List<string>>? gridOwners = null;

            foreach (var map in tenantGroup)
            {
                var largeDir = Path.Combine(gridStorage, "tenants", map.TenantId, "large", map.Id.ToString());
                var mapRows = rowsByMap.GetValueOrDefault(map.Id);

                if (mapRows is { Count: > 0 })
                {
                    var missingRows = mapRows.Where(r => !sourceFiles.Exists(r.File)).ToList();
                    if (missingRows.Count > 0)
                    {
                        gridOwners ??= await LoadGridOwnersAsync(map.TenantId, ct);
                        report.MissingTileSources.Add(BuildMissingTileSources(
                            map.TenantId, tenantNames.GetValueOrDefault(map.TenantId, map.TenantId),
                            map.Id, map.Name, mapRows, missingRows, liveMapIds, gridOwners, sourceFiles));
                    }
                }

                if ((mapRows == null || mapRows.Count == 0) && !Directory.Exists(largeDir))
                    continue;

                // Per-zoom aggregates: tile coord -> newest data timestamp in its footprint.
                var levels = new Dictionary<(int X, int Y), DateTime>[7];
                levels[0] = new Dictionary<(int X, int Y), DateTime>();
                foreach (var row in mapRows ?? new())
                {
                    var key = (FloorDiv(row.CoordX, 4), FloorDiv(row.CoordY, 4));
                    var when = CacheToUtc(row.Cache);
                    if (!levels[0].TryGetValue(key, out var current) || when > current)
                        levels[0][key] = when;
                }
                for (int z = 1; z <= 6; z++)
                {
                    levels[z] = new Dictionary<(int X, int Y), DateTime>();
                    foreach (var kv in levels[z - 1])
                    {
                        var key = (FloorDiv(kv.Key.X, 2), FloorDiv(kv.Key.Y, 2));
                        if (!levels[z].TryGetValue(key, out var current) || kv.Value > current)
                            levels[z][key] = kv.Value;
                    }
                }

                var drift = new WebpPyramidDriftDto
                {
                    TenantId = map.TenantId,
                    TenantName = tenantNames.GetValueOrDefault(map.TenantId, map.TenantId),
                    MapId = map.Id,
                    MapName = map.Name
                };

                // File mtimes per zoom level — needed for the derivation check below.
                var fileTimes = new Dictionary<(int X, int Y), DateTime>[7];

                for (int z = 0; z <= 6; z++)
                {
                    fileTimes[z] = new Dictionary<(int X, int Y), DateTime>();
                    var zoomDir = Path.Combine(largeDir, z.ToString());
                    if (!Directory.Exists(zoomDir))
                        continue;

                    foreach (var file in new DirectoryInfo(zoomDir).EnumerateFiles("*.webp"))
                    {
                        if (!TryParseTileName(file.Name, out var x, out var y))
                            continue;

                        // Zero-length/short file (FileInfo.Length comes with the enumeration, no
                        // extra IO). Not entered in fileTimes: it must not drive the derivation
                        // rule, and a zoom-0 one counts as a missing cell — which it is.
                        if (file.Length < WebpTileFile.HeaderLength)
                        {
                            drift.FilesChecked++;
                            drift.CorruptFiles++;
                            AddDriftSample(drift, z, x, y, "empty");
                            continue;
                        }

                        drift.FilesChecked++;
                        fileTimes[z][(x, y)] = file.LastWriteTimeUtc;

                        if (!levels[z].TryGetValue((x, y), out var newestData))
                        {
                            drift.GhostFiles++;
                            drift.GhostBytes += file.Length;
                            AddDriftSample(drift, z, x, y, "ghost");
                        }
                        else if (file.LastWriteTimeUtc + StaleSlack < newestData)
                        {
                            drift.StaleFiles++;
                            AddDriftSample(drift, z, x, y, "stale");
                        }
                        else if (z > 0 && IsStaleByDerivation(fileTimes[z - 1], x, y, file.LastWriteTimeUtc))
                        {
                            // Derivation rule: a parent composed BEFORE one of its children changed
                            // cannot reflect that child — even when its mtime beats the raw data
                            // (pre-fix chain regens baked ghost content from stale siblings into
                            // parents with fresh mtimes; only the child comparison exposes them).
                            drift.StaleFiles++;
                            AddDriftSample(drift, z, x, y, "stale (older than child)");
                        }
                    }
                }

                drift.MissingZoom0Cells = levels[0].Keys.Count(c => !fileTimes[0].ContainsKey(c));

                if (drift.GhostFiles > 0 || drift.StaleFiles > 0 || drift.CorruptFiles > 0)
                {
                    report.WebpDrift.Add(drift);
                }
            }
        }

        report.WebpDrift = report.WebpDrift
            .OrderByDescending(d => d.GhostFiles + d.StaleFiles + d.CorruptFiles)
            .ToList();
        report.MissingTileSources = report.MissingTileSources
            .OrderByDescending(m => m.DarkCells)
            .ThenByDescending(m => m.MissingRows)
            .ToList();
    }

    /// <summary>Zoom-0 tile row as the pyramid scans read it.</summary>
    private sealed record ZoomZeroRow(int MapId, int CoordX, int CoordY, long Cache, string File);

    /// <summary>
    /// Answers "does the file this row names exist?" exactly as tile generation resolves it
    /// (<c>Path.Combine(GridStorage, File)</c>), from one listing per directory: a tenant's pool
    /// holds ~100k PNGs, and a stat per row would make the scan seconds slower. A name absent from
    /// the listing is confirmed with a stat (written after the listing, or a case-only mismatch on
    /// a case-insensitive file system), so a miss is never a false alarm.
    /// </summary>
    private sealed class SourceFileIndex(string gridStorage)
    {
        private readonly Dictionary<string, HashSet<string>?> _listings = new(StringComparer.Ordinal);

        public bool Exists(string relativeFile)
        {
            var fullPath = Path.Combine(gridStorage, relativeFile);
            var dir = Path.GetDirectoryName(fullPath);
            var name = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(dir) || name.Length == 0)
            {
                return File.Exists(fullPath);
            }

            if (!_listings.TryGetValue(dir, out var names))
            {
                names = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f)!).ToHashSet(StringComparer.Ordinal)
                    : null;
                _listings[dir] = names;
            }

            return (names != null && names.Contains(name)) || File.Exists(fullPath);
        }
    }

    /// <summary>Grid ids per (map, cell) of one tenant — the owners a missing row could be repointed to.</summary>
    private async Task<Dictionary<(int Map, int X, int Y), List<string>>> LoadGridOwnersAsync(
        string tenantId, CancellationToken ct)
    {
        var grids = await _db.Grids.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId)
            .Select(g => new { g.Id, g.Map, g.CoordX, g.CoordY })
            .ToListAsync(ct);

        var owners = new Dictionary<(int Map, int X, int Y), List<string>>();
        foreach (var grid in grids)
        {
            var key = (grid.Map, grid.CoordX, grid.CoordY);
            if (!owners.TryGetValue(key, out var ids))
            {
                owners[key] = ids = new List<string>();
            }
            ids.Add(grid.Id);
        }
        return owners;
    }

    /// <summary>What the repair can do for one missing row (the scan predicts with the same rule).</summary>
    private enum MissingRowFix
    {
        /// <summary>Single owner grid with a pool PNG: point the row at it.</summary>
        Repoint,

        /// <summary>Single owner grid without a pool PNG: make it requestable so a client re-uploads it.</summary>
        Rearm,

        /// <summary>No owner grid, or one whose id is not a plain file name: only a restore helps.</summary>
        NoGrid,

        /// <summary>Several owner grids (contested cell): whose pool PNG would be the right one?</summary>
        Contested
    }

    private static (MissingRowFix Fix, string? OwnerId, string? PoolFile) ClassifyMissingRow(
        string tenantId, int mapId, int x, int y,
        Dictionary<(int Map, int X, int Y), List<string>> gridOwners, SourceFileIndex sourceFiles)
    {
        if (!gridOwners.TryGetValue((mapId, x, y), out var owners))
        {
            return (MissingRowFix.NoGrid, null, null);
        }

        if (owners.Count > 1)
        {
            return (MissingRowFix.Contested, null, null);
        }

        // Built only from a safe id, so the path is canonical and stays in the tenant's pool.
        var poolFile = TenantFilePathService.PoolRelativePath(tenantId, owners[0]);
        if (poolFile == null)
        {
            return (MissingRowFix.NoGrid, null, null);
        }

        return sourceFiles.Exists(poolFile)
            ? (MissingRowFix.Repoint, owners[0], poolFile)
            : (MissingRowFix.Rearm, owners[0], null);
    }

    /// <summary>WebP zoom-0 cell (4x4 base grids) of a row, floor-divided.</summary>
    private static (int MapId, int X, int Y) CellOf(int mapId, int x, int y) => (mapId, FloorDiv(x, 4), FloorDiv(y, 4));

    /// <summary>
    /// Describes one live map's rows whose source PNG is missing: how many, which 4x4 cells are
    /// completely or partly dark, what the repair could do per row (repoint to the single owner
    /// grid's pool PNG, re-arm the owner, nothing without an owner, skip a contested cell), which
    /// cells it would leave undrawn, and where the files used to live — per directory, with the
    /// merge shift (row coord minus the coord in the per-map file name), which identifies where
    /// to restore them from.
    /// </summary>
    private static MissingTileSourcesDto BuildMissingTileSources(
        string tenantId, string tenantName, int mapId, string mapName,
        List<ZoomZeroRow> mapRows, List<ZoomZeroRow> missingRows, HashSet<int> liveMapIds,
        Dictionary<(int Map, int X, int Y), List<string>> gridOwners, SourceFileIndex sourceFiles)
    {
        var entry = new MissingTileSourcesDto
        {
            TenantId = tenantId,
            TenantName = tenantName,
            MapId = mapId,
            MapName = mapName,
            MissingRows = missingRows.Count
        };

        // Dark = every row of the 4x4 cell is missing; partly dark = some (still drawn, without them).
        var rowsPerCell = mapRows.GroupBy(r => CellOf(mapId, r.CoordX, r.CoordY)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var cell in missingRows.GroupBy(r => CellOf(mapId, r.CoordX, r.CoordY)))
        {
            if (cell.Count() == rowsPerCell[cell.Key])
                entry.DarkCells++;
            else
                entry.PartlyDarkCells++;
        }

        var repointCells = new HashSet<(int, int, int)>();
        var stillMissingCells = new HashSet<(int, int, int)>();
        var byDirectory = new Dictionary<string, (int Rows, (int X, int Y)? Shift, bool Mixed)>(StringComparer.Ordinal);
        foreach (var row in missingRows)
        {
            var cell = CellOf(mapId, row.CoordX, row.CoordY);
            switch (ClassifyMissingRow(tenantId, mapId, row.CoordX, row.CoordY, gridOwners, sourceFiles).Fix)
            {
                case MissingRowFix.Repoint:
                    entry.RecoverableFromPool++;
                    repointCells.Add(cell);
                    break;
                case MissingRowFix.Contested:
                    entry.AmbiguousCells++;
                    stillMissingCells.Add(cell);
                    break;
                case MissingRowFix.NoGrid:
                    entry.RowsWithoutGrid++;
                    stillMissingCells.Add(cell);
                    break;
                case MissingRowFix.Rearm:
                    stillMissingCells.Add(cell);
                    break;
            }

            var (directory, shift) = DescribeSourceDirectory(row, mapId, liveMapIds);
            byDirectory[directory] = byDirectory.TryGetValue(directory, out var current)
                ? (current.Rows + 1, current.Shift, current.Mixed || !Nullable.Equals(current.Shift, shift))
                : (1, shift, false);

            if (entry.SampleFiles.Count < DriftSampleCap)
            {
                entry.SampleFiles.Add(row.File);
            }
        }

        entry.RepairHeldCells = repointCells.Count(stillMissingCells.Contains);

        entry.ByDirectory = byDirectory
            .OrderByDescending(kv => kv.Value.Rows)
            .Select(kv =>
            {
                var shift = kv.Value.Mixed
                    ? " shift=mixed"
                    : kv.Value.Shift is { } s ? $" shift=({s.X},{s.Y})" : string.Empty;
                return $"{kv.Key}{shift}: {kv.Value.Rows} rows";
            })
            .ToList();

        return entry;
    }

    /// <summary>
    /// The directory a missing row's file lived in. For a per-map zoom-0 file
    /// (tenants/{t}/{mapId}/0/{x}_{y}.png, what .hmap/public-map imports write) also the shift
    /// between the row's cell and the cell in the file name, and whether that map is gone.
    /// </summary>
    private static (string Directory, (int X, int Y)? Shift) DescribeSourceDirectory(
        ZoomZeroRow row, int mapId, HashSet<int> liveMapIds)
    {
        var segments = NormalizePath(row.File).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 5 && segments[0] == "tenants" && segments[3] == "0"
            && int.TryParse(segments[2], out var dirMapId)
            && segments[4].EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && TryParseTileName(segments[4][..^4], out var fileX, out var fileY))
        {
            var directory = $"tenants/{segments[1]}/{dirMapId}/0";
            if (dirMapId != mapId && !liveMapIds.Contains(dirMapId))
            {
                directory += " (dead map)";
            }
            return (directory, (row.CoordX - fileX, row.CoordY - fileY));
        }

        return (segments.Length > 1 ? string.Join('/', segments[..^1]) : "(storage root)", null);
    }


    /// <summary>
    /// True when any of the four child files at zoom-1 coords (2x+dx, 2y+dy) was written after
    /// the parent (beyond slack) — the parent's composite predates its own inputs.
    /// </summary>
    private static bool IsStaleByDerivation(
        Dictionary<(int X, int Y), DateTime> childTimes, int x, int y, DateTime parentMtimeUtc)
    {
        for (int dx = 0; dx <= 1; dx++)
        {
            for (int dy = 0; dy <= 1; dy++)
            {
                if (childTimes.TryGetValue((x * 2 + dx, y * 2 + dy), out var childMtime)
                    && parentMtimeUtc + StaleSlack < childMtime)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void AddDriftSample(WebpPyramidDriftDto drift, int zoom, int x, int y, string kind)
    {
        if (drift.SampleDriftTiles.Count < DriftSampleCap)
        {
            drift.SampleDriftTiles.Add($"z{zoom} ({x},{y}) {kind}");
        }
    }

    private static int FloorDiv(int value, int divisor) => (int)Math.Floor(value / (double)divisor);

    /// <summary>
    /// Tiles.Cache holds unix MILLISECONDS on the upload/merge/zoom paths but unix SECONDS on the
    /// hmap-import path. Anything below 10^12 (≈ Sep 2001 as ms) can only be a seconds value.
    /// </summary>
    private static DateTime CacheToUtc(long cache)
    {
        if (cache <= 0)
            return DateTime.MinValue;
        var ms = cache < 1_000_000_000_000 ? cache * 1000 : cache;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    }

    private static bool TryParseTileName(string fileName, out int x, out int y)
    {
        x = 0;
        y = 0;
        var stem = fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^5]
            : fileName;
        var parts = stem.Split('_');
        return parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
    }

    /// <summary>
    /// Per tenant: dead-map tile rows, dead-map directories on disk, and unreferenced pool PNGs.
    /// Uses the same live-reference guards as the purge, so the report shows exactly what a purge
    /// would remove.
    /// </summary>
    private async Task ScanOrphanStorageAsync(MapIntegrityReportDto report, CancellationToken ct)
    {
        var gridStorage = _configuration["GridStorage"] ?? "map";

        var tenants = await _db.Tenants.IgnoreQueryFilters()
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        var liveMapsByTenant = (await _db.Maps.IgnoreQueryFilters()
                .Select(m => new { m.TenantId, m.Id })
                .ToListAsync(ct))
            .GroupBy(m => m.TenantId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Id).ToHashSet());

        var rowCountsByTenantMap = (await _db.Tiles.IgnoreQueryFilters()
                .GroupBy(t => new { t.TenantId, t.MapId })
                .Select(g => new { g.Key.TenantId, g.Key.MapId, Rows = g.Count() })
                .ToListAsync(ct))
            .GroupBy(r => r.TenantId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var tenant in tenants)
        {
            ct.ThrowIfCancellationRequested();

            var liveIds = liveMapsByTenant.GetValueOrDefault(tenant.Id) ?? new HashSet<int>();
            var entry = new TenantOrphanStorageDto { TenantId = tenant.Id, TenantName = tenant.Name };
            var deadIds = new HashSet<int>();

            // 1. Tile rows on dead map ids.
            foreach (var agg in rowCountsByTenantMap.GetValueOrDefault(tenant.Id) ?? new())
            {
                if (!liveIds.Contains(agg.MapId))
                {
                    deadIds.Add(agg.MapId);
                    entry.OrphanedTileRows += agg.Rows;
                }
            }

            // 2 + 3 need the tenant's referenced-file sets; load lazily only when the tenant
            // has anything on disk to check.
            var tenantDir = Path.Combine(gridStorage, "tenants", tenant.Id);
            if (Directory.Exists(tenantDir))
            {
                var referenced = await LoadReferencedFilesAsync(tenant.Id, liveIds, ct);

                // 2. Dead-map directories (legacy + WebP trees), guarded against live references.
                foreach (var (dir, mapId) in EnumerateNumericMapDirs(tenantDir))
                {
                    if (liveIds.Contains(mapId))
                        continue;

                    deadIds.Add(mapId);
                    if (referenced.PathPrefixes.Contains(NormalizePath(Path.Combine("tenants", tenant.Id, mapId.ToString()))))
                        continue; // live rows reference files inside — purge keeps it too

                    entry.DeadMapDirectories++;
                    entry.DeadMapDirectoryBytes += DirectorySize(dir);
                }

                // 3. Unreferenced pool PNGs.
                var (count, bytes) = ScanUnreferencedPoolFiles(tenantDir, referenced);
                entry.UnreferencedGridFiles = count;
                entry.UnreferencedGridFileBytes = bytes;
            }

            if (entry.OrphanedTileRows > 0 || entry.DeadMapDirectories > 0 || entry.UnreferencedGridFiles > 0)
            {
                entry.DeadMapIds = deadIds.OrderBy(id => id).ToList();
                report.OrphanStorage.Add(entry);
            }
        }

        report.OrphanStorage = report.OrphanStorage
            .OrderByDescending(o => o.DeadMapDirectoryBytes + o.UnreferencedGridFileBytes)
            .ToList();
    }

    public async Task<OrphanPurgeResultDto> PurgeOrphanedMapDataAsync(string tenantId, CancellationToken ct = default)
    {
        var tenantExists = await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId, ct);
        if (!tenantExists)
        {
            throw new ArgumentException($"Tenant {tenantId} not found");
        }

        var gridStorage = _configuration["GridStorage"] ?? "map";
        var result = new OrphanPurgeResultDto { TenantId = tenantId };

        var liveIds = (await _db.Maps.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId)
                .Select(m => m.Id)
                .ToListAsync(ct))
            .ToHashSet();

        // 1. Delete tile rows on dead map ids FIRST — the reference sets below must only see
        //    rows that survive, so files referenced solely by dead rows become deletable.
        var deadRowMapIds = (await _db.Tiles.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId)
                .Select(t => t.MapId)
                .Distinct()
                .ToListAsync(ct))
            .Where(id => !liveIds.Contains(id))
            .ToList();

        foreach (var chunk in Chunk(deadRowMapIds))
        {
            result.TileRowsDeleted += await _db.Tiles.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && chunk.Contains(t.MapId))
                .ExecuteDeleteAsync(ct);
        }
        _db.ChangeTracker.Clear();

        var tenantDir = Path.Combine(gridStorage, "tenants", tenantId);
        if (Directory.Exists(tenantDir))
        {
            var referenced = await LoadReferencedFilesAsync(tenantId, liveIds, ct);

            // 2. Dead-map directories (legacy + WebP trees).
            foreach (var (dir, mapId) in EnumerateNumericMapDirs(tenantDir))
            {
                if (liveIds.Contains(mapId))
                    continue;

                if (referenced.PathPrefixes.Contains(NormalizePath(Path.Combine("tenants", tenantId, mapId.ToString()))))
                {
                    result.Warnings.Add(
                        $"Kept directory of dead map {mapId}: live tile rows still reference files inside it");
                    continue;
                }

                try
                {
                    var size = DirectorySize(dir);
                    Directory.Delete(dir, recursive: true);
                    result.DirectoriesDeleted++;
                    result.DirectoryBytesFreed += size;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete dead map directory {Dir}", dir);
                    result.Warnings.Add($"Could not delete '{dir}': {ex.Message}");
                }
            }

            // 3. Unreferenced pool PNGs.
            var gridsDir = Path.Combine(tenantDir, "grids");
            if (Directory.Exists(gridsDir))
            {
                foreach (var file in Directory.EnumerateFiles(gridsDir, "*.png"))
                {
                    ct.ThrowIfCancellationRequested();

                    var stem = Path.GetFileNameWithoutExtension(file);
                    if (referenced.GridStems.Contains(stem))
                        continue;

                    try
                    {
                        var size = new FileInfo(file).Length;
                        File.Delete(file);
                        result.GridFilesDeleted++;
                        result.GridFileBytesFreed += size;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete unreferenced pool PNG {File}", file);
                        result.Warnings.Add($"Could not delete '{Path.GetFileName(file)}': {ex.Message}");
                    }
                }
            }
        }

        result.StorageAfterMB = await _quotaService.RecalculateStorageUsageAsync(tenantId, gridStorage);

        _logger.LogWarning(
            "Purged orphaned map data for tenant {TenantId}: {Rows} tile rows, {Dirs} directories " +
            "({DirMB:F1} MB), {Files} pool PNGs ({FileMB:F1} MB); storage now {AfterMB:F1} MB",
            tenantId, result.TileRowsDeleted, result.DirectoriesDeleted,
            result.DirectoryBytesFreed / 1024.0 / 1024.0,
            result.GridFilesDeleted, result.GridFileBytesFreed / 1024.0 / 1024.0,
            result.StorageAfterMB);

        return result;
    }

    public async Task<MissingTileSourcesRepairResultDto> RepairMissingTileSourcesAsync(
        string tenantId, CancellationToken ct = default)
    {
        var tenantExists = await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId, ct);
        if (!tenantExists)
        {
            throw new ArgumentException($"Tenant {tenantId} not found");
        }

        var gridStorage = _configuration["GridStorage"] ?? "map";
        var result = new MissingTileSourcesRepairResultDto { TenantId = tenantId };

        // Re-derived here, never taken from the client: the same rows the scan reports.
        var liveMapIds = (await _db.Maps.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId)
                .Select(m => m.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var liveIdList = liveMapIds.ToList();
        var rows = await _db.Tiles.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.Zoom == 0 && t.File != "" && liveIdList.Contains(t.MapId))
            .Select(t => new { t.Id, t.MapId, t.CoordX, t.CoordY, t.File, t.FileSizeBytes, t.Cache })
            .ToListAsync(ct);

        var sourceFiles = new SourceFileIndex(gridStorage);
        var missing = rows.Where(r => !sourceFiles.Exists(r.File)).ToList();
        result.MissingRows = missing.Count;
        if (missing.Count == 0)
        {
            return result;
        }

        // Restore keys first, from the rows exactly as they are before anything changes.
        var sources = missing
            .Select(r => (Row: r, Source: DescribeSourceDirectory(
                new ZoomZeroRow(r.MapId, r.CoordX, r.CoordY, r.Cache, r.File), r.MapId, liveMapIds)))
            .ToList();
        var mixedDirectories = new HashSet<(int MapId, string Directory)>();
        result.RestoreKeys = sources
            .GroupBy(x => (x.Row.MapId, x.Source.Directory))
            .OrderBy(g => g.Key.MapId).ThenBy(g => g.Key.Directory, StringComparer.Ordinal)
            .Select(g =>
            {
                var shifts = g.Select(x => x.Source.Shift).Distinct().ToList();
                if (shifts.Count > 1)
                {
                    mixedDirectories.Add(g.Key);
                }
                var shift = shifts.Count > 1
                    ? " shift=mixed"
                    : shifts[0] is { } s ? $" shift=({s.X},{s.Y})" : string.Empty;
                return $"map {g.Key.MapId}: {g.Key.Directory}{shift} rows={g.Count()} bytes={g.Sum(x => (long)x.Row.FileSizeBytes)}";
            })
            .ToList();

        var owners = await LoadGridOwnersAsync(tenantId, ct);
        var fixes = sources
            .Select(x => (x.Row, x.Source, Fix: ClassifyMissingRow(tenantId, x.Row.MapId, x.Row.CoordX, x.Row.CoordY, owners, sourceFiles)))
            .ToList();

        // Cells that keep a missing row after the repair. Regeneration is per 4x4 cell and the
        // zoom-0 compose skips missing sources, so redrawing such a cell would drop those rows
        // from its WebP — possibly the last copy of them. Their repointed rows are only marked.
        var heldCells = fixes
            .Where(f => f.Fix.Fix != MissingRowFix.Repoint)
            .Select(f => CellOf(f.Row.MapId, f.Row.CoordX, f.Row.CoordY))
            .ToHashSet();

        var repoints = new List<(int Id, string File, int Size)>();
        var rearm = new HashSet<string>(StringComparer.Ordinal);
        var heldRepointedCells = new HashSet<(int, int, int)>();
        foreach (var (row, source, (fix, ownerId, poolFile)) in fixes)
        {
            switch (fix)
            {
                case MissingRowFix.Contested:
                    result.AmbiguousSkipped++; // whose pool PNG would be the right one?
                    continue;

                case MissingRowFix.Repoint:
                    repoints.Add((row.Id, poolFile!, (int)new FileInfo(Path.Combine(gridStorage, poolFile!)).Length));
                    AddCoord(result.RepointedBaseCoords, row.MapId, row.CoordX, row.CoordY);
                    var cell = CellOf(row.MapId, row.CoordX, row.CoordY);
                    if (heldCells.Contains(cell))
                        heldRepointedCells.Add(cell);
                    else
                        AddCoord(result.RedrawBaseCoords, row.MapId, row.CoordX, row.CoordY);
                    continue;

                case MissingRowFix.Rearm:
                    rearm.Add(ownerId!);
                    break;

                case MissingRowFix.NoGrid:
                    result.RowsWithoutGrid++; // nothing to re-request
                    break;
            }

            result.UnrecoverableRowIds.Add(row.Id);

            // A single-shift per-map directory rebuilds from its key; a pool PNG from its owner
            // grid. Anything else would lose its coordinate-to-file mapping in a forget.
            var keyed = source.Shift != null
                ? !mixedDirectories.Contains((row.MapId, source.Directory))
                : ownerId != null && string.Equals(Path.GetFileNameWithoutExtension(row.File), ownerId, StringComparison.Ordinal);
            if (!keyed)
            {
                result.UnkeyedRows.Add($"map {row.MapId} ({row.CoordX},{row.CoordY}) {row.File} bytes={row.FileSizeBytes}");
            }
        }
        result.CellsHeldForRestore = heldRepointedCells.Count;

        var now = DateTime.UtcNow;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var requestableAt = now.AddMinutes(-1);
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            foreach (var (id, file, size) in repoints)
            {
                result.RowsRepointed += await _db.Tiles.IgnoreQueryFilters()
                    .Where(t => t.TenantId == tenantId && t.Id == id)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(t => t.File, file)
                        .SetProperty(t => t.FileSizeBytes, size)
                        .SetProperty(t => t.Cache, nowMs), ct);
            }

            // Requestable again: the next client that sees the grid re-uploads it into the pool
            // (GridUpload's no-updates guard lets a re-upload through when the row's file is gone).
            foreach (var chunk in Chunk(rearm.ToList()))
            {
                result.GridsRearmed += await _db.Grids.IgnoreQueryFilters()
                    .Where(g => g.TenantId == tenantId && chunk.Contains(g.Id) && g.NextUpdate > requestableAt)
                    .ExecuteUpdateAsync(u => u.SetProperty(g => g.NextUpdate, requestableAt), ct);
            }

            await transaction.CommitAsync(ct);
        }

        // ExecuteUpdate bypasses the change tracker, which may still hold the old values.
        _db.ChangeTracker.Clear();

        result.RepointedMapIds = result.RepointedBaseCoords.Keys.OrderBy(id => id).ToList();
        result.RowsLeftForRestore = result.UnrecoverableRowIds.Count;

        _logger.LogWarning(
            "Repaired missing tile sources of tenant {TenantId}: {Missing} missing rows, {Repointed} repointed to the grid pool " +
            "on {Maps} map(s) ({Held} cell(s) held back from redraw), {Rearmed} grids re-armed, {Ambiguous} contested skipped, " +
            "{Unrecoverable} unrecoverable ({WithoutGrid} without a grid)",
            tenantId, result.MissingRows, result.RowsRepointed, result.RepointedMapIds.Count, result.CellsHeldForRestore,
            result.GridsRearmed, result.AmbiguousSkipped, result.UnrecoverableRowIds.Count, result.RowsWithoutGrid);

        return result;
    }

    private static void AddCoord(Dictionary<int, List<Coord>> coordsByMap, int mapId, int x, int y)
    {
        if (!coordsByMap.TryGetValue(mapId, out var coords))
        {
            coordsByMap[mapId] = coords = new List<Coord>();
        }
        coords.Add(new Coord(x, y));
    }

    public async Task<int> ForgetMissingTileSourceRowsAsync(
        string tenantId, IReadOnlyCollection<int> tileRowIds, CancellationToken ct = default)
    {
        var gridStorage = _configuration["GridStorage"] ?? "map";
        var forgotten = 0;

        foreach (var chunk in Chunk(tileRowIds.Distinct().ToList()))
        {
            var rows = await _db.Tiles.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.Zoom == 0 && chunk.Contains(t.Id))
                .Select(t => new { t.Id, t.File })
                .ToListAsync(ct);

            // A file restored since the repair read the rows makes its row renderable again.
            var stillMissing = rows
                .Where(r => r.File != "" && !File.Exists(Path.Combine(gridStorage, r.File)))
                .Select(r => r.Id)
                .ToList();
            if (stillMissing.Count == 0)
            {
                continue;
            }

            forgotten += await _db.Tiles.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && stillMissing.Contains(t.Id))
                .ExecuteDeleteAsync(ct);
        }

        _db.ChangeTracker.Clear();

        _logger.LogWarning("Forgot {Count} unrecoverable zoom-0 tile row(s) of tenant {TenantId}", forgotten, tenantId);
        return forgotten;
    }

    /// <summary>
    /// Everything a tenant's LIVE rows and grids still claim:
    /// - GridStems: PNG stems referenced by a live-map tile row's File under grids/ or owned by a grid row.
    /// - PathPrefixes: normalized "tenants/{t}/{mapId}" prefixes of every live-referenced File — detects
    ///   live references into per-map directories (public-import zoom-0 tiles survive merges there).
    /// Only rows on LIVE map ids count: a dead map's own rows must never protect its files, or the
    /// scan would report nothing deletable while the purge (which drops dead rows first) deletes it.
    /// </summary>
    private async Task<(HashSet<string> GridStems, HashSet<string> PathPrefixes)> LoadReferencedFilesAsync(
        string tenantId, HashSet<int> liveMapIds, CancellationToken ct)
    {
        var liveIdList = liveMapIds.ToList();
        var files = await _db.Tiles.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.File != "" && liveIdList.Contains(t.MapId))
            .Select(t => t.File)
            .Distinct()
            .ToListAsync(ct);

        var gridStems = new HashSet<string>(StringComparer.Ordinal);
        var pathPrefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var normalized = NormalizePath(file);
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (normalized.Contains("/grids/"))
            {
                var name = segments[^1];
                gridStems.Add(name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
            }
            else if (segments.Length >= 3 && segments[0] == "tenants")
            {
                // tenants/{tenantId}/{mapId}/... — remember the per-map prefix.
                pathPrefixes.Add($"tenants/{segments[1]}/{segments[2]}");
            }
        }

        // A grid row can exist whose PNG landed on disk before its tile row committed.
        var gridIds = await _db.Grids.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId)
            .Select(g => g.Id)
            .ToListAsync(ct);
        gridStems.UnionWith(gridIds);

        return (gridStems, pathPrefixes);
    }

    /// <summary>Numeric per-map dirs of a tenant: tenants/{t}/{mapId} and tenants/{t}/large/{mapId}.</summary>
    private static IEnumerable<(string Dir, int MapId)> EnumerateNumericMapDirs(string tenantDir)
    {
        foreach (var root in new[] { tenantDir, Path.Combine(tenantDir, "large") })
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (int.TryParse(Path.GetFileName(dir), out var mapId))
                {
                    yield return (dir, mapId);
                }
            }
        }
    }

    private static (int Count, long Bytes) ScanUnreferencedPoolFiles(
        string tenantDir, (HashSet<string> GridStems, HashSet<string> PathPrefixes) referenced)
    {
        var gridsDir = Path.Combine(tenantDir, "grids");
        if (!Directory.Exists(gridsDir))
            return (0, 0);

        var count = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(gridsDir, "*.png"))
        {
            if (referenced.GridStems.Contains(Path.GetFileNameWithoutExtension(file)))
                continue;

            count++;
            try { bytes += new FileInfo(file).Length; } catch { }
        }
        return (count, bytes);
    }

    private static long DirectorySize(string dir)
    {
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { size += new FileInfo(file).Length; } catch { }
        }
        return size;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static IEnumerable<List<T>> Chunk<T>(List<T> items)
    {
        for (var i = 0; i < items.Count; i += ChunkSize)
        {
            yield return items.GetRange(i, Math.Min(ChunkSize, items.Count - i));
        }
    }
}
