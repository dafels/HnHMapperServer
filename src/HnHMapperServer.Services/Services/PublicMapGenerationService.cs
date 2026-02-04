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
using SixLabors.ImageSharp.Formats.Webp;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PublicMapGenerationService> _logger;
    private readonly string _gridStorage;
    private readonly ConcurrentQueue<string> _generationQueue = new();
    private readonly ConcurrentDictionary<string, bool> _runningGenerations = new();

    public PublicMapGenerationService(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PublicMapGenerationService> logger)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
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

            // Track bounds in original grid coordinates
            int? minX = null, maxX = null, minY = null, maxY = null;

            // Calculate bounds from all zoom-0 coordinates
            foreach (var (coord, _) in allCoordinates)
            {
                var (zoom, x, y) = coord;
                if (zoom == 0)
                {
                    minX = minX.HasValue ? Math.Min(minX.Value, x) : x;
                    maxX = maxX.HasValue ? Math.Max(maxX.Value, x) : x;
                    minY = minY.HasValue ? Math.Min(minY.Value, y) : y;
                    maxY = maxY.HasValue ? Math.Max(maxY.Value, y) : y;
                }
            }

            // Generate 400x400 tiles by combining 4x4 base tiles
            // Each output tile (tx, ty) covers base tiles (4*tx to 4*tx+3, 4*ty to 4*ty+3)
            var copiedZoom0Coords = new HashSet<(int x, int y)>();
            var zoomDir = Path.Combine(outputPath, "0");
            Directory.CreateDirectory(zoomDir);

            // Calculate output tile bounds (divide by 4, using floor for proper rounding)
            var outMinX = minX.HasValue ? (int)Math.Floor(minX.Value / 4.0) : 0;
            var outMaxX = maxX.HasValue ? (int)Math.Floor(maxX.Value / 4.0) : 0;
            var outMinY = minY.HasValue ? (int)Math.Floor(minY.Value / 4.0) : 0;
            var outMaxY = maxY.HasValue ? (int)Math.Floor(maxY.Value / 4.0) : 0;

            var totalOutputTiles = (outMaxX - outMinX + 1) * (outMaxY - outMinY + 1);
            var processedTiles = 0;
            var lastProgressUpdate = 0;

            var webpEncoder = new WebpEncoder
            {
                Quality = 85,
                Method = WebpEncodingMethod.Default
            };

            _logger.LogInformation("Generating {TileCount} 400x400 tiles for public map {PublicMapId} from {SourceCount} source tiles",
                totalOutputTiles, publicMapId, allCoordinates.Count);

            // Generate each 400x400 tile
            for (var tx = outMinX; tx <= outMaxX; tx++)
            {
                for (var ty = outMinY; ty <= outMaxY; ty++)
                {
                    // Create 400x400 transparent canvas
                    using var img = new Image<Rgba32>(400, 400);
                    img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

                    var hasAnyTile = false;

                    // Load and place each of the 4x4 base tiles
                    for (int dx = 0; dx < 4; dx++)
                    {
                        for (int dy = 0; dy < 4; dy++)
                        {
                            var baseX = tx * 4 + dx;
                            var baseY = ty * 4 + dy;
                            var key = (0, baseX, baseY);

                            if (allCoordinates.TryGetValue(key, out var tileInfo))
                            {
                                var sourcePath = Path.Combine(_gridStorage, tileInfo.file);
                                if (File.Exists(sourcePath))
                                {
                                    try
                                    {
                                        using var baseImg = await Image.LoadAsync<Rgba32>(sourcePath);
                                        // Place 100x100 base tile at correct position in 400x400 canvas
                                        img.Mutate(ctx => ctx.DrawImage(baseImg, new Point(dx * 100, dy * 100), 1f));
                                        hasAnyTile = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "Failed to load base tile at ({X}, {Y})", baseX, baseY);
                                    }
                                }
                            }
                        }
                    }

                    // Only save if we had at least one base tile
                    if (hasAnyTile)
                    {
                        var outputFile = Path.Combine(zoomDir, $"{tx}_{ty}.webp");
                        await img.SaveAsWebpAsync(outputFile, webpEncoder);
                        copiedZoom0Coords.Add((tx, ty));
                    }

                    processedTiles++;

                    // Update progress every 5%
                    var currentProgress = totalOutputTiles > 0 ? (processedTiles * 50) / totalOutputTiles : 50;
                    if (currentProgress >= lastProgressUpdate + 5 || processedTiles == totalOutputTiles)
                    {
                        lastProgressUpdate = currentProgress;
                        publicMap.GenerationProgress = currentProgress;
                        await dbContext.SaveChangesAsync();
                    }
                }
            }

            _logger.LogInformation("Successfully generated {GeneratedCount} zoom-0 tiles (400x400) for public map {PublicMapId}",
                copiedZoom0Coords.Count, publicMapId);

            // Extract and save thingwall markers from all sources
            var markerCount = await ExtractAndSaveMarkersAsync(dbContext, sources, sourceOffsets, outputPath, publicMapId);

            // Generate zoom tiles 1-6 from the actually copied zoom-0 tiles
            var zoomTileCount = await GenerateZoomTilesAsync(outputPath, copiedZoom0Coords, publicMap, dbContext);

            stopwatch.Stop();

            // Calculate total tile count: zoom-0 tiles + zoom 1-6 tiles
            var totalGeneratedTiles = copiedZoom0Coords.Count + zoomTileCount;

            // Update final status
            publicMap.GenerationStatus = "completed";
            publicMap.GenerationProgress = 100;
            publicMap.TileCount = totalGeneratedTiles;
            publicMap.LastGeneratedAt = DateTime.UtcNow;
            publicMap.LastGenerationDurationSeconds = (int)stopwatch.Elapsed.TotalSeconds;
            publicMap.MinX = minX;
            publicMap.MaxX = maxX;
            publicMap.MinY = minY;
            publicMap.MaxY = maxY;
            await dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Completed generation for public map {PublicMapId}: {TileCount} tiles in {Duration}s",
                publicMapId, totalGeneratedTiles, stopwatch.Elapsed.TotalSeconds);

            // Invalidate Web service cache to reload fresh tiles
            try
            {
                var webClient = _httpClientFactory.CreateClient("Web");
                await webClient.PostAsync($"/internal/public-cache/invalidate/{publicMapId}", null);
                _logger.LogInformation("Invalidated Web cache for regenerated public map {PublicMapId}", publicMapId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate Web cache for {PublicMapId}", publicMapId);
            }

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

            // WebP encoder for consistent output
            var webpEncoder = new WebpEncoder
            {
                Quality = 85,
                Method = WebpEncodingMethod.Default
            };

            foreach (var (px, py) in parentList)
            {
                // Create 400x400 transparent canvas (matching tile size)
                using var img = new Image<Rgba32>(400, 400);
                img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

                var hasAnyChild = false;

                // Load and place each of the 4 child tiles (now WebP format, 400x400 each)
                for (int dx = 0; dx <= 1; dx++)
                {
                    for (int dy = 0; dy <= 1; dy++)
                    {
                        var childX = px * 2 + dx;
                        var childY = py * 2 + dy;
                        var childPath = Path.Combine(childDir, $"{childX}_{childY}.webp");

                        if (File.Exists(childPath))
                        {
                            try
                            {
                                using var childImg = await Image.LoadAsync<Rgba32>(childPath);
                                // Resize 400x400 child tile to 200x200 (quarter of parent)
                                childImg.Mutate(ctx => ctx.Resize(200, 200));
                                // Place in appropriate quadrant
                                img.Mutate(ctx => ctx.DrawImage(childImg, new Point(200 * dx, 200 * dy), 1f));
                                hasAnyChild = true;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to load child tile {ChildPath}", childPath);
                            }
                        }
                    }
                }

                // Only save if we had at least one child tile (WebP format, 400x400)
                if (hasAnyChild)
                {
                    var outputFile = Path.Combine(zoomDir, $"{px}_{py}.webp");
                    await img.SaveAsWebpAsync(outputFile, webpEncoder);
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
