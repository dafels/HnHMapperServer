using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Alignment;
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
    private readonly IAlignmentSolver _alignmentSolver;
    private readonly ILogger<PublicMapGenerationService> _logger;
    private readonly string _gridStorage;
    private readonly ConcurrentQueue<string> _generationQueue = new();
    private readonly ConcurrentDictionary<string, bool> _runningGenerations = new();

    public PublicMapGenerationService(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        IAlignmentSolver alignmentSolver,
        IConfiguration configuration,
        ILogger<PublicMapGenerationService> logger)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _alignmentSolver = alignmentSolver;
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

            // Load BOTH source types — tenant map instances and hmap files — for one merged pipeline.
            var tenantSources = await dbContext.PublicMapSources
                .Where(s => s.PublicMapId == publicMapId)
                .ToListAsync();
            var hmapLinks = await dbContext.PublicMapHmapSources
                .Where(h => h.PublicMapId == publicMapId)
                .ToListAsync();

            var outputPath = Path.Combine(_gridStorage, "public", publicMapId);

            if (tenantSources.Count == 0 && hmapLinks.Count == 0)
            {
                // No sources: empty the map completely so nothing stale survives.
                if (Directory.Exists(outputPath))
                    Directory.Delete(outputPath, recursive: true);
                await dbContext.PublicMapGridIndex.Where(g => g.PublicMapId == publicMapId).ExecuteDeleteAsync();
                await dbContext.PublicMapSourceAlignments.Where(a => a.PublicMapId == publicMapId).ExecuteDeleteAsync();

                publicMap.GenerationStatus = "completed";
                publicMap.GenerationProgress = 100;
                publicMap.TileCount = 0;
                publicMap.MinX = publicMap.MaxX = publicMap.MinY = publicMap.MaxY = null;
                publicMap.LastGeneratedAt = DateTime.UtcNow;
                publicMap.LastGenerationDurationSeconds = 0;
                await dbContext.SaveChangesAsync();

                _logger.LogInformation("Public map {PublicMapId} has no sources, emptied", publicMapId);
                return true;
            }

            // Completely wipe existing tiles before regeneration.
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
                _logger.LogInformation("Wiped existing tiles for public map {PublicMapId}", publicMapId);
            }
            Directory.CreateDirectory(outputPath);

            // Load grid sets for ALL sources and align them in ONE order-independent pass. Tenant and
            // hmap grids share a content-hash id space, so they weave into the same landmasses.
            var tenantLoaded = await PublicMapSourceLoader.LoadAsync(dbContext, tenantSources);
            var hmapLoaded = await PublicMapSourceLoader.LoadHmapAsync(dbContext, hmapLinks, _gridStorage);
            if (hmapLoaded.Count < hmapLinks.Count)
                _logger.LogWarning("{Missing} hmap source file(s) missing or unreadable for {PublicMapId} — skipped",
                    hmapLinks.Count - hmapLoaded.Count, publicMapId);

            var allSets = new List<SourceGridSet>(tenantLoaded.Count + hmapLoaded.Count);
            allSets.AddRange(tenantLoaded.Select(t => t.GridSet));
            allSets.AddRange(hmapLoaded.Select(h => h.GridSet));

            var alignment = _alignmentSolver.Align(allSets);
            var sourceOffsets = alignment.Offsets;

            foreach (var cluster in alignment.Clusters)
                _logger.LogInformation(
                    "Aligned landmass {Index}: {SourceCount} source(s), {GridCount} grids, origin ({X},{Y}), confidence {Conf:F2}{Standalone}",
                    cluster.Index, cluster.SourceKeys.Count, cluster.GridCount,
                    cluster.PlacedOriginX, cluster.PlacedOriginY, cluster.Confidence,
                    cluster.IsStandalone ? " [standalone]" : "");
            foreach (var w in alignment.Warnings)
                _logger.LogWarning("Alignment warning ({Type}): {Message}", w.Type, w.Message);

            // Persist per-source alignment (tenant + hmap) for the UI / re-imports.
            await PersistSourceAlignmentsAsync(dbContext, publicMapId, tenantLoaded, hmapLoaded, alignment);

            // Merge all sources into one set of winners keyed by UNIFIED base-grid coordinate.
            // Tenant cells supply an existing PNG to copy; hmap cells supply a grid to render.
            var winners = new Dictionary<(int x, int y), WinningCell>();

            foreach (var (source, gridSet) in tenantLoaded)
            {
                var offset = sourceOffsets.GetValueOrDefault(gridSet.SourceKey, (X: 0, Y: 0));
                var coordToGridId = new Dictionary<(int, int), string>(gridSet.Grids.Count);
                foreach (var kv in gridSet.Grids)
                    coordToGridId[kv.Value] = kv.Key;

                // Only zoom-0 tiles — zoom 1-6 are regenerated from the merged zoom-0 set.
                var tiles = await dbContext.Tiles
                    .IgnoreQueryFilters()
                    .Where(t => t.TenantId == source.TenantId && t.MapId == source.MapId && t.Zoom == 0)
                    .Select(t => new { t.CoordX, t.CoordY, t.File, t.Cache })
                    .ToListAsync();

                foreach (var tile in tiles)
                {
                    var key = (tile.CoordX + offset.X, tile.CoordY + offset.Y);
                    coordToGridId.TryGetValue((tile.CoordX, tile.CoordY), out var gid);
                    var cand = new WinningCell(CellSourceKind.Tenant, tile.Cache, gridSet.SourceKey, gid,
                        source.TenantId, source.MapId, tile.File, null, null);
                    if (!winners.TryGetValue(key, out var existing) || CellWins(cand, existing))
                        winners[key] = cand;
                }
            }

            foreach (var h in hmapLoaded)
            {
                var offset = sourceOffsets.GetValueOrDefault(h.GridSet.SourceKey, (X: 0, Y: 0));
                foreach (var grid in h.Data.Grids)
                {
                    var key = (grid.TileX + offset.X, grid.TileY + offset.Y);
                    // Only a real content-hash grid id is indexable for tenant-import reuse.
                    var gid = grid.GridId != 0 ? grid.GridIdString : null;
                    var cand = new WinningCell(CellSourceKind.Hmap, grid.ModifiedTime, h.GridSet.SourceKey, gid,
                        null, null, null, h.Link.HmapSourceId, grid);
                    if (!winners.TryGetValue(key, out var existing) || CellWins(cand, existing))
                        winners[key] = cand;
                }
            }

            // Bounds from the unified winner coordinates.
            int? minX = null, maxX = null, minY = null, maxY = null;
            foreach (var (x, y) in winners.Keys)
            {
                minX = minX.HasValue ? Math.Min(minX.Value, x) : x;
                maxX = maxX.HasValue ? Math.Max(maxX.Value, x) : x;
                minY = minY.HasValue ? Math.Min(minY.Value, y) : y;
                maxY = maxY.HasValue ? Math.Max(maxY.Value, y) : y;
            }

            // Prefetch hmap tile textures for the cells an hmap actually wins (bounded + disk-cached).
            TileResourceService? tileResourceService = null;
            if (winners.Values.Any(c => c.Kind == CellSourceKind.Hmap))
            {
                var resourceNames = new HashSet<string>();
                foreach (var c in winners.Values)
                    if (c.Kind == CellSourceKind.Hmap && c.Grid != null)
                        foreach (var ts in c.Grid.Tilesets)
                            resourceNames.Add(ts.ResourceName);
                tileResourceService = new TileResourceService(Path.Combine(_gridStorage, "hmap-tile-cache"));
                await tileResourceService.PrefetchTilesAsync(resourceNames);
                _logger.LogInformation("Prefetched {Count} hmap tile resources for {PublicMapId}", resourceNames.Count, publicMapId);
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

            _logger.LogInformation("Generating {TileCount} 400x400 tiles for public map {PublicMapId} from {CellCount} merged cells",
                totalOutputTiles, publicMapId, winners.Count);

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

                            if (winners.TryGetValue((baseX, baseY), out var cell))
                            {
                                try
                                {
                                    using var baseImg = await RenderCellAsync(cell, tileResourceService);
                                    if (baseImg != null)
                                    {
                                        // Place 100x100 base tile at correct position in 400x400 canvas
                                        img.Mutate(ctx => ctx.DrawImage(baseImg, new Point(dx * 100, dy * 100), 1f));
                                        hasAnyTile = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to render base cell at ({X}, {Y})", baseX, baseY);
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

            // Extract and save thingwall markers from all sources (tenant + hmap), offset-applied.
            var markerCount = await ExtractAndSaveMarkersAsync(dbContext, tenantLoaded, hmapLoaded, sourceOffsets, outputPath, publicMapId);

            // Generate zoom tiles 1-6 from the actually written zoom-0 tiles
            var zoomTileCount = await GenerateZoomTilesAsync(outputPath, copiedZoom0Coords, publicMap, dbContext);

            // Snapshot per-coord winning grid id + source provenance into PublicMapGridIndex so tenant
            // imports can reuse them without ever reading the source data.
            await ReplaceGridIndexAsync(dbContext, publicMapId, winners, copiedZoom0Coords);

            tileResourceService?.Dispose();

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
    /// Wipe + rewrite the durable <c>PublicMapSourceAlignment</c> rows for this public map from the
    /// solver result, for BOTH tenant and hmap sources.
    /// </summary>
    private static async Task PersistSourceAlignmentsAsync(
        ApplicationDbContext dbContext,
        string publicMapId,
        List<(PublicMapSourceEntity Source, SourceGridSet GridSet)> tenantLoaded,
        List<HmapLoadedSource> hmapLoaded,
        AlignmentResult result)
    {
        await dbContext.PublicMapSourceAlignments
            .Where(a => a.PublicMapId == publicMapId)
            .ExecuteDeleteAsync();

        // Sum of accepted-edge matches incident to a source = how strongly it's tied to its cluster.
        var matchByKey = new Dictionary<string, int>();
        foreach (var e in result.Edges)
        {
            if (!e.Accepted) continue;
            matchByKey[e.SourceA] = matchByKey.GetValueOrDefault(e.SourceA) + e.TotalMatches;
            matchByKey[e.SourceB] = matchByKey.GetValueOrDefault(e.SourceB) + e.TotalMatches;
        }

        var tenantByKey = tenantLoaded.ToDictionary(t => t.GridSet.SourceKey, t => t.Source);
        var hmapByKey = hmapLoaded.ToDictionary(h => h.GridSet.SourceKey, h => h.Link);
        var clusterByKey = new Dictionary<string, AlignmentCluster>();
        foreach (var cluster in result.Clusters)
            foreach (var key in cluster.SourceKeys)
                clusterByKey[key] = cluster;

        var now = DateTime.UtcNow;
        var rows = new List<PublicMapSourceAlignmentEntity>();
        foreach (var (key, off) in result.Offsets)
        {
            if (!clusterByKey.TryGetValue(key, out var cluster)) continue;
            var row = new PublicMapSourceAlignmentEntity
            {
                PublicMapId = publicMapId,
                ComponentIndex = cluster.Index,
                UnifiedOffsetX = off.X,
                UnifiedOffsetY = off.Y,
                MatchCountToComponent = matchByKey.GetValueOrDefault(key),
                AlignmentConfidence = cluster.Confidence,
                IsStandalone = cluster.IsStandalone,
                ComputedAt = now
            };
            if (tenantByKey.TryGetValue(key, out var ts))
            {
                row.SourceType = "Tenant";
                row.SourceTenantId = ts.TenantId;
                row.SourceMapId = ts.MapId;
            }
            else if (hmapByKey.TryGetValue(key, out var hl))
            {
                row.SourceType = "Hmap";
                row.SourceHmapId = hl.HmapSourceId;
            }
            else continue;
            rows.Add(row);
        }

        if (rows.Count > 0)
        {
            await dbContext.PublicMapSourceAlignments.AddRangeAsync(rows);
            await dbContext.SaveChangesAsync();
        }
    }

    private enum CellSourceKind { Tenant, Hmap }

    /// <summary>One unified base-grid cell's winning source: a tenant tile to copy, or an hmap grid
    /// to render. Carries provenance + the freshness used for conflict resolution.</summary>
    private sealed record WinningCell(
        CellSourceKind Kind,
        long Freshness,
        string SourceKey,
        string? GridId,
        string? TenantId,
        int? MapId,
        string? TileFile,
        int? HmapSourceId,
        HmapGridData? Grid);

    /// <summary>Deterministic cell winner — see <see cref="CellConflict"/>.</summary>
    private static bool CellWins(WinningCell c, WinningCell existing)
        => CellConflict.Wins(
            c.Kind == CellSourceKind.Tenant, c.Freshness, c.SourceKey, c.GridId,
            existing.Kind == CellSourceKind.Tenant, existing.Freshness, existing.SourceKey, existing.GridId);

    /// <summary>Render one 100×100 base cell: copy the tenant PNG from disk, or render the hmap grid.</summary>
    private async Task<Image<Rgba32>?> RenderCellAsync(WinningCell cell, TileResourceService? tileResourceService)
    {
        if (cell.Kind == CellSourceKind.Tenant)
        {
            var sourcePath = Path.Combine(_gridStorage, cell.TileFile!);
            return File.Exists(sourcePath) ? await Image.LoadAsync<Rgba32>(sourcePath) : null;
        }
        if (tileResourceService != null && cell.Grid != null)
            return await RenderHmapGridAsync(cell.Grid, tileResourceService);
        return null;
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
        List<(PublicMapSourceEntity Source, SourceGridSet GridSet)> tenantLoaded,
        List<HmapLoadedSource> hmapLoaded,
        IReadOnlyDictionary<string, (int X, int Y)> sourceOffsets,
        string outputPath,
        string publicMapId)
    {
        var allMarkers = new List<PublicMapMarkerDto>();

        // Tenant thingwall markers.
        foreach (var (source, gridSet) in tenantLoaded)
        {
            var offset = sourceOffsets.GetValueOrDefault(gridSet.SourceKey, (X: 0, Y: 0));

            var markers = await dbContext.Markers
                .IgnoreQueryFilters()
                .Where(m => m.TenantId == source.TenantId
                           && m.Image.Contains("thingwall")
                           && !m.Hidden)
                .ToListAsync();
            if (markers.Count == 0)
                continue;

            var gridIds = markers.Select(m => m.GridId).Distinct().ToList();
            var grids = await dbContext.Grids
                .IgnoreQueryFilters()
                .Where(g => g.TenantId == source.TenantId
                           && g.Map == source.MapId
                           && gridIds.Contains(g.Id))
                .ToDictionaryAsync(g => g.Id, g => (g.CoordX, g.CoordY));

            foreach (var marker in markers)
            {
                if (!grids.TryGetValue(marker.GridId, out var gridCoord))
                    continue;

                allMarkers.Add(new PublicMapMarkerDto
                {
                    Id = marker.Id,
                    Name = marker.Name,
                    X = (gridCoord.CoordX + offset.X) * 100 + marker.PositionX,
                    Y = (gridCoord.CoordY + offset.Y) * 100 + marker.PositionY,
                    Image = marker.Image
                });
            }
        }

        // Hmap thingwall markers — apply the SAME alignment offset (previously omitted).
        foreach (var h in hmapLoaded)
        {
            var offset = sourceOffsets.GetValueOrDefault(h.GridSet.SourceKey, (X: 0, Y: 0));
            foreach (var marker in h.Data.Markers)
            {
                if (marker is not HmapSMarker sMarker) continue;
                if (!sMarker.ResourceName.Contains("thingwall")) continue;

                // Hmap marker coords are absolute TILE units; decompose to grid + intra-grid position.
                var gridX = (int)Math.Floor(marker.TileX / 100.0);
                var gridY = (int)Math.Floor(marker.TileY / 100.0);
                var posX = ((marker.TileX % 100) + 100) % 100;
                var posY = ((marker.TileY % 100) + 100) % 100;

                allMarkers.Add(new PublicMapMarkerDto
                {
                    Id = (int)(sMarker.ObjectId % int.MaxValue),
                    Name = marker.Name,
                    X = (gridX + offset.X) * 100 + posX,
                    Y = (gridY + offset.Y) * 100 + posY,
                    Image = sMarker.ResourceName
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

    /// <summary>
    /// Wipe + repopulate <c>PublicMapGridIndex</c> rows for the regenerated PUBLIC map.
    /// Called by both generation paths so the snapshot is consistent regardless of source type.
    ///
    /// Only zoom-0 winning entries with a non-null gridId AND that actually ended up on disk
    /// (<paramref name="copiedZoom0Coords"/>) get an index row — entries we computed but never
    /// wrote shouldn't claim a grid identity in the snapshot.
    /// </summary>
    private async Task ReplaceGridIndexAsync(
        ApplicationDbContext dbContext,
        string publicMapId,
        Dictionary<(int x, int y), WinningCell> winners,
        HashSet<(int x, int y)> copiedZoom0Coords)
    {
        // Wipe stale entries for this public map, then refresh the snapshot.
        await dbContext.PublicMapGridIndex
            .Where(g => g.PublicMapId == publicMapId)
            .ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        // Build base-coord set from the actual on-disk zoom-0 output tiles so we don't index
        // sub-grids the consumer can never read back.
        var diskCoveredBaseCoords = new HashSet<(int x, int y)>();
        foreach (var (tx, ty) in copiedZoom0Coords)
        {
            for (int dx = 0; dx < 4; dx++)
            for (int dy = 0; dy < 4; dy++)
                diskCoveredBaseCoords.Add((tx * 4 + dx, ty * 4 + dy));
        }

        var batch = new List<PublicMapGridIndexEntity>();
        foreach (var ((x, y), cell) in winners)
        {
            if (cell.GridId == null) continue;
            if (!diskCoveredBaseCoords.Contains((x, y))) continue;

            batch.Add(new PublicMapGridIndexEntity
            {
                PublicMapId = publicMapId,
                UnifiedX = x,
                UnifiedY = y,
                GridId = cell.GridId,
                SnapshotCache = cell.Freshness,
                IndexedAt = now,
                SourceType = cell.Kind == CellSourceKind.Tenant ? "Tenant" : "Hmap",
                SourceTenantId = cell.TenantId,
                SourceMapId = cell.MapId,
                SourceHmapId = cell.HmapSourceId
            });
        }

        if (batch.Count > 0)
        {
            await dbContext.PublicMapGridIndex.AddRangeAsync(batch);
            await dbContext.SaveChangesAsync();
        }

        _logger.LogInformation(
            "PublicMapGridIndex for {PublicMapId}: wrote {Count} rows",
            publicMapId, batch.Count);
    }

    /// <summary>
    /// HMap-sources variant: writes one index row per rendered HMap grid using GridIdString as
    /// the opaque content hash. Same wipe-then-repopulate semantics as
    /// <see cref="ReplaceGridIndexAsync"/>.
    /// </summary>
    private async Task ReplaceGridIndexFromHmapAsync(
        ApplicationDbContext dbContext,
        string publicMapId,
        Dictionary<(int x, int y), (HmapGridData grid, int priority, long hmapSourceId)> allGrids,
        HashSet<(int x, int y)> copiedZoom0Coords)
    {
        await dbContext.PublicMapGridIndex
            .Where(g => g.PublicMapId == publicMapId)
            .ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        var diskCoveredBaseCoords = new HashSet<(int x, int y)>();
        foreach (var (tx, ty) in copiedZoom0Coords)
        {
            for (int dx = 0; dx < 4; dx++)
            for (int dy = 0; dy < 4; dy++)
                diskCoveredBaseCoords.Add((tx * 4 + dx, ty * 4 + dy));
        }

        var batch = new List<PublicMapGridIndexEntity>();
        foreach (var ((x, y), info) in allGrids)
        {
            if (!diskCoveredBaseCoords.Contains((x, y))) continue;
            if (string.IsNullOrEmpty(info.grid.GridIdString)) continue;

            batch.Add(new PublicMapGridIndexEntity
            {
                PublicMapId = publicMapId,
                UnifiedX = x,
                UnifiedY = y,
                GridId = info.grid.GridIdString,
                // No source Tiles.Cache for HMap sources; use 0 as a sentinel.
                SnapshotCache = 0,
                IndexedAt = now
            });
        }

        if (batch.Count > 0)
        {
            await dbContext.PublicMapGridIndex.AddRangeAsync(batch);
            await dbContext.SaveChangesAsync();
        }

        _logger.LogInformation(
            "PublicMapGridIndex (HMap sources) for {PublicMapId}: wrote {Count} rows",
            publicMapId, batch.Count);
    }

    /// <summary>
    /// SUPERSEDED — the unified <see cref="StartGenerationAsync"/> now merges tenant AND hmap sources
    /// in one aligned pass, so this legacy hmap-only path is no longer wired (the endpoint and
    /// background service both route through the unified pipeline). Retained temporarily for
    /// interface compatibility; safe to delete along with ReplaceGridIndexFromHmapAsync and
    /// ExtractAndSaveMarkersFromHmapSourcesAsync.
    /// </summary>
    public async Task<bool> StartGenerationFromHmapSourcesAsync(string publicMapId)
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

            _logger.LogInformation("Starting HMap source generation for public map {PublicMapId}", publicMapId);

            // Get all HMap sources
            var hmapSources = await dbContext.PublicMapHmapSources
                .Where(pms => pms.PublicMapId == publicMapId)
                .OrderByDescending(pms => pms.Priority)
                .ThenBy(pms => pms.AddedAt)
                .ToListAsync();

            if (!hmapSources.Any())
            {
                publicMap.GenerationStatus = "completed";
                publicMap.GenerationProgress = 100;
                publicMap.TileCount = 0;
                publicMap.LastGeneratedAt = DateTime.UtcNow;
                publicMap.LastGenerationDurationSeconds = 0;
                await dbContext.SaveChangesAsync();

                _logger.LogInformation("Public map {PublicMapId} has no HMap sources, nothing to generate", publicMapId);
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

            // Parse all HMap files and collect grid data with priority
            var hmapReader = new HmapReader();
            var allGrids = new Dictionary<(int x, int y), (HmapGridData grid, int priority, long hmapSourceId)>();
            var tileCacheDir = Path.Combine(_gridStorage, "hmap-tile-cache");

            // Collect all tile resource names needed
            var allResourceNames = new HashSet<string>();

            foreach (var source in hmapSources)
            {
                var hmapSource = await dbContext.HmapSources.FindAsync(source.HmapSourceId);
                if (hmapSource == null) continue;

                var hmapFilePath = Path.Combine(_gridStorage, hmapSource.FilePath);
                if (!File.Exists(hmapFilePath))
                {
                    _logger.LogWarning("HMap file not found: {FilePath}", hmapFilePath);
                    continue;
                }

                try
                {
                    await using var fileStream = new FileStream(hmapFilePath, FileMode.Open, FileAccess.Read);
                    var hmapData = hmapReader.Read(fileStream);

                    foreach (var grid in hmapData.Grids)
                    {
                        var key = (grid.TileX, grid.TileY);
                        // Higher priority sources overwrite lower priority
                        if (!allGrids.TryGetValue(key, out var existing) || source.Priority > existing.priority)
                        {
                            allGrids[key] = (grid, source.Priority, source.HmapSourceId);
                        }

                        // Collect resource names for prefetching
                        foreach (var tileset in grid.Tilesets)
                        {
                            allResourceNames.Add(tileset.ResourceName);
                        }
                    }

                    _logger.LogInformation("Parsed HMap source {SourceId} ({Name}): {GridCount} grids",
                        source.HmapSourceId, hmapSource.Name, hmapData.Grids.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse HMap source {SourceId}", source.HmapSourceId);
                }
            }

            if (allGrids.Count == 0)
            {
                publicMap.GenerationStatus = "completed";
                publicMap.GenerationProgress = 100;
                publicMap.TileCount = 0;
                publicMap.LastGeneratedAt = DateTime.UtcNow;
                publicMap.LastGenerationDurationSeconds = (int)stopwatch.Elapsed.TotalSeconds;
                await dbContext.SaveChangesAsync();

                _logger.LogWarning("No grids found in HMap sources for public map {PublicMapId}", publicMapId);
                return true;
            }

            // Track bounds
            var minX = allGrids.Keys.Min(k => k.x);
            var maxX = allGrids.Keys.Max(k => k.x);
            var minY = allGrids.Keys.Min(k => k.y);
            var maxY = allGrids.Keys.Max(k => k.y);

            // Prefetch tile resources from Haven server
            publicMap.GenerationProgress = 5;
            await dbContext.SaveChangesAsync();

            using var tileResourceService = new TileResourceService(tileCacheDir);
            await tileResourceService.PrefetchTilesAsync(allResourceNames);

            publicMap.GenerationProgress = 15;
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("Prefetched {ResourceCount} tile resources, rendering {GridCount} grids",
                allResourceNames.Count, allGrids.Count);

            // Calculate output tile bounds (4x4 base grids per output tile)
            var outMinX = (int)Math.Floor(minX / 4.0);
            var outMaxX = (int)Math.Floor(maxX / 4.0);
            var outMinY = (int)Math.Floor(minY / 4.0);
            var outMaxY = (int)Math.Floor(maxY / 4.0);

            var totalOutputTiles = (outMaxX - outMinX + 1) * (outMaxY - outMinY + 1);
            var processedTiles = 0;
            var lastProgressUpdate = 15;

            var zoomDir = Path.Combine(outputPath, "0");
            Directory.CreateDirectory(zoomDir);

            var copiedZoom0Coords = new HashSet<(int x, int y)>();

            var webpEncoder = new WebpEncoder
            {
                Quality = 85,
                Method = WebpEncodingMethod.Default
            };

            // Generate each 400x400 output tile
            for (var tx = outMinX; tx <= outMaxX; tx++)
            {
                for (var ty = outMinY; ty <= outMaxY; ty++)
                {
                    // Create 400x400 transparent canvas
                    using var img = new Image<Rgba32>(400, 400);
                    img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

                    var hasAnyTile = false;

                    // Render each of the 4x4 base grids
                    for (int dx = 0; dx < 4; dx++)
                    {
                        for (int dy = 0; dy < 4; dy++)
                        {
                            var baseX = tx * 4 + dx;
                            var baseY = ty * 4 + dy;
                            var key = (baseX, baseY);

                            if (allGrids.TryGetValue(key, out var gridInfo))
                            {
                                try
                                {
                                    using var gridTile = await RenderHmapGridAsync(gridInfo.grid, tileResourceService);
                                    img.Mutate(ctx => ctx.DrawImage(gridTile, new Point(dx * 100, dy * 100), 1f));
                                    hasAnyTile = true;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to render grid at ({X}, {Y})", baseX, baseY);
                                }
                            }
                        }
                    }

                    // Only save if we had at least one grid
                    if (hasAnyTile)
                    {
                        var outputFile = Path.Combine(zoomDir, $"{tx}_{ty}.webp");
                        await img.SaveAsWebpAsync(outputFile, webpEncoder);
                        copiedZoom0Coords.Add((tx, ty));
                    }

                    processedTiles++;

                    // Update progress (15-50% for tile rendering)
                    var currentProgress = 15 + (totalOutputTiles > 0 ? (processedTiles * 35) / totalOutputTiles : 35);
                    if (currentProgress >= lastProgressUpdate + 5 || processedTiles == totalOutputTiles)
                    {
                        lastProgressUpdate = currentProgress;
                        publicMap.GenerationProgress = currentProgress;
                        await dbContext.SaveChangesAsync();
                    }
                }
            }

            _logger.LogInformation("Rendered {TileCount} zoom-0 tiles for public map {PublicMapId}",
                copiedZoom0Coords.Count, publicMapId);

            // Extract markers from HMap sources
            var markerCount = await ExtractAndSaveMarkersFromHmapSourcesAsync(
                dbContext, hmapSources, outputPath, publicMapId);

            // Generate zoom tiles 1-6
            var zoomTileCount = await GenerateZoomTilesAsync(outputPath, copiedZoom0Coords, publicMap, dbContext);

            // Snapshot the per-coord gridIds so tenant imports can reuse them (HMap GridIdString).
            await ReplaceGridIndexFromHmapAsync(dbContext, publicMapId, allGrids, copiedZoom0Coords);

            stopwatch.Stop();

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
                "Completed HMap source generation for public map {PublicMapId}: {TileCount} tiles in {Duration}s",
                publicMapId, totalGeneratedTiles, stopwatch.Elapsed.TotalSeconds);

            // Invalidate Web service cache
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
            _logger.LogError(ex, "HMap source generation failed for public map {PublicMapId}", publicMapId);

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

    /// <summary>
    /// Renders a single 100x100 grid tile from HMap data.
    /// </summary>
    private async Task<Image<Rgba32>> RenderHmapGridAsync(HmapGridData grid, TileResourceService tileResourceService)
    {
        const int GRID_SIZE = 100;
        var result = new Image<Rgba32>(GRID_SIZE, GRID_SIZE);

        // Load tile textures for this grid
        var tileTex = new Image<Rgba32>?[grid.Tilesets.Count];
        for (int i = 0; i < grid.Tilesets.Count; i++)
        {
            tileTex[i] = await tileResourceService.GetTileImageAsync(grid.Tilesets[i].ResourceName);
        }

        // Pass 1: Base texture sampling
        for (int y = 0; y < GRID_SIZE; y++)
        {
            for (int x = 0; x < GRID_SIZE; x++)
            {
                var tileIndex = y * GRID_SIZE + x;
                if (grid.TileIndices == null || tileIndex >= grid.TileIndices.Length)
                {
                    result[x, y] = new Rgba32(128, 128, 128);
                    continue;
                }

                var tsetIdx = grid.TileIndices[tileIndex];
                if (tsetIdx >= tileTex.Length || tileTex[tsetIdx] == null)
                {
                    result[x, y] = new Rgba32(128, 128, 128);
                    continue;
                }

                var tex = tileTex[tsetIdx]!;
                var tx = ((x % tex.Width) + tex.Width) % tex.Width;
                var ty = ((y % tex.Height) + tex.Height) % tex.Height;
                result[x, y] = tex[tx, ty];
            }
        }

        // Pass 2: Ridge/cliff shading
        if (grid.ZMap != null && grid.TileIndices != null)
        {
            const float CLIFF_THRESHOLD = 11.0f;
            const float CLIFF_BLEND = 0.6f;

            for (int y = 1; y < GRID_SIZE - 1; y++)
            {
                for (int x = 1; x < GRID_SIZE - 1; x++)
                {
                    var idx = y * GRID_SIZE + x;
                    float z = grid.ZMap[idx];
                    bool broken = false;

                    if (Math.Abs(z - grid.ZMap[(y - 1) * GRID_SIZE + x]) > CLIFF_THRESHOLD)
                        broken = true;
                    else if (Math.Abs(z - grid.ZMap[(y + 1) * GRID_SIZE + x]) > CLIFF_THRESHOLD)
                        broken = true;
                    else if (Math.Abs(z - grid.ZMap[y * GRID_SIZE + (x - 1)]) > CLIFF_THRESHOLD)
                        broken = true;
                    else if (Math.Abs(z - grid.ZMap[y * GRID_SIZE + (x + 1)]) > CLIFF_THRESHOLD)
                        broken = true;

                    if (broken)
                    {
                        var color = result[x, y];
                        var f1 = (int)(CLIFF_BLEND * 255);
                        var f2 = 255 - f1;
                        result[x, y] = new Rgba32(
                            (byte)((color.R * f2) / 255),
                            (byte)((color.G * f2) / 255),
                            (byte)((color.B * f2) / 255),
                            color.A
                        );
                    }
                }
            }
        }

        // Pass 3: Tile priority borders
        if (grid.TileIndices != null)
        {
            for (int y = 0; y < GRID_SIZE; y++)
            {
                for (int x = 0; x < GRID_SIZE; x++)
                {
                    var idx = y * GRID_SIZE + x;
                    var tileId = grid.TileIndices[idx];

                    bool hasHigherNeighbor = false;

                    if (x > 0 && grid.TileIndices[idx - 1] > tileId) hasHigherNeighbor = true;
                    if (!hasHigherNeighbor && x < GRID_SIZE - 1 && grid.TileIndices[idx + 1] > tileId) hasHigherNeighbor = true;
                    if (!hasHigherNeighbor && y > 0 && grid.TileIndices[idx - GRID_SIZE] > tileId) hasHigherNeighbor = true;
                    if (!hasHigherNeighbor && y < GRID_SIZE - 1 && grid.TileIndices[idx + GRID_SIZE] > tileId) hasHigherNeighbor = true;

                    if (hasHigherNeighbor)
                        result[x, y] = new Rgba32(0, 0, 0, 255);
                }
            }
        }

        // Dispose tile textures
        foreach (var img in tileTex)
        {
            img?.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Extracts thingwall markers from HMap sources and saves them to markers.json.
    /// </summary>
    private async Task<int> ExtractAndSaveMarkersFromHmapSourcesAsync(
        ApplicationDbContext dbContext,
        List<PublicMapHmapSourceEntity> hmapSources,
        string outputPath,
        string publicMapId)
    {
        var allMarkers = new List<PublicMapMarkerDto>();
        var hmapReader = new HmapReader();

        foreach (var source in hmapSources)
        {
            var hmapSource = await dbContext.HmapSources.FindAsync(source.HmapSourceId);
            if (hmapSource == null) continue;

            var hmapFilePath = Path.Combine(_gridStorage, hmapSource.FilePath);
            if (!File.Exists(hmapFilePath)) continue;

            try
            {
                await using var fileStream = new FileStream(hmapFilePath, FileMode.Open, FileAccess.Read);
                var hmapData = hmapReader.Read(fileStream);

                // Build grid lookup for marker position calculation
                var gridLookup = hmapData.Grids.ToDictionary(
                    g => g.SegmentId + "_" + g.TileX + "_" + g.TileY,
                    g => g
                );

                foreach (var marker in hmapData.Markers)
                {
                    if (marker is not HmapSMarker sMarker) continue;
                    if (!sMarker.ResourceName.Contains("thingwall")) continue;

                    // Calculate absolute position
                    var gridX = marker.TileX / 100;
                    var gridY = marker.TileY / 100;
                    var posX = marker.TileX % 100;
                    var posY = marker.TileY % 100;

                    var absX = gridX * 100 + posX;
                    var absY = gridY * 100 + posY;

                    allMarkers.Add(new PublicMapMarkerDto
                    {
                        Id = (int)(sMarker.ObjectId % int.MaxValue),
                        Name = marker.Name,
                        X = absX,
                        Y = absY,
                        Image = sMarker.ResourceName
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract markers from HMap source {SourceId}", source.HmapSourceId);
            }
        }

        // Deduplicate markers by position
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

        _logger.LogInformation("Saved {MarkerCount} thingwall markers for public map {PublicMapId} from HMap sources",
            uniqueMarkers.Count, publicMapId);

        return uniqueMarkers.Count;
    }
}
