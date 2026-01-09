using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Singleton service for managing public map tile generation.
/// Uses IServiceProvider to create scoped DbContext access.
/// Queue and running state persist across the application lifetime.
/// </summary>
public class PublicMapGenerationService : IPublicMapGenerationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PublicMapGenerationService> _logger;
    private readonly string _gridStorage;
    private readonly ConcurrentQueue<string> _generationQueue = new();
    private readonly ConcurrentDictionary<string, bool> _runningGenerations = new();

    public PublicMapGenerationService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<PublicMapGenerationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _gridStorage = configuration["GridStorage"] ?? "map";
    }

    public async Task<bool> StartGenerationAsync(string publicMapId)
    {
        // Check if already running
        if (_runningGenerations.ContainsKey(publicMapId))
        {
            _logger.LogWarning("Generation already in progress for public map {PublicMapId}", publicMapId);
            return false;
        }

        // Mark as running
        if (!_runningGenerations.TryAdd(publicMapId, true))
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Create a scope for database operations
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Update status to running
            var publicMap = await dbContext.PublicMaps.FirstOrDefaultAsync(p => p.Id == publicMapId);
            if (publicMap == null)
            {
                _logger.LogError("Public map {PublicMapId} not found", publicMapId);
                return false;
            }

            publicMap.GenerationStatus = "running";
            publicMap.GenerationProgress = 0;
            publicMap.GenerationError = null;
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("Starting generation for public map {PublicMapId}", publicMapId);

            // Get all sources
            var sources = await dbContext.PublicMapSources
                .Where(s => s.PublicMapId == publicMapId)
                .ToListAsync();

            if (!sources.Any())
            {
                publicMap.GenerationStatus = "completed";
                publicMap.GenerationProgress = 100;
                publicMap.TileCount = 0;
                publicMap.LastGeneratedAt = DateTime.UtcNow;
                publicMap.LastGenerationDurationSeconds = 0;
                await dbContext.SaveChangesAsync();

                _logger.LogInformation("Public map {PublicMapId} has no sources, nothing to generate", publicMapId);
                return true;
            }

            // Completely wipe existing tiles before regeneration
            var outputPath = Path.Combine(_gridStorage, "public", publicMapId);
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
                _logger.LogInformation("Wiped existing tiles for public map {PublicMapId}", publicMapId);
            }
            Directory.CreateDirectory(outputPath);

            // Calculate offsets automatically from shared grid IDs
            var sourceOffsets = await CalculateSourceOffsetsAsync(dbContext, sources);

            // Get all unique coordinates from source tiles
            // The dictionary key uses UNIFIED coordinates (with offsets applied)
            // This allows tiles from different sources to properly overlap/merge
            var allCoordinates = new Dictionary<(int zoom, int x, int y), (string tenantId, int mapId, string file, long cache)>();

            foreach (var source in sources)
            {
                var offset = sourceOffsets.GetValueOrDefault(source.Id, (X: 0, Y: 0));

                // Only fetch zoom-0 tiles - we'll generate zoom levels 1-6 from the merged zoom-0 tiles
                var tiles = await dbContext.Tiles
                    .IgnoreQueryFilters()
                    .Where(t => t.TenantId == source.TenantId && t.MapId == source.MapId && t.Zoom == 0)
                    .Select(t => new { t.Zoom, t.CoordX, t.CoordY, t.File, t.Cache, t.TenantId, t.MapId })
                    .ToListAsync();

                foreach (var tile in tiles)
                {
                    // Scale offset for this zoom level
                    // At zoom 0: 1:1 offset, at zoom N: offset / 2^N
                    // Bit shift right divides by 2^zoom
                    var scaledOffsetX = offset.X >> tile.Zoom;
                    var scaledOffsetY = offset.Y >> tile.Zoom;

                    // Calculate unified coordinates with offset applied
                    var unifiedX = tile.CoordX + scaledOffsetX;
                    var unifiedY = tile.CoordY + scaledOffsetY;

                    var key = (tile.Zoom, unifiedX, unifiedY);

                    // Keep the newest tile (highest Cache value)
                    if (!allCoordinates.TryGetValue(key, out var existing) || tile.Cache > existing.cache)
                    {
                        allCoordinates[key] = (tile.TenantId, tile.MapId, tile.File, tile.Cache);
                    }
                }
            }

            var totalTiles = allCoordinates.Count;
            var processedTiles = 0;
            var lastProgressUpdate = 0;

            // Track bounds
            int? minX = null, maxX = null, minY = null, maxY = null;

            // Track coordinates that were actually copied (for zoom generation)
            var copiedZoom0Coords = new HashSet<(int x, int y)>();

            _logger.LogInformation("Copying {TileCount} zoom-0 tiles for public map {PublicMapId}", totalTiles, publicMapId);

            foreach (var (coord, tileInfo) in allCoordinates)
            {
                var (zoom, x, y) = coord;
                var (tenantId, mapId, file, _) = tileInfo;

                // Update bounds (only for zoom 0)
                if (zoom == 0)
                {
                    minX = minX.HasValue ? Math.Min(minX.Value, x) : x;
                    maxX = maxX.HasValue ? Math.Max(maxX.Value, x) : x;
                    minY = minY.HasValue ? Math.Min(minY.Value, y) : y;
                    maxY = maxY.HasValue ? Math.Max(maxY.Value, y) : y;
                }

                // Source file path - use the File column from database which has the actual path
                // Zoom 0 tiles use grids/{gridId}.png format, higher zooms use {mapId}/{zoom}/{x}_{y}.png
                var sourcePath = Path.Combine(_gridStorage, file);

                // Destination file path
                var destDir = Path.Combine(outputPath, zoom.ToString());
                Directory.CreateDirectory(destDir);
                var destPath = Path.Combine(destDir, $"{x}_{y}.png");

                // Copy tile and track success
                if (File.Exists(sourcePath))
                {
                    try
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                        copiedZoom0Coords.Add((x, y));  // Track successful copy
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to copy tile {SourcePath} to {DestPath}", sourcePath, destPath);
                    }
                }
                else
                {
                    _logger.LogWarning("Source tile not found: {SourcePath} (tenant: {TenantId}, map: {MapId})",
                        sourcePath, tenantId, mapId);
                }

                processedTiles++;

                // Update progress every 5%
                var currentProgress = totalTiles > 0 ? (processedTiles * 50) / totalTiles : 50; // First 50% for copying
                if (currentProgress >= lastProgressUpdate + 5 || processedTiles == totalTiles)
                {
                    lastProgressUpdate = currentProgress;
                    publicMap.GenerationProgress = currentProgress;
                    await dbContext.SaveChangesAsync();
                }
            }

            _logger.LogInformation("Successfully copied {CopiedCount} of {TotalCount} zoom-0 tiles for public map {PublicMapId}",
                copiedZoom0Coords.Count, totalTiles, publicMapId);

            // Extract and save thingwall markers from all sources
            var markerCount = await ExtractAndSaveMarkersAsync(dbContext, sources, sourceOffsets, outputPath, publicMapId);

            // Generate zoom tiles 1-6 from the actually copied zoom-0 tiles
            var zoomTileCount = await GenerateZoomTilesAsync(outputPath, copiedZoom0Coords, publicMap, dbContext);

            stopwatch.Stop();

            // Update total tile count to include zoom tiles
            totalTiles += zoomTileCount;

            // Update final status
            publicMap.GenerationStatus = "completed";
            publicMap.GenerationProgress = 100;
            publicMap.TileCount = totalTiles;
            publicMap.LastGeneratedAt = DateTime.UtcNow;
            publicMap.LastGenerationDurationSeconds = (int)stopwatch.Elapsed.TotalSeconds;
            publicMap.MinX = minX;
            publicMap.MaxX = maxX;
            publicMap.MinY = minY;
            publicMap.MaxY = maxY;
            await dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Completed generation for public map {PublicMapId}: {TileCount} tiles in {Duration}s",
                publicMapId, totalTiles, stopwatch.Elapsed.TotalSeconds);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generation failed for public map {PublicMapId}", publicMapId);

            // Update error status
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var publicMap = await dbContext.PublicMaps.FirstOrDefaultAsync(p => p.Id == publicMapId);
                if (publicMap != null)
                {
                    publicMap.GenerationStatus = "failed";
                    publicMap.GenerationError = ex.Message;
                    publicMap.LastGenerationDurationSeconds = (int)stopwatch.Elapsed.TotalSeconds;
                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception updateEx)
            {
                _logger.LogError(updateEx, "Failed to update error status for public map {PublicMapId}", publicMapId);
            }

            return false;
        }
        finally
        {
            _runningGenerations.TryRemove(publicMapId, out _);
        }
    }

    public Task<bool> IsGenerationRunningAsync(string publicMapId)
    {
        return Task.FromResult(_runningGenerations.ContainsKey(publicMapId));
    }

    public void QueueGeneration(string publicMapId)
    {
        if (!_generationQueue.Contains(publicMapId))
        {
            _generationQueue.Enqueue(publicMapId);
            _logger.LogInformation("Queued generation for public map {PublicMapId}", publicMapId);
        }
    }

    public string? DequeueGeneration()
    {
        return _generationQueue.TryDequeue(out var publicMapId) ? publicMapId : null;
    }

    public bool HasQueuedGenerations()
    {
        return !_generationQueue.IsEmpty;
    }

    /// <summary>
    /// Calculates coordinate offsets for each source by finding shared grid IDs.
    /// Uses the same approach as GridService.ProcessGridUpdateAsync for map merging.
    /// </summary>
    private async Task<Dictionary<int, (int X, int Y)>> CalculateSourceOffsetsAsync(
        ApplicationDbContext dbContext,
        List<PublicMapSourceEntity> sources)
    {
        var offsets = new Dictionary<int, (int X, int Y)>();

        if (sources.Count == 0)
            return offsets;

        // First source is the base (offset 0,0)
        var baseSource = sources.First();
        offsets[baseSource.Id] = (0, 0);

        if (sources.Count == 1)
            return offsets;

        // Load base grids: gridId → (coordX, coordY)
        var baseGrids = await dbContext.Grids
            .IgnoreQueryFilters()
            .Where(g => g.TenantId == baseSource.TenantId && g.Map == baseSource.MapId)
            .ToDictionaryAsync(g => g.Id, g => (g.CoordX, g.CoordY));

        _logger.LogInformation(
            "Base source {SourceId} ({TenantId}/{MapId}) has {GridCount} grids",
            baseSource.Id, baseSource.TenantId, baseSource.MapId, baseGrids.Count);

        // Calculate offset for each other source
        foreach (var source in sources.Skip(1))
        {
            var sourceGrids = await dbContext.Grids
                .IgnoreQueryFilters()
                .Where(g => g.TenantId == source.TenantId && g.Map == source.MapId)
                .ToDictionaryAsync(g => g.Id, g => (g.CoordX, g.CoordY));

            _logger.LogInformation(
                "Source {SourceId} ({TenantId}/{MapId}) has {GridCount} grids",
                source.Id, source.TenantId, source.MapId, sourceGrids.Count);

            // Find first shared grid ID
            var sharedGridId = baseGrids.Keys.FirstOrDefault(id => sourceGrids.ContainsKey(id));

            if (sharedGridId != null)
            {
                var baseCoord = baseGrids[sharedGridId];
                var sourceCoord = sourceGrids[sharedGridId];
                var calculatedOffset = (X: baseCoord.CoordX - sourceCoord.CoordX,
                                        Y: baseCoord.CoordY - sourceCoord.CoordY);
                offsets[source.Id] = calculatedOffset;

                _logger.LogInformation(
                    "Auto-aligned source {SourceId} using shared grid {GridId}: " +
                    "base coord ({BaseX}, {BaseY}), source coord ({SourceX}, {SourceY}) → offset ({OffsetX}, {OffsetY})",
                    source.Id, sharedGridId,
                    baseCoord.CoordX, baseCoord.CoordY,
                    sourceCoord.CoordX, sourceCoord.CoordY,
                    calculatedOffset.X, calculatedOffset.Y);
            }
            else
            {
                offsets[source.Id] = (0, 0);
                _logger.LogWarning(
                    "No shared grids found between base source and source {SourceId} ({TenantId}/{MapId}) - using offset (0, 0). " +
                    "Maps may not align correctly.",
                    source.Id, source.TenantId, source.MapId);
            }
        }

        return offsets;
    }

    /// <summary>
    /// Generates zoom tiles 1-6 from the merged zoom-0 tiles.
    /// Each higher zoom level combines 4 tiles from the previous level into 1 tile.
    /// </summary>
    private async Task<int> GenerateZoomTilesAsync(
        string outputPath,
        HashSet<(int x, int y)> zoom0Coords,
        PublicMapEntity publicMap,
        ApplicationDbContext dbContext)
    {
        _logger.LogInformation("Starting zoom tile generation from {Zoom0Count} zoom-0 tiles", zoom0Coords.Count);

        if (zoom0Coords.Count == 0)
        {
            _logger.LogWarning("No zoom-0 tiles were copied, skipping zoom generation");
            return 0;
        }

        var currentLevelCoords = zoom0Coords;
        var totalZoomTiles = 0;
        var overallStopwatch = Stopwatch.StartNew();

        for (int zoom = 1; zoom <= 6; zoom++)
        {
            var zoomStopwatch = Stopwatch.StartNew();
            var parentCoords = new HashSet<(int x, int y)>();

            // Calculate parent coordinates for this zoom level
            // Each parent tile covers 4 child tiles (2x2 grid)
            // Use floor division for negative coordinates (same as Coord.Parent())
            foreach (var (x, y) in currentLevelCoords)
            {
                var px = x < 0 ? (x - 1) / 2 : x / 2;
                var py = y < 0 ? (y - 1) / 2 : y / 2;
                parentCoords.Add((px, py));
            }

            if (parentCoords.Count == 0)
            {
                _logger.LogInformation("No tiles to generate at zoom level {Zoom}", zoom);
                break;
            }

            _logger.LogInformation("Generating zoom-{Zoom}: {ParentCount} tiles from {ChildCount} children",
                zoom, parentCoords.Count, currentLevelCoords.Count);

            var zoomDir = Path.Combine(outputPath, zoom.ToString());
            Directory.CreateDirectory(zoomDir);
            var childDir = Path.Combine(outputPath, (zoom - 1).ToString());

            var generatedCount = 0;
            var processedCount = 0;
            var lastLogTime = DateTime.UtcNow;
            var parentList = parentCoords.ToList(); // Convert to list for progress tracking

            foreach (var (px, py) in parentList)
            {
                // Create 100x100 transparent canvas
                using var img = new Image<Rgba32>(100, 100);
                img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

                var hasAnyChild = false;

                // Load and place each of the 4 child tiles
                for (int dx = 0; dx <= 1; dx++)
                {
                    for (int dy = 0; dy <= 1; dy++)
                    {
                        var childX = px * 2 + dx;
                        var childY = py * 2 + dy;
                        var childPath = Path.Combine(childDir, $"{childX}_{childY}.png");

                        if (File.Exists(childPath))
                        {
                            try
                            {
                                using var childImg = await Image.LoadAsync<Rgba32>(childPath);
                                // Resize child tile to 50x50 (quarter of parent)
                                childImg.Mutate(ctx => ctx.Resize(50, 50));
                                // Place in appropriate quadrant
                                img.Mutate(ctx => ctx.DrawImage(childImg, new Point(50 * dx, 50 * dy), 1f));
                                hasAnyChild = true;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to load child tile {ChildPath}", childPath);
                            }
                        }
                    }
                }

                // Only save if we had at least one child tile
                if (hasAnyChild)
                {
                    var outputFile = Path.Combine(zoomDir, $"{px}_{py}.png");
                    await img.SaveAsPngAsync(outputFile);
                    generatedCount++;
                }

                processedCount++;

                // Log progress every 10 seconds or every 1000 tiles
                if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 10 || processedCount % 1000 == 0)
                {
                    var percentComplete = (processedCount * 100) / parentList.Count;
                    _logger.LogInformation("Zoom-{Zoom} progress: {Processed}/{Total} ({Percent}%) - {Generated} generated",
                        zoom, processedCount, parentList.Count, percentComplete, generatedCount);
                    lastLogTime = DateTime.UtcNow;

                    // Update database progress more frequently during large operations
                    var zoomProgress = 50 + (((zoom - 1) * 50) / 6) + ((processedCount * 50) / (parentList.Count * 6));
                    publicMap.GenerationProgress = Math.Min(99, zoomProgress); // Cap at 99 until fully done
                    await dbContext.SaveChangesAsync();
                }
            }

            totalZoomTiles += generatedCount;
            currentLevelCoords = parentCoords;

            zoomStopwatch.Stop();
            _logger.LogInformation("Completed zoom-{Zoom}: {Count} tiles generated in {Duration:F1}s",
                zoom, generatedCount, zoomStopwatch.Elapsed.TotalSeconds);

            // Update progress (50-100% for zoom generation)
            var finalZoomProgress = 50 + ((zoom * 50) / 6);
            publicMap.GenerationProgress = finalZoomProgress;
            await dbContext.SaveChangesAsync();
        }

        overallStopwatch.Stop();
        _logger.LogInformation("Zoom generation complete: {TotalTiles} tiles across all zoom levels in {Duration:F1}s",
            totalZoomTiles, overallStopwatch.Elapsed.TotalSeconds);

        return totalZoomTiles;
    }

    /// <summary>
    /// Extracts thingwall markers from all source tenant/maps and saves them to markers.json.
    /// Applies the same coordinate offsets as tiles for proper alignment.
    /// </summary>
    private async Task<int> ExtractAndSaveMarkersAsync(
        ApplicationDbContext dbContext,
        List<PublicMapSourceEntity> sources,
        Dictionary<int, (int X, int Y)> sourceOffsets,
        string outputPath,
        string publicMapId)
    {
        var allMarkers = new List<PublicMapMarkerDto>();

        foreach (var source in sources)
        {
            var offset = sourceOffsets.GetValueOrDefault(source.Id, (X: 0, Y: 0));

            // Query thingwall markers (Image contains "thingwall") that are not hidden
            var markers = await dbContext.Markers
                .IgnoreQueryFilters()
                .Where(m => m.TenantId == source.TenantId
                           && m.Image.Contains("thingwall")
                           && !m.Hidden)
                .ToListAsync();

            if (markers.Count == 0)
                continue;

            // Get grid coordinates for these markers
            var gridIds = markers.Select(m => m.GridId).Distinct().ToList();
            var grids = await dbContext.Grids
                .IgnoreQueryFilters()
                .Where(g => g.TenantId == source.TenantId
                           && g.Map == source.MapId
                           && gridIds.Contains(g.Id))
                .ToDictionaryAsync(g => g.Id, g => (g.CoordX, g.CoordY));

            foreach (var marker in markers)
            {
                // Skip if grid is not in this map
                if (!grids.TryGetValue(marker.GridId, out var gridCoord))
                    continue;

                // Calculate absolute position with offset applied
                // Grid coordinate + offset gives the unified grid position
                // Then multiply by 100 (tile size) and add marker position within grid
                var absX = (gridCoord.CoordX + offset.X) * 100 + marker.PositionX;
                var absY = (gridCoord.CoordY + offset.Y) * 100 + marker.PositionY;

                allMarkers.Add(new PublicMapMarkerDto
                {
                    Id = marker.Id,
                    Name = marker.Name,
                    X = absX,
                    Y = absY,
                    Image = marker.Image
                });
            }
        }

        // Deduplicate markers by position (same position might exist from multiple sources)
        var uniqueMarkers = allMarkers
            .GroupBy(m => (m.X, m.Y))
            .Select(g => g.First())
            .ToList();

        // Save markers to JSON file
        var markersPath = Path.Combine(outputPath, "markers.json");
        var markersJson = JsonSerializer.Serialize(uniqueMarkers, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
        await File.WriteAllTextAsync(markersPath, markersJson);

        _logger.LogInformation("Saved {MarkerCount} thingwall markers for public map {PublicMapId}",
            uniqueMarkers.Count, publicMapId);

        return uniqueMarkers.Count;
    }
}
