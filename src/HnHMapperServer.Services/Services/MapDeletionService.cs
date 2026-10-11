using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// See <see cref="IMapDeletionService"/>. Replaces two diverging teardowns: the tenant-admin
/// delete removed the rows and tenants/{t}/{mapId} but never the WebP pyramid (dead
/// large/{id} trees piled up) and deleted every file its rows named,
/// even PNGs other maps' rows still used; the superadmin delete removed only the Maps row — Tiles
/// and Grids have no foreign key to Maps, so they stayed behind on the dead id and the next
/// gridUpdate re-anchored to it.
///
/// Every query uses IgnoreQueryFilters() plus an explicit TenantId predicate (same rationale as
/// MapRegionWipeService / TenantDataPurgeService). Rows go first, in one transaction, child
/// before parent; files after the commit, so a failed delete leaves recoverable files rather
/// than a live map without them. A file is deleted only when no tile row of a LIVE map
/// references it (the reference rule of the merge, the integrity scan and the orphan purge).
/// </summary>
public class MapDeletionService : IMapDeletionService
{
    // SQLite parameter-count safety for Contains() chunks.
    private const int ChunkSize = 500;

    private readonly ApplicationDbContext _db;
    private readonly ITileService _tileService;
    private readonly ILargeTileService _largeTileService;
    private readonly IStorageQuotaService _quotaService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MapDeletionService> _logger;

    public MapDeletionService(
        ApplicationDbContext db,
        ITileService tileService,
        ILargeTileService largeTileService,
        IStorageQuotaService quotaService,
        IConfiguration configuration,
        ILogger<MapDeletionService> logger)
    {
        _db = db;
        _tileService = tileService;
        _largeTileService = largeTileService;
        _quotaService = quotaService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<MapDeletionResultDto> DeleteMapAsync(string tenantId, int mapId, CancellationToken ct = default)
    {
        var map = await _db.Maps.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mapId && m.TenantId == tenantId, ct)
            ?? throw new ArgumentException($"Map {mapId} not found in tenant {tenantId}");

        var gridStorage = _configuration["GridStorage"] ?? "map";
        var result = new MapDeletionResultDto { TenantId = tenantId, MapId = mapId, MapName = map.Name };

        // Captured up front — the rows are gone by the time files are deleted.
        var gridIds = await _db.Grids.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId && g.Map == mapId)
            .Select(g => g.Id)
            .ToListAsync(ct);

        var tileFiles = await _db.Tiles.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.MapId == mapId && t.File != "")
            .Select(t => t.File)
            .ToListAsync(ct);

        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            result.DirtyMarks = await _db.DirtyWebpCells
                .Where(d => d.TenantId == tenantId && d.MapId == mapId)
                .ExecuteDeleteAsync(ct);
            result.DirtyMarks += await _db.DirtyZoomTiles.IgnoreQueryFilters()
                .Where(d => d.TenantId == tenantId && d.MapId == mapId)
                .ExecuteDeleteAsync(ct);

            // Markers belong to grids, not maps. Timers pointing at them are nulled by the FK.
            foreach (var chunk in Chunk(gridIds))
            {
                result.Markers += await _db.Markers.IgnoreQueryFilters()
                    .Where(m => m.TenantId == tenantId && chunk.Contains(m.GridId))
                    .ExecuteDeleteAsync(ct);
            }

            result.CustomMarkers = await _db.CustomMarkers.IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && c.MapId == mapId)
                .ExecuteDeleteAsync(ct);

            result.Roads = await _db.Roads.IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId && r.MapId == mapId)
                .ExecuteDeleteAsync(ct);

            result.Pings = await _db.Pings.IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId && p.MapId == mapId)
                .ExecuteDeleteAsync(ct);

            result.Overlays = await _db.OverlayData.IgnoreQueryFilters()
                .Where(o => o.TenantId == tenantId && o.MapId == mapId)
                .ExecuteDeleteAsync(ct);
            result.Overlays += await _db.OverlayOffsets.IgnoreQueryFilters()
                .Where(o => o.TenantId == tenantId && (o.CurrentMapId == mapId || o.OverlayMapId == mapId))
                .ExecuteDeleteAsync(ct);

            result.Tiles = await _db.Tiles.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && t.MapId == mapId)
                .ExecuteDeleteAsync(ct);

            result.Grids = await _db.Grids.IgnoreQueryFilters()
                .Where(g => g.TenantId == tenantId && g.Map == mapId)
                .ExecuteDeleteAsync(ct);

            await _db.Maps.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.Id == mapId)
                .ExecuteDeleteAsync(ct);

            await transaction.CommitAsync(ct);
        }

        // ExecuteDelete bypasses the change tracker, which may still hold now-deleted entities.
        _db.ChangeTracker.Clear();

        // The map's own tile files plus its grids' pool PNGs (a grid id is unique per tenant, so
        // only tile rows can still point at those). A grid id that is not a plain file name names
        // no pool file of its own (TenantFilePathService.IsSafeGridId).
        var candidates = tileFiles
            .Concat(gridIds.Select(id => TenantFilePathService.PoolRelativePath(tenantId, id)).OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        await DeleteUnreferencedFilesAsync(tenantId, gridStorage, candidates, result, ct);

        // Per-map directory: legacy zoom 1-6 pyramid plus any imported zoom-0 PNGs. Kept when a
        // live row of another map still references a file inside it (the rows the guard sees are
        // all live: this map's are gone).
        var perMapDir = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString());
        if (Directory.Exists(perMapDir))
        {
            if (await _tileService.AnyLiveTileReferencesDirectoryAsync(
                    tenantId, Path.Combine("tenants", tenantId, mapId.ToString()), excludeMapId: mapId))
            {
                result.Warnings.Add($"Kept directory of map {mapId}: tile rows of other maps still reference files inside it");
            }
            else
            {
                TryDeleteDirectory(perMapDir, result);
            }
        }

        // WebP pyramid (also evicts this process's in-memory tile cache for the map).
        var (webpFiles, webpBytes) = _largeTileService.DeleteMapWebpTiles(tenantId, mapId);
        result.WebpFilesDeleted = webpFiles;
        result.BytesFreed += webpBytes;

        // Rewrites CurrentStorageMB from what is actually left on disk.
        await _quotaService.RecalculateStorageUsageAsync(tenantId, gridStorage);

        _logger.LogInformation(
            "Deleted map {MapId} '{MapName}' (tenant {TenantId}): {Grids} grids, {Tiles} tiles, {Markers} markers, " +
            "{CustomMarkers} custom markers, {Roads} roads, {Overlays} overlays, {Files} files + {WebpFiles} webp files " +
            "({MB:F2} MB), {Kept} shared files kept",
            mapId, map.Name, tenantId, result.Grids, result.Tiles, result.Markers, result.CustomMarkers, result.Roads,
            result.Overlays, result.FilesDeleted, result.WebpFilesDeleted, result.MegabytesFreed, result.FilesKeptShared);

        return result;
    }

    /// <summary>
    /// Deletes the candidate files no LIVE-map tile row references. Candidates and references
    /// are compared in canonical form ("/" separators, "." and ".." resolved): a stored path like
    /// "tenants/t/grids/../5/0/1_1.png" names the same file as "tenants/t/5/0/1_1.png", and a
    /// raw string compare let it miss the live row naming that file. Rows are matched on the
    /// stored and canonical forms, each with both separators. Every path is containment-checked
    /// against the tenant's own directory, so a poisoned File value can never escape it.
    /// </summary>
    private async Task DeleteUnreferencedFilesAsync(
        string tenantId, string gridStorage, List<string> storedCandidates, MapDeletionResultDto result, CancellationToken ct)
    {
        var liveMapIds = _db.Maps.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.Id);

        var storageRoot = Path.GetFullPath(gridStorage);
        var candidates = storedCandidates
            .Select(f => Canonical(storageRoot, gridStorage, f))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var stillReferenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in Chunk(storedCandidates.Concat(candidates).Distinct(StringComparer.Ordinal).ToList()))
        {
            var forms = chunk
                .Select(NormalizePath)
                .SelectMany(f => new[] { f, f.Replace('/', '\\') })
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var referenced = await _db.Tiles.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && liveMapIds.Contains(t.MapId) && forms.Contains(t.File))
                .Select(t => t.File)
                .ToListAsync(ct);
            stillReferenced.UnionWith(referenced.Select(f => Canonical(storageRoot, gridStorage, f)));
        }

        var tenantRoot = Path.GetFullPath(Path.Combine(gridStorage, "tenants", tenantId));
        var outsideTenant = 0;

        foreach (var file in candidates)
        {
            if (stillReferenced.Contains(file))
            {
                result.FilesKeptShared++;
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(gridStorage, file));
            }
            catch (Exception)
            {
                outsideTenant++;
                continue;
            }

            if (!fullPath.StartsWith(tenantRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                outsideTenant++;
                continue;
            }

            try
            {
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var size = new FileInfo(fullPath).Length;
                File.Delete(fullPath);
                result.FilesDeleted++;
                result.BytesFreed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to delete tile file {File} for tenant {TenantId}", file, tenantId);
                result.Warnings.Add($"Could not delete '{file}': {ex.Message}");
            }
        }

        if (outsideTenant > 0)
        {
            result.Warnings.Add($"{outsideTenant} tile file path(s) outside the tenant directory were left alone");
        }
    }

    private void TryDeleteDirectory(string dir, MapDeletionResultDto result)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { result.BytesFreed += new FileInfo(file).Length; } catch { }
            }

            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete map directory {Dir}", dir);
            result.Warnings.Add($"Could not delete '{dir}': {ex.Message}");
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    /// <summary>
    /// A stored File (relative to GridStorage) in canonical form: "/" separators, "." and ".."
    /// resolved. Input that does not resolve stays as stored — the containment check rejects it.
    /// </summary>
    private static string Canonical(string storageRoot, string gridStorage, string file)
    {
        try
        {
            return NormalizePath(Path.GetRelativePath(storageRoot, Path.GetFullPath(Path.Combine(gridStorage, NormalizePath(file)))));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return NormalizePath(file);
        }
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> items)
    {
        for (var i = 0; i < items.Count; i += ChunkSize)
        {
            yield return items.GetRange(i, Math.Min(ChunkSize, items.Count - i));
        }
    }
}
