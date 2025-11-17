using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Infrastructure.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Services.Services;

public class TileService : ITileService
{
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
    }

    public async Task<TileData?> GetTileAsync(int mapId, Coord coord, int zoom)
    {
        return await _tileRepository.GetTileAsync(mapId, coord, zoom);
    }

    public async Task UpdateZoomLevelAsync(int mapId, Coord coord, int zoom, string tenantId, string gridStorage, List<TileData>? preloadedTiles = null)
    {
        using var img = new Image<Rgba32>(100, 100);
        img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        int loadedSubTiles = 0;

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

                try
                {
                    using var subImg = await Image.LoadAsync<Rgba32>(filePath);

                    // Resize to 50x50 and place in appropriate quadrant
                    using var resized = subImg.Clone(ctx => ctx.Resize(50, 50));
                    img.Mutate(ctx => ctx.DrawImage(resized, new Point(50 * x, 50 * y), 1f));
                    loadedSubTiles++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load sub-tile {File}", filePath);
                }
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
        await img.SaveAsPngAsync(outputFile);

        // Calculate file size
        var fileInfo = new FileInfo(outputFile);
        var fileSizeBytes = (int)fileInfo.Length;

        // Update tenant storage quota
        var fileSizeMB = fileSizeBytes / 1024.0 / 1024.0;
        await _quotaService.IncrementStorageUsageAsync(tenantId, fileSizeMB);

        var relativePath = Path.Combine("tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{coord.Name()}.png");
        await SaveTileAsync(mapId, coord, zoom, relativePath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), tenantId, fileSizeBytes);
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

    public async Task<int> RebuildIncompleteZoomTilesAsync(string tenantId, string gridStorage, int maxTilesToRebuild)
    {
        int rebuiltCount = 0;

        try
        {
            // Get all tiles from the database for this tenant
            // Use IgnoreQueryFilters() to bypass tenant filter (background services have no HTTP context)
            // Then manually filter by the provided tenantId
            var tileEntities = await _dbContext.Tiles
                .IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId)
                .ToListAsync();

            // Convert to domain models
            var tenantTiles = tileEntities.Select(t => new TileData
            {
                MapId = t.MapId,
                Coord = new Coord(t.CoordX, t.CoordY),
                Zoom = t.Zoom,
                File = t.File,
                Cache = t.Cache,
                TenantId = t.TenantId,
                FileSizeBytes = t.FileSizeBytes
            }).ToList();

            _logger.LogInformation("Zoom rebuild scan for tenant {TenantId}: Found {TotalTiles} tiles", tenantId, tenantTiles.Count);

            // Process zoom levels 1-6 in order
            for (int zoom = 1; zoom <= 6 && rebuiltCount < maxTilesToRebuild; zoom++)
            {
                int skippedMissingSubTiles = 0;
                int skippedNoNewerSubTiles = 0;
                int zoomLevelRebuiltCount = 0;
                int zoomLevelCreatedCount = 0;

                // First, find all parent coordinates that should exist at this zoom level
                // based on tiles that exist at the previous zoom level
                var previousZoomTiles = tenantTiles.Where(t => t.Zoom == zoom - 1).ToList();
                var parentCoords = previousZoomTiles
                    .Select(t => t.Coord.Parent())
                    .Distinct()
                    .ToList();

                // Get tiles that already exist at this zoom level
                var existingZoomTiles = tenantTiles.Where(t => t.Zoom == zoom).ToList();
                var existingCoords = existingZoomTiles.Select(t => new { t.Coord.X, t.Coord.Y, t.MapId }).ToHashSet();

                _logger.LogInformation("Zoom rebuild: Level {Zoom} has {Existing} existing tiles, {Expected} parent coords from level {PrevZoom}",
                    zoom, existingZoomTiles.Count, parentCoords.Count, zoom - 1);

                // Create missing zoom tiles
                foreach (var parentCoord in parentCoords)
                {
                    if (rebuiltCount >= maxTilesToRebuild)
                        break;

                    // Get the map ID from any of the sub-tiles
                    var sampleSubTile = previousZoomTiles.FirstOrDefault(t => t.Coord.Parent().Equals(parentCoord));
                    if (sampleSubTile == null) continue;

                    var mapId = sampleSubTile.MapId;

                    // Check if this tile already exists
                    if (existingCoords.Contains(new { X = parentCoord.X, Y = parentCoord.Y, MapId = mapId }))
                        continue;

                    // Create the missing tile
                    _logger.LogInformation("Creating missing zoom tile: Map={MapId}, Zoom={Zoom}, Coord={Coord}, TenantId={TenantId}",
                        mapId, zoom, parentCoord, tenantId);

                    await UpdateZoomLevelAsync(mapId, parentCoord, zoom, tenantId, gridStorage, tenantTiles);

                    // Add to tenantTiles list so higher zoom levels can use it
                    var newTile = new TileData
                    {
                        MapId = mapId,
                        Coord = parentCoord,
                        Zoom = zoom,
                        File = $"tenants/{tenantId}/{mapId}/{zoom}/{parentCoord.Name()}.png",
                        Cache = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        TenantId = tenantId,
                        FileSizeBytes = 0 // Will be set by SaveTileAsync
                    };
                    tenantTiles.Add(newTile);

                    rebuiltCount++;
                    zoomLevelCreatedCount++;
                }

                if (existingZoomTiles.Count > 0)
                {
                    _logger.LogInformation("Zoom rebuild: Checking {Count} existing tiles at zoom level {Zoom}", existingZoomTiles.Count, zoom);
                }

                foreach (var zoomTile in existingZoomTiles)
                {
                    if (rebuiltCount >= maxTilesToRebuild)
                        break;

                    // Check how many sub-tiles exist at the previous zoom level
                    var subTilesExist = new List<TileData>();
                    int subTileCount = 0;

                    for (int x = 0; x <= 1; x++)
                    {
                        for (int y = 0; y <= 1; y++)
                        {
                            var subCoord = new Coord(zoomTile.Coord.X * 2 + x, zoomTile.Coord.Y * 2 + y);

                            // Search in already-loaded tenantTiles instead of calling repository
                            // (repository uses global query filter which requires HTTP context)
                            var subTile = tenantTiles.FirstOrDefault(t =>
                                t.MapId == zoomTile.MapId &&
                                t.Zoom == zoom - 1 &&
                                t.Coord.X == subCoord.X &&
                                t.Coord.Y == subCoord.Y);

                            if (subTile != null && !string.IsNullOrEmpty(subTile.File))
                            {
                                subTileCount++;
                                subTilesExist.Add(subTile);
                            }
                        }
                    }

                    bool shouldRebuild = false;
                    string rebuildReason = "";

                    // Rebuild if we have at least ONE sub-tile (allows partial coverage)
                    if (subTileCount > 0)
                    {
                        // Check if any sub-tile has a different timestamp than the zoom tile
                        bool hasNewerThanZoom = subTilesExist.Any(st => st.Cache > zoomTile.Cache);

                        // Check if sub-tiles have varying timestamps among themselves
                        var uniqueTimestamps = subTilesExist.Select(st => st.Cache).Distinct().Count();
                        bool hasVaryingSubTileTimestamps = uniqueTimestamps > 1;

                        // Check if zoom tile is older than 1 minute (likely needs rebuilding)
                        // This is aggressive but needed for one-time fix of broken tiles
                        var tileAgeMinutes = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - zoomTile.Cache) / 1000.0 / 60.0;
                        bool isOldTile = tileAgeMinutes > 1;

                        // Check if tile file is suspiciously small (likely empty/transparent)
                        // Real zoom tiles should be 2KB+, empty ones are <1KB
                        bool isEmptyTile = zoomTile.FileSizeBytes > 0 && zoomTile.FileSizeBytes < 1024;

                        if (hasNewerThanZoom)
                        {
                            shouldRebuild = true;
                            rebuildReason = $"has newer sub-tiles ({subTileCount}/4)";
                        }
                        else if (hasVaryingSubTileTimestamps)
                        {
                            shouldRebuild = true;
                            rebuildReason = $"sub-tiles have varying timestamps ({subTileCount}/4)";
                        }
                        else if (isEmptyTile && subTileCount >= 1)
                        {
                            // Rebuild empty tiles that have at least one sub-tile
                            shouldRebuild = true;
                            rebuildReason = $"empty tile fix ({subTileCount}/4, {zoomTile.FileSizeBytes}b)";
                        }
                        else if (isOldTile && subTileCount >= 1)
                        {
                            // Rebuild old tiles with at least one sub-tile (one-time fix for existing tiles)
                            shouldRebuild = true;
                            rebuildReason = $"old tile refresh ({subTileCount}/4)";
                        }
                    }

                    // Track why we're skipping tiles
                    if (subTileCount == 0)
                    {
                        skippedMissingSubTiles++;
                    }
                    else if (!shouldRebuild)
                    {
                        skippedNoNewerSubTiles++;
                    }

                    // Rebuild if needed
                    if (shouldRebuild)
                    {
                        _logger.LogInformation(
                            "Rebuilding zoom tile: Map={MapId}, Zoom={Zoom}, Coord={Coord}, TenantId={TenantId}, Reason={Reason}",
                            zoomTile.MapId, zoom, zoomTile.Coord, zoomTile.TenantId, rebuildReason);

                        // Get the old file size for quota adjustment
                        var oldFilePath = Path.Combine(gridStorage, zoomTile.File);
                        long oldFileSizeBytes = 0;
                        if (File.Exists(oldFilePath))
                        {
                            oldFileSizeBytes = new FileInfo(oldFilePath).Length;
                        }

                        // Regenerate the zoom tile, passing preloaded tiles to avoid query filter issues
                        await UpdateZoomLevelAsync(
                            zoomTile.MapId,
                            zoomTile.Coord,
                            zoom,
                            zoomTile.TenantId,
                            gridStorage,
                            tenantTiles);

                        // Adjust quota: UpdateZoomLevelAsync already increments for the new file,
                        // so we need to decrement the old file size
                        if (oldFileSizeBytes > 0)
                        {
                            var oldFileSizeMB = oldFileSizeBytes / 1024.0 / 1024.0;
                            await _quotaService.IncrementStorageUsageAsync(zoomTile.TenantId, -oldFileSizeMB);
                        }

                        // Update the zoom tile in the tenantTiles list with new timestamp
                        // This allows higher zoom levels to detect it as "changed" and rebuild
                        var existingTile = tenantTiles.FirstOrDefault(t =>
                            t.MapId == zoomTile.MapId &&
                            t.Zoom == zoom &&
                            t.Coord.X == zoomTile.Coord.X &&
                            t.Coord.Y == zoomTile.Coord.Y);

                        if (existingTile != null)
                        {
                            // Update timestamp to current time so higher zoom levels see it as "newer"
                            existingTile.Cache = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        }

                        rebuiltCount++;
                        zoomLevelRebuiltCount++;
                    }
                }

                // Log summary for this zoom level
                _logger.LogInformation(
                    "Zoom {Zoom} summary: {Existing} existing tiles, {Created} created, {Rebuilt} rebuilt, {SkippedMissing} skipped (missing sub-tiles), {SkippedNotNewer} skipped (no newer sub-tiles)",
                    zoom, existingZoomTiles.Count, zoomLevelCreatedCount, zoomLevelRebuiltCount, skippedMissingSubTiles, skippedNoNewerSubTiles);
            }

            if (rebuiltCount > 0)
            {
                _logger.LogInformation("Rebuilt {Count} incomplete zoom tiles", rebuiltCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rebuilding incomplete zoom tiles");
        }

        return rebuiltCount;
    }
}
