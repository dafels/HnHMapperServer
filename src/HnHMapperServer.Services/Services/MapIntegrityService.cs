using HnHMapperServer.Core.DTOs;
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
            "{DriftMaps} maps with WebP pyramid drift",
            report.TenantsScanned, report.TotalGrids, report.ContestedMaps.Count,
            report.PlaceholderRows.Count, report.OrphanStorage.Count, report.WebpDrift.Count);

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
    /// since). Cells with rows but no zoom-0 file are only informational — on-the-fly generation
    /// heals those the moment someone looks.
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
                .Select(t => new { t.MapId, t.CoordX, t.CoordY, t.Cache })
                .ToListAsync(ct);
            var rowsByMap = rows.GroupBy(r => r.MapId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var map in tenantGroup)
            {
                var largeDir = Path.Combine(gridStorage, "tenants", map.TenantId, "large", map.Id.ToString());
                var mapRows = rowsByMap.GetValueOrDefault(map.Id);

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
                var zoom0Files = new HashSet<(int X, int Y)>();

                for (int z = 0; z <= 6; z++)
                {
                    var zoomDir = Path.Combine(largeDir, z.ToString());
                    if (!Directory.Exists(zoomDir))
                        continue;

                    foreach (var file in new DirectoryInfo(zoomDir).EnumerateFiles("*.webp"))
                    {
                        if (!TryParseTileName(file.Name, out var x, out var y))
                            continue;

                        drift.FilesChecked++;
                        if (z == 0)
                            zoom0Files.Add((x, y));

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
                    }
                }

                drift.MissingZoom0Cells = levels[0].Keys.Count(c => !zoom0Files.Contains(c));

                if (drift.GhostFiles > 0 || drift.StaleFiles > 0)
                {
                    report.WebpDrift.Add(drift);
                }
            }
        }

        report.WebpDrift = report.WebpDrift
            .OrderByDescending(d => d.GhostFiles + d.StaleFiles)
            .ToList();
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
