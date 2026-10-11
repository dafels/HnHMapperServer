using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Infrastructure.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Services.Services;

public class TileService : ITileService
{
    /// <summary>
    /// Fast PNG encoder for zoom tile generation.
    /// Uses fastest compression (level 1) for ~4x faster encoding.
    /// Trade-off: ~10-15% larger files, but encoding time drops from ~40ms to ~10ms per tile.
    /// </summary>
    public static readonly PngEncoder FastPngEncoder = new()
    {
        CompressionLevel = PngCompressionLevel.BestSpeed,
        FilterMethod = PngFilterMethod.None,
        BitDepth = PngBitDepth.Bit8,
        ColorType = PngColorType.RgbWithAlpha
    };

    private readonly ITileRepository _tileRepository;
    private readonly IGridRepository _gridRepository;
    private readonly IUpdateNotificationService _updateNotificationService;
    private readonly IStorageQuotaService _quotaService;
    private readonly ILogger<TileService> _logger;
    private readonly ApplicationDbContext _dbContext;

    public TileService(
        ITileRepository tileRepository,
        IGridRepository gridRepository,
        IUpdateNotificationService updateNotificationService,
        IStorageQuotaService quotaService,
        ILogger<TileService> logger,
        ApplicationDbContext dbContext)
    {
        _tileRepository = tileRepository;
        _gridRepository = gridRepository;
        _updateNotificationService = updateNotificationService;
        _quotaService = quotaService;
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task SaveTileAsync(int mapId, Coord coord, int zoom, string file, long timestamp, string tenantId, int fileSizeBytes)
    {
        var tileData = new TileData
        {
            MapId = mapId,
            Coord = coord,
            Zoom = zoom,
            File = file,
            Cache = timestamp,
            TenantId = tenantId,
            FileSizeBytes = fileSizeBytes
        };

        await _tileRepository.SaveTileAsync(tileData);
        _updateNotificationService.NotifyTileUpdate(tileData);

        // If this is a base tile (zoom 0), mark all parent zoom levels as dirty
        // This enables the optimized rebuild that only processes changed tiles
        if (zoom == 0)
        {
            await MarkParentTilesDirtyAsync(mapId, coord, tenantId);
        }
    }

    /// <summary>
    /// Marks all parent zoom tiles (1-6) as dirty when a base tile is uploaded, plus the WebP
    /// cell covering it (see <see cref="MarkWebpCellsDirtyAsync"/>).
    /// Uses INSERT OR IGNORE to avoid duplicates efficiently.
    /// </summary>
    private async Task MarkParentTilesDirtyAsync(int mapId, Coord baseCoord, string tenantId)
    {
        var now = DateTime.UtcNow;
        var currentCoord = baseCoord;

        for (int zoom = 1; zoom <= 6; zoom++)
        {
            // Coord.Parent() floors for negative coordinates; plain /2 truncates toward zero and
            // marked the wrong parent for every negative coord (e.g. -1/2 = 0 instead of -1).
            currentCoord = currentCoord.Parent();

            // Check if already exists (unique index will prevent duplicates anyway)
            var exists = await _dbContext.DirtyZoomTiles
                .IgnoreQueryFilters()
                .AnyAsync(d => d.TenantId == tenantId
                    && d.MapId == mapId
                    && d.CoordX == currentCoord.X
                    && d.CoordY == currentCoord.Y
                    && d.Zoom == zoom);

            if (!exists)
            {
                try
                {
                    _dbContext.DirtyZoomTiles.Add(new DirtyZoomTileEntity
                    {
                        TenantId = tenantId,
                        MapId = mapId,
                        CoordX = currentCoord.X,
                        CoordY = currentCoord.Y,
                        Zoom = zoom,
                        CreatedAt = now
                    });
                    await _dbContext.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Unique constraint violation - tile already marked dirty by another request
                    // This is expected in high-concurrency scenarios, safe to ignore
                    _dbContext.ChangeTracker.Clear();
                }
            }
        }

        await MarkWebpCellsDirtyAsync(tenantId, mapId, new[] { baseCoord });
    }

    /// <summary>
    /// The WebP pyramid's own durable queue: one DirtyWebpCells row per 4x4 zoom-0 cell, whose
    /// MarkedAt is bumped on every re-mark, consumed only by the WebP backstop once the cell's
    /// chain on disk is newer than the mark. It deliberately does not share DirtyZoomTiles, which
    /// the legacy PNG rebuild deletes on its own schedule (a backstop reading it saw 0-100% of the
    /// marks depending on timer phase), and whose CreatedAt never moves forward (data written after
    /// a regeneration looked fresh). Callers mark after their tile rows are saved, so a mark is
    /// never older than the data it covers. Own SaveChanges, separate from DirtyZoomTiles, so a
    /// failure on one never drops the other.
    /// </summary>
    private async Task MarkWebpCellsDirtyAsync(string tenantId, int mapId, IEnumerable<Coord> baseCoords)
    {
        // Sorted so each chunk's bounding-box read stays compact.
        var cells = baseCoords
            .Select(c => WebpDirtyCellPlanner.CellOf(c.X, c.Y))
            .Distinct()
            .OrderBy(c => c.X)
            .ThenBy(c => c.Y)
            .ToList();

        var now = DateTime.UtcNow;
        const int chunkSize = 500;
        for (int i = 0; i < cells.Count; i += chunkSize)
        {
            var chunk = cells.GetRange(i, Math.Min(chunkSize, cells.Count - i));

            // Two attempts: a concurrent writer may insert the same cell first (unique index), or
            // the backstop may delete a row between our read and our update (0 rows affected); a
            // fresh read resolves both. A second failure (e.g. the map was deleted: FK) is dropped.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await UpsertWebpCellsAsync(tenantId, mapId, chunk, now);
                    break;
                }
                catch (DbUpdateException ex)
                {
                    _dbContext.ChangeTracker.Clear();
                    if (attempt == 1)
                    {
                        _logger.LogDebug(ex, "Could not mark {Count} WebP cells dirty for tenant {TenantId} map {MapId}",
                            chunk.Count, tenantId, mapId);
                    }
                }
            }
        }
    }

    private async Task UpsertWebpCellsAsync(string tenantId, int mapId, List<(int X, int Y)> cells, DateTime now)
    {
        int minX = cells.Min(c => c.X), maxX = cells.Max(c => c.X);
        int minY = cells.Min(c => c.Y), maxY = cells.Max(c => c.Y);

        var existing = (await _dbContext.DirtyWebpCells
                .Where(d => d.TenantId == tenantId && d.MapId == mapId
                    && d.CellX >= minX && d.CellX <= maxX
                    && d.CellY >= minY && d.CellY <= maxY)
                .ToListAsync())
            .ToDictionary(d => (d.CellX, d.CellY));

        foreach (var cell in cells)
        {
            if (existing.TryGetValue(cell, out var row))
            {
                if (row.MarkedAt < now)
                {
                    row.MarkedAt = now;
                }
            }
            else
            {
                _dbContext.DirtyWebpCells.Add(new DirtyWebpCellEntity
                {
                    TenantId = tenantId,
                    MapId = mapId,
                    CellX = cell.X,
                    CellY = cell.Y,
                    MarkedAt = now
                });
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task<TileData?> GetTileAsync(int mapId, Coord coord, int zoom)
    {
        return await _tileRepository.GetTileAsync(mapId, coord, zoom);
    }

    public async Task DeleteTilesByMapAsync(int mapId)
    {
        await _tileRepository.DeleteTilesByMapAsync(mapId);
    }

    /// <summary>
    /// Explicit tenantId + IgnoreQueryFilters like the integrity service's reference set
    /// (MapIntegrityService.LoadReferencedFilesAsync), so a merge, the scan and the orphan purge
    /// agree on which per-map directories are still in use. Stored paths carry the separator of
    /// the OS that wrote them, so both forms are matched.
    /// </summary>
    public async Task<bool> AnyLiveTileReferencesDirectoryAsync(string tenantId, string relativeDirectory, int excludeMapId)
    {
        var slashPrefix = relativeDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        var backslashPrefix = slashPrefix.Replace('/', '\\');

        var liveMapIds = _dbContext.Maps
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.Id != excludeMapId)
            .Select(m => m.Id);

        return await _dbContext.Tiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(t => t.TenantId == tenantId
                && t.File != ""
                && liveMapIds.Contains(t.MapId)
                && (t.File.StartsWith(slashPrefix) || t.File.StartsWith(backslashPrefix)));
    }

    /// <summary>
    /// Marks the zoom 1-6 parents of many base coords dirty in one pass. Unlike the per-tile
    /// path this dedupes parents in memory and diffs against existing rows first, so bulk
    /// writers (imports, merges, rebuilds) don't pay 6 exists-queries per tile. Also marks the
    /// WebP cells covering them (bumping existing marks), which is what makes a multi-flush import
    /// re-dirty a cell the backstop regenerated between two flushes.
    /// Explicit tenantId + IgnoreQueryFilters: bulk callers run outside the row's tenant context
    /// (superadmin requests, background services).
    /// </summary>
    public async Task MarkParentTilesDirtyBatchAsync(string tenantId, int mapId, IReadOnlyCollection<Coord> baseCoords)
    {
        if (baseCoords.Count == 0)
            return;

        var wanted = new HashSet<(int Zoom, int X, int Y)>();
        foreach (var baseCoord in baseCoords)
        {
            var current = baseCoord;
            for (int zoom = 1; zoom <= 6; zoom++)
            {
                current = current.Parent();
                wanted.Add((zoom, current.X, current.Y));
            }
        }

        var existing = await _dbContext.DirtyZoomTiles
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.MapId == mapId)
            .Select(d => new { d.Zoom, d.CoordX, d.CoordY })
            .ToListAsync();
        var existingSet = existing.Select(e => (e.Zoom, e.CoordX, e.CoordY)).ToHashSet();

        var now = DateTime.UtcNow;
        var toAdd = wanted
            .Where(w => !existingSet.Contains(w))
            .Select(w => new DirtyZoomTileEntity
            {
                TenantId = tenantId,
                MapId = mapId,
                CoordX = w.X,
                CoordY = w.Y,
                Zoom = w.Zoom,
                CreatedAt = now
            })
            .ToList();

        const int batchSize = 500;
        for (int i = 0; i < toAdd.Count; i += batchSize)
        {
            _dbContext.DirtyZoomTiles.AddRange(toAdd.Skip(i).Take(batchSize));
            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Unique-index race with a concurrent marker — rows are already there.
                _dbContext.ChangeTracker.Clear();
            }
        }

        if (toAdd.Count > 0)
        {
            _logger.LogDebug("Marked {Count} parent zoom tiles dirty for tenant {TenantId} map {MapId} ({BaseCount} base coords)",
                toAdd.Count, tenantId, mapId, baseCoords.Count);
        }

        await MarkWebpCellsDirtyAsync(tenantId, mapId, baseCoords);
    }

    /// <summary>
    /// Tenant-scoped (the caller's ambient tenant, like <see cref="GetTileAsync"/>).
    /// </summary>
    public async Task<bool> HasRenderableTileAsync(int mapId, Coord coord, string gridStorage)
    {
        var tile = await GetTileAsync(mapId, coord, 0);
        return tile != null
            && !string.IsNullOrEmpty(tile.File)
            && File.Exists(Path.Combine(gridStorage, tile.File));
    }

    /// <summary>
    /// Writes a zoom-0 grid PNG or a legacy zoom PNG in place of whatever is at
    /// <paramref name="fullPath"/> and charges the quota with the size CHANGE: grid refreshes
    /// (every 30 min per walked grid) and zoom rebuilds overwrite the same file, and charging
    /// the full size each time is what drifted CurrentStorageMB above disk. The write is
    /// temp file + rename (AtomicFile), so a failure leaves the previous file intact — the old
    /// GridUpload deleted the only copy of a tile after a failed write.
    /// </summary>
    /// <returns>Bytes charged (new size minus the replaced file's size).</returns>
    public async Task<long> ReplaceTileFileAsync(string tenantId, string fullPath, byte[] bytes)
    {
        var previous = AtomicFile.LengthOrZero(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await AtomicFile.WriteAllBytesAsync(fullPath, bytes);

        var delta = bytes.LongLength - previous;
        if (delta != 0)
        {
            await _quotaService.IncrementStorageUsageAsync(tenantId, delta / 1024.0 / 1024.0);
        }

        return delta;
    }

    /// <summary>
    /// Best-effort removal of a retired (merged-away) map's tile directories, charging the quota
    /// with the net change on disk in one update. The WebP pyramid (tenants/{t}/large/{mapId}/)
    /// always goes and is never credited: the pyramid is reconciled-only, and the merge
    /// regenerates the target's cells uncharged. The per-map directory (tenants/{t}/{mapId}/:
    /// legacy zoom 1-6 pyramid plus any imported zoom-0 PNGs) goes only when no LIVE-map row still
    /// references a file inside it — the integrity purge's rule, so merge, scan and purge agree —
    /// and its bytes are credited once the delete succeeds (a failed delete credits nothing; the
    /// reconciliation corrects a partial one). Grid PNGs in the shared grids/ dir are untouched.
    /// </summary>
    /// <param name="rehomedBytes">Bytes the caller copied out of the per-map directory into the
    /// grid pool just before (the merge re-home): charged in the same update.</param>
    /// <returns>Bytes charged (rehomed minus freed).</returns>
    public async Task<long> DeleteRetiredMapDirectoriesAsync(string tenantId, int mapId, string gridStorage, long rehomedBytes)
    {
        TryDeleteDirectory(Path.Combine(gridStorage, "tenants", tenantId, "large", mapId.ToString()), out _);

        long freed = 0;
        var perMapDir = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString());
        if (Directory.Exists(perMapDir))
        {
            // The retired map's row may still exist (merge deletes it after this call), so it
            // is excluded explicitly; its own rows were already dropped.
            if (await AnyLiveTileReferencesDirectoryAsync(
                    tenantId, Path.Combine("tenants", tenantId, mapId.ToString()), excludeMapId: mapId))
            {
                _logger.LogWarning(
                    "Kept directory of merged map {SourceMapId} (tenant {TenantId}): live tile rows still reference files inside it",
                    mapId, tenantId);
            }
            else if (TryDeleteDirectory(perMapDir, out var bytes))
            {
                freed = bytes;
            }
        }

        var delta = rehomedBytes - freed;
        if (delta != 0)
        {
            await _quotaService.IncrementStorageUsageAsync(tenantId, delta / 1024.0 / 1024.0);
        }

        return delta;
    }

    /// <summary>Deletes a directory tree; <paramref name="bytes"/> = what it held when it was deleted.</summary>
    private bool TryDeleteDirectory(string dir, out long bytes)
    {
        bytes = 0;
        try
        {
            if (!Directory.Exists(dir))
                return false;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                total += AtomicFile.LengthOrZero(file);
            }

            Directory.Delete(dir, recursive: true);
            bytes = total;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete retired map directory {Dir}", dir);
            return false;
        }
    }

    public async Task UpdateZoomLevelAsync(int mapId, Coord coord, int zoom, string tenantId, string gridStorage, List<TileData>? preloadedTiles = null)
    {
        using var img = new Image<Rgba32>(100, 100);
        img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        // OPTIMIZED: Load all 4 sub-tiles in parallel instead of sequentially
        // This reduces I/O wait time from 4x to 1x (parallel file reads)
        var loadTasks = new List<Task<(int x, int y, Image<Rgba32>? image)>>();

        for (int x = 0; x <= 1; x++)
        {
            for (int y = 0; y <= 1; y++)
            {
                var subCoord = new Coord(coord.X * 2 + x, coord.Y * 2 + y);

                // Use preloaded tiles if available (for background services without HTTP context)
                // Otherwise fall back to repository query (for normal HTTP requests)
                TileData? td;
                if (preloadedTiles != null)
                {
                    td = preloadedTiles.FirstOrDefault(t =>
                        t.MapId == mapId &&
                        t.Zoom == zoom - 1 &&
                        t.Coord.X == subCoord.X &&
                        t.Coord.Y == subCoord.Y);
                }
                else
                {
                    td = await GetTileAsync(mapId, subCoord, zoom - 1);
                }

                if (td == null || string.IsNullOrEmpty(td.File))
                    continue;

                var filePath = Path.Combine(gridStorage, td.File);
                if (!File.Exists(filePath))
                    continue;

                // Capture loop variables for the async lambda
                var capturedX = x;
                var capturedY = y;
                var capturedPath = filePath;

                // Start async image loading task
                loadTasks.Add(LoadSubTileAsync(capturedX, capturedY, capturedPath));
            }
        }

        // Wait for all image loads to complete in parallel
        var loadedImages = await Task.WhenAll(loadTasks);

        int loadedSubTiles = 0;

        // Composite all loaded images onto the canvas (must be sequential for thread safety)
        foreach (var (x, y, subImage) in loadedImages)
        {
            if (subImage == null)
                continue;

            try
            {
                // Resize to 50x50 and place in appropriate quadrant
                using var resized = subImage.Clone(ctx => ctx.Resize(50, 50));
                img.Mutate(ctx => ctx.DrawImage(resized, new Point(50 * x, 50 * y), 1f));
                loadedSubTiles++;
            }
            finally
            {
                subImage.Dispose();
            }
        }

        if (loadedSubTiles == 0)
        {
            _logger.LogWarning("Zoom tile Map={MapId} Zoom={Zoom} Coord={Coord} has NO sub-tiles loaded - creating empty transparent tile", mapId, zoom, coord);
        }
        else if (loadedSubTiles < 4)
        {
            _logger.LogDebug("Zoom tile Map={MapId} Zoom={Zoom} Coord={Coord} has only {Count}/4 sub-tiles loaded", mapId, zoom, coord, loadedSubTiles);
        }

        // Save the combined tile to tenant-specific directory. Rebuilds overwrite the same file,
        // so only the size change is charged (ReplaceTileFileAsync).
        var outputDir = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString(), zoom.ToString());
        var outputFile = Path.Combine(outputDir, $"{coord.Name()}.png");

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await img.SaveAsPngAsync(ms, FastPngEncoder);
            bytes = ms.ToArray();
        }
        await ReplaceTileFileAsync(tenantId, outputFile, bytes);
        var fileSizeBytes = bytes.Length;

        var relativePath = Path.Combine("tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{coord.Name()}.png");
        await SaveTileAsync(mapId, coord, zoom, relativePath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), tenantId, fileSizeBytes);
    }

    /// <summary>
    /// Loads a sub-tile image asynchronously. Returns null if loading fails.
    /// </summary>
    private async Task<(int x, int y, Image<Rgba32>? image)> LoadSubTileAsync(int x, int y, string filePath)
    {
        try
        {
            var image = await Image.LoadAsync<Rgba32>(filePath);
            return (x, y, image);
        }
        catch (Exception)
        {
            // Failed to load - return null (will be skipped during compositing)
            return (x, y, null);
        }
    }

    public async Task RebuildZoomsAsync(string gridStorage)
    {
        _logger.LogInformation("Rebuild Zooms starting...");
        _logger.LogWarning("RebuildZoomsAsync: This method has NOT been fully updated for multi-tenancy. " +
                          "It assumes files are in old 'grids/' directory and may not work correctly after migration.");

        var allGrids = await _gridRepository.GetAllGridsAsync();
        var needProcess = new Dictionary<(Coord, int), bool>();
        var saveGrid = new Dictionary<(Coord, int), (string gridId, string tenantId)>();

        foreach (var grid in allGrids)
        {
            needProcess[(grid.Coord.Parent(), grid.Map)] = true;
            saveGrid[(grid.Coord, grid.Map)] = (grid.Id, grid.TenantId);
        }

        _logger.LogInformation("Rebuild Zooms: Saving base tiles...");
        foreach (var ((coord, mapId), (gridId, tenantId)) in saveGrid)
        {
            // NOTE: Still using old path format - needs migration update
            var filePath = Path.Combine(gridStorage, "grids", $"{gridId}.png");
            if (!File.Exists(filePath))
                continue;

            var fileInfo = new FileInfo(filePath);
            var fileSizeBytes = (int)fileInfo.Length;

            var relativePath = Path.Combine("grids", $"{gridId}.png");
            await SaveTileAsync(mapId, coord, 0, relativePath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), tenantId, fileSizeBytes);
        }

        for (int z = 1; z <= 6; z++)
        {
            _logger.LogInformation("Rebuild Zooms: Level {Zoom}", z);
            var process = needProcess.Keys.ToList();
            needProcess.Clear();

            foreach (var (coord, mapId) in process)
            {
                // Get tenantId from grid
                var grid = allGrids.FirstOrDefault(g => g.Coord == coord && g.Map == mapId);
                if (grid == null)
                {
                    throw new InvalidOperationException($"Grid at {coord} on map {mapId} not found during zoom rebuild");
                }

                await UpdateZoomLevelAsync(mapId, coord, z, grid.TenantId, gridStorage);
                needProcess[(coord.Parent(), mapId)] = true;
            }
        }

        _logger.LogInformation("Rebuild Zooms: Complete!");
    }

    /// <summary>
    /// OPTIMIZED: Rebuilds zoom tiles using dirty tile tracking.
    /// Instead of scanning all tiles to find stale ones, queries only the dirty tile table.
    /// Expected performance: O(dirty tiles) instead of O(all tiles).
    /// </summary>
    public async Task<int> RebuildIncompleteZoomTilesAsync(string tenantId, string gridStorage, int maxTilesToRebuild)
    {
        int rebuiltCount = 0;

        try
        {
            // FAST: Query only dirty tiles (should be small number)
            var dirtyTiles = await _dbContext.DirtyZoomTiles
                .IgnoreQueryFilters()
                .Where(d => d.TenantId == tenantId)
                .OrderBy(d => d.Zoom)  // Process lower zoom levels first
                .ThenBy(d => d.MapId)
                .ThenBy(d => d.CoordX)
                .ThenBy(d => d.CoordY)
                .Take(maxTilesToRebuild)
                .ToListAsync();

            if (dirtyTiles.Count == 0)
            {
                _logger.LogDebug("Tenant {TenantId}: No dirty zoom tiles", tenantId);
                return 0;
            }

            _logger.LogDebug("Tenant {TenantId}: Processing {Count} dirty zoom tiles", tenantId, dirtyTiles.Count);

            foreach (var dirty in dirtyTiles)
            {
                var coord = new Coord(dirty.CoordX, dirty.CoordY);

                // Load only the 4 sub-tiles needed for this specific parent
                var subTiles = await LoadSubTilesForParentAsync(tenantId, dirty.MapId, coord, dirty.Zoom - 1);

                if (subTiles.Count == 0)
                {
                    _logger.LogDebug("Skipping dirty tile Map={MapId} Zoom={Zoom} Coord={Coord}: no sub-tiles found",
                        dirty.MapId, dirty.Zoom, coord);
                    // Still remove from dirty list since there's nothing to rebuild
                    _dbContext.DirtyZoomTiles.Remove(dirty);
                    continue;
                }

                _logger.LogDebug("Rebuilding dirty zoom tile: Map={MapId}, Zoom={Zoom}, Coord={Coord}, SubTiles={SubTileCount}",
                    dirty.MapId, dirty.Zoom, coord, subTiles.Count);

                // Charges only the size change of the file it replaces (whether or not a Tiles
                // row pointed at it — the old compensation here missed orphan files).
                await UpdateZoomLevelAsync(dirty.MapId, coord, dirty.Zoom, tenantId, gridStorage, subTiles);

                // Remove from dirty list
                _dbContext.DirtyZoomTiles.Remove(dirty);
                rebuiltCount++;
            }

            // Save all dirty tile removals in one batch
            await _dbContext.SaveChangesAsync();

            if (rebuiltCount > 0)
            {
                _logger.LogInformation("Rebuilt {Count} dirty zoom tiles for tenant {TenantId}", rebuiltCount, tenantId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rebuilding dirty zoom tiles for tenant {TenantId}", tenantId);
        }

        return rebuiltCount;
    }

    /// <summary>
    /// Checks if tenant has any dirty tiles pending rebuild.
    /// Used by ZoomTileRebuildService for fast skip check.
    /// </summary>
    public async Task<bool> HasDirtyZoomTilesAsync(string tenantId)
    {
        return await _dbContext.DirtyZoomTiles
            .IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == tenantId);
    }

    /// <summary>
    /// Gets the count of dirty tiles for a tenant.
    /// Used for monitoring and logging.
    /// </summary>
    public async Task<int> GetDirtyZoomTileCountAsync(string tenantId)
    {
        return await _dbContext.DirtyZoomTiles
            .IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == tenantId);
    }

    /// <summary>
    /// Gets the distinct map IDs that have dirty zoom tiles for a tenant.
    /// Used by LargeTileGenerationService to only scan maps with recent uploads.
    /// </summary>
    public async Task<HashSet<int>> GetDirtyMapIdsAsync(string tenantId)
    {
        var mapIds = await _dbContext.DirtyZoomTiles
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId)
            .Select(d => d.MapId)
            .Distinct()
            .ToListAsync();
        return mapIds.ToHashSet();
    }

    /// <summary>
    /// Loads only the 4 specific sub-tiles needed for a parent coordinate.
    /// Much more efficient than loading all tiles for a tenant.
    /// </summary>
    private async Task<List<TileData>> LoadSubTilesForParentAsync(string tenantId, int mapId, Coord parentCoord, int subZoom)
    {
        // Calculate the coordinate range for the 4 sub-tiles
        var minX = parentCoord.X * 2;
        var maxX = parentCoord.X * 2 + 1;
        var minY = parentCoord.Y * 2;
        var maxY = parentCoord.Y * 2 + 1;

        var subTiles = await _dbContext.Tiles
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId
                && t.MapId == mapId
                && t.Zoom == subZoom
                && t.CoordX >= minX && t.CoordX <= maxX
                && t.CoordY >= minY && t.CoordY <= maxY)
            .ToListAsync();

        return subTiles.Select(t => new TileData
        {
            MapId = t.MapId,
            Coord = new Coord(t.CoordX, t.CoordY),
            Zoom = t.Zoom,
            File = t.File,
            Cache = t.Cache,
            TenantId = t.TenantId,
            FileSizeBytes = t.FileSizeBytes
        }).ToList();
    }
}
