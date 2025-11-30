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
    /// Marks all parent zoom tiles (1-6) as dirty when a base tile is uploaded.
    /// Uses INSERT OR IGNORE to avoid duplicates efficiently.
    /// </summary>
    private async Task MarkParentTilesDirtyAsync(int mapId, Coord baseCoord, string tenantId)
    {
        var now = DateTime.UtcNow;
        var currentCoord = baseCoord;

        for (int zoom = 1; zoom <= 6; zoom++)
        {
            currentCoord = new Coord(currentCoord.X / 2, currentCoord.Y / 2);

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
    }

    public async Task<TileData?> GetTileAsync(int mapId, Coord coord, int zoom)
    {
        return await _tileRepository.GetTileAsync(mapId, coord, zoom);
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

        // Save the combined tile to tenant-specific directory
        var outputDir = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString(), zoom.ToString());
        Directory.CreateDirectory(outputDir);

        var outputFile = Path.Combine(outputDir, $"{coord.Name()}.png");
        await img.SaveAsPngAsync(outputFile, FastPngEncoder);

        // Calculate file size
        var fileInfo = new FileInfo(outputFile);
        var fileSizeBytes = (int)fileInfo.Length;

        // Update tenant storage quota
        var fileSizeMB = fileSizeBytes / 1024.0 / 1024.0;
        await _quotaService.IncrementStorageUsageAsync(tenantId, fileSizeMB);

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

                // Check if zoom tile exists (for quota adjustment)
                var existingTile = await _dbContext.Tiles
                    .IgnoreQueryFilters()
                    .Where(t => t.TenantId == tenantId
                        && t.MapId == dirty.MapId
                        && t.Zoom == dirty.Zoom
                        && t.CoordX == dirty.CoordX
                        && t.CoordY == dirty.CoordY)
                    .FirstOrDefaultAsync();

                long oldFileSizeBytes = 0;
                if (existingTile != null && !string.IsNullOrEmpty(existingTile.File))
                {
                    var oldFilePath = Path.Combine(gridStorage, existingTile.File);
                    if (File.Exists(oldFilePath))
                    {
                        oldFileSizeBytes = new FileInfo(oldFilePath).Length;
                    }
                }

                _logger.LogDebug("Rebuilding dirty zoom tile: Map={MapId}, Zoom={Zoom}, Coord={Coord}, SubTiles={SubTileCount}",
                    dirty.MapId, dirty.Zoom, coord, subTiles.Count);

                await UpdateZoomLevelAsync(dirty.MapId, coord, dirty.Zoom, tenantId, gridStorage, subTiles);

                // Adjust quota: UpdateZoomLevelAsync already increments for the new file,
                // so we need to decrement the old file size
                if (oldFileSizeBytes > 0)
                {
                    var oldFileSizeMB = oldFileSizeBytes / 1024.0 / 1024.0;
                    await _quotaService.IncrementStorageUsageAsync(tenantId, -oldFileSizeMB);
                }

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
