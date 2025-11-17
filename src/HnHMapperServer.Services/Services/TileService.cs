using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

public class TileService : ITileService
{
    private readonly ITileRepository _tileRepository;
    private readonly IGridRepository _gridRepository;
    private readonly IUpdateNotificationService _updateNotificationService;
    private readonly IStorageQuotaService _quotaService;
    private readonly ILogger<TileService> _logger;

    public TileService(
        ITileRepository tileRepository,
        IGridRepository gridRepository,
        IUpdateNotificationService updateNotificationService,
        IStorageQuotaService quotaService,
        ILogger<TileService> logger)
    {
        _tileRepository = tileRepository;
        _gridRepository = gridRepository;
        _updateNotificationService = updateNotificationService;
        _quotaService = quotaService;
        _logger = logger;
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

    public async Task UpdateZoomLevelAsync(int mapId, Coord coord, int zoom, string tenantId, string gridStorage)
    {
        using var img = new Image<Rgba32>(100, 100);
        img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        for (int x = 0; x <= 1; x++)
        {
            for (int y = 0; y <= 1; y++)
            {
                var subCoord = new Coord(coord.X * 2 + x, coord.Y * 2 + y);
                var td = await GetTileAsync(mapId, subCoord, zoom - 1);

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
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load sub-tile {File}", filePath);
                }
            }
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

    public async Task<int> RebuildIncompleteZoomTilesAsync(string gridStorage, int maxTilesToRebuild)
    {
        int rebuiltCount = 0;

        try
        {
            // Get all tiles from the database
            var allTiles = await _tileRepository.GetAllTilesAsync();

            _logger.LogInformation("Zoom rebuild scan: Found {TotalTiles} total tiles in database", allTiles.Count);

            // Process zoom levels 1-6 in order
            for (int zoom = 1; zoom <= 6 && rebuiltCount < maxTilesToRebuild; zoom++)
            {
                // Get all tiles at this zoom level
                var zoomTiles = allTiles.Where(t => t.Zoom == zoom).ToList();

                int skippedMissingSubTiles = 0;
                int skippedNoNewerSubTiles = 0;

                if (zoomTiles.Count > 0)
                {
                    _logger.LogInformation("Zoom rebuild: Checking {Count} tiles at zoom level {Zoom}", zoomTiles.Count, zoom);
                }

                foreach (var zoomTile in zoomTiles)
                {
                    if (rebuiltCount >= maxTilesToRebuild)
                        break;

                    // Check if all 4 sub-tiles exist at the previous zoom level
                    var subTilesExist = new List<TileData>();
                    bool allSubTilesExist = true;
                    int subTileCount = 0;

                    for (int x = 0; x <= 1; x++)
                    {
                        for (int y = 0; y <= 1; y++)
                        {
                            var subCoord = new Coord(zoomTile.Coord.X * 2 + x, zoomTile.Coord.Y * 2 + y);
                            var subTile = await GetTileAsync(zoomTile.MapId, subCoord, zoom - 1);

                            if (subTile == null || string.IsNullOrEmpty(subTile.File))
                            {
                                allSubTilesExist = false;
                                break;
                            }

                            subTileCount++;
                            subTilesExist.Add(subTile);
                        }

                        if (!allSubTilesExist)
                            break;
                    }

                    bool shouldRebuild = false;
                    string rebuildReason = "";

                    if (allSubTilesExist)
                    {
                        // Check if any sub-tile has a different timestamp than the zoom tile
                        bool hasNewerThanZoom = subTilesExist.Any(st => st.Cache > zoomTile.Cache);

                        // Check if sub-tiles have varying timestamps among themselves
                        var uniqueTimestamps = subTilesExist.Select(st => st.Cache).Distinct().Count();
                        bool hasVaryingSubTileTimestamps = uniqueTimestamps > 1;

                        if (hasNewerThanZoom)
                        {
                            shouldRebuild = true;
                            rebuildReason = "has newer sub-tiles";
                        }
                        else if (hasVaryingSubTileTimestamps)
                        {
                            shouldRebuild = true;
                            rebuildReason = "sub-tiles have varying timestamps";
                        }
                    }

                    // Track why we're skipping tiles
                    if (!allSubTilesExist)
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

                        // Regenerate the zoom tile
                        await UpdateZoomLevelAsync(
                            zoomTile.MapId,
                            zoomTile.Coord,
                            zoom,
                            zoomTile.TenantId,
                            gridStorage);

                        // Adjust quota: UpdateZoomLevelAsync already increments for the new file,
                        // so we need to decrement the old file size
                        if (oldFileSizeBytes > 0)
                        {
                            var oldFileSizeMB = oldFileSizeBytes / 1024.0 / 1024.0;
                            await _quotaService.IncrementStorageUsageAsync(zoomTile.TenantId, -oldFileSizeMB);
                        }

                        rebuiltCount++;
                    }
                }

                // Log summary for this zoom level
                if (zoomTiles.Count > 0)
                {
                    _logger.LogInformation(
                        "Zoom {Zoom} summary: {Total} tiles checked, {Rebuilt} rebuilt, {SkippedMissing} skipped (missing sub-tiles), {SkippedNotNewer} skipped (no newer sub-tiles)",
                        zoom, zoomTiles.Count, rebuiltCount, skippedMissingSubTiles, skippedNoNewerSubTiles);
                }
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
