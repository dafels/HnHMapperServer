using System.Collections.Concurrent;
using System.Diagnostics;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Service for generating 400x400 WebP tiles from base 100x100 tiles.
/// Provides on-the-fly generation with in-memory LRU caching and parallel batch processing.
/// </summary>
public class LargeTileService : ILargeTileService
{
    private const int LargeTileSize = 400;
    private const int BaseTileSize = 100;
    private const int TilesPerLargeTile = 4; // 4x4 base tiles per large tile at zoom 0
    private const string LogPrefix = "[LargeTile]";
    private const int MaxCacheEntries = 500; // ~25MB assuming 50KB average per tile
    private const int MaxParallelism = 4; // Concurrent tile generation limit

    private static readonly WebpEncoder WebpEncoder = new()
    {
        Quality = 85,
        Method = WebpEncodingMethod.Default
    };

    // LRU-style in-memory cache for tile bytes
    private static readonly ConcurrentDictionary<string, CacheEntry> _tileCache = new();
    private static long _cacheAccessCounter = 0;

    // Request deduplication: track in-progress generation tasks to avoid duplicate work
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> _generationInProgress = new();

    // Negative cache: remember tiles that don't exist to avoid repeated DB queries
    private static readonly ConcurrentDictionary<string, DateTime> _nonExistentTileCache = new();
    private const int NegativeCacheTtlMinutes = 5;
    private const int MaxNegativeCacheEntries = 10000;

    // Concurrency limiter: prevent DB overload from too many simultaneous generations
    private static readonly SemaphoreSlim _generationSemaphore = new(8, 8);

    // Track generation statistics for logging
    private static readonly ConcurrentDictionary<string, TenantStats> _tenantStats = new();

    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<LargeTileService> _logger;
    private readonly string _gridStorage;

    public LargeTileService(
        ApplicationDbContext dbContext,
        IConfiguration configuration,
        ILogger<LargeTileService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _gridStorage = configuration["GridStorage"] ?? "map";
    }

    private class CacheEntry
    {
        public byte[] Data { get; init; } = null!;
        public long LastAccess { get; set; }
    }

    private class TenantStats
    {
        public int OnTheFlyGenerated;
        public int OnTheFlyFailed;
        public int CacheHits;
        public int MemoryCacheHits;
        public int NegativeCacheHits;
        public int Coalesced; // Requests that waited on existing generation
        public int DirtyMarked;
        public long TotalGenerationTimeMs;
        public DateTime LastActivity = DateTime.UtcNow;
    }

    private TenantStats GetStats(string tenantId) => _tenantStats.GetOrAdd(tenantId, _ => new TenantStats());

    /// <summary>
    /// Logs current statistics for all active tenants.
    /// </summary>
    public void LogStatsSummary()
    {
        foreach (var (tenantId, stats) in _tenantStats)
        {
            if (stats.LastActivity > DateTime.UtcNow.AddMinutes(-5))
            {
                var avgMs = stats.OnTheFlyGenerated > 0 ? stats.TotalGenerationTimeMs / stats.OnTheFlyGenerated : 0;
                _logger.LogInformation(
                    "{Prefix} Stats [{Tenant}] MemHits={MemHits} DiskHits={DiskHits} NegHits={NegHits} Coalesced={Coalesced} Generated={Generated} Failed={Failed} Dirty={Dirty} AvgGenTime={AvgMs}ms CacheSize={CacheSize} NegCacheSize={NegCacheSize}",
                    LogPrefix, tenantId, stats.MemoryCacheHits, stats.CacheHits, stats.NegativeCacheHits, stats.Coalesced, stats.OnTheFlyGenerated, stats.OnTheFlyFailed, stats.DirtyMarked, avgMs, _tileCache.Count, _nonExistentTileCache.Count);
            }
        }
    }

    public string GetLargeTilePath(string tenantId, int mapId, int zoom, int x, int y)
    {
        return Path.Combine(_gridStorage, "tenants", tenantId, "large", mapId.ToString(), zoom.ToString(), $"{x}_{y}.webp");
    }

    public async Task<byte[]?> GetOrGenerateLargeTileAsync(string tenantId, int mapId, int zoom, int x, int y)
    {
        var cacheKey = $"{tenantId}/{mapId}/{zoom}/{x}_{y}";
        var stats = GetStats(tenantId);
        stats.LastActivity = DateTime.UtcNow;

        // Check in-memory cache first (fastest)
        if (_tileCache.TryGetValue(cacheKey, out var cached))
        {
            cached.LastAccess = Interlocked.Increment(ref _cacheAccessCounter);
            Interlocked.Increment(ref stats.MemoryCacheHits);
            return cached.Data;
        }

        // Check negative cache (tiles that don't exist)
        if (_nonExistentTileCache.TryGetValue(cacheKey, out var expiry))
        {
            if (DateTime.UtcNow < expiry)
            {
                Interlocked.Increment(ref stats.NegativeCacheHits);
                return null;
            }
            // Expired, remove and re-check
            _nonExistentTileCache.TryRemove(cacheKey, out _);
        }

        var path = GetLargeTilePath(tenantId, mapId, zoom, x, y);

        // Check filesystem cache
        if (File.Exists(path))
        {
            Interlocked.Increment(ref stats.CacheHits);
            var bytes = await File.ReadAllBytesAsync(path);
            AddToCache(cacheKey, bytes);
            return bytes;
        }

        // Check if another request is already generating this tile (request coalescing)
        if (_generationInProgress.TryGetValue(cacheKey, out var existingTask))
        {
            Interlocked.Increment(ref stats.Coalesced);
            return await existingTask;
        }

        // Start generation and register it for coalescing
        var generationTask = GenerateLargeTileInternalAsync(tenantId, mapId, zoom, x, y, cacheKey, stats);

        if (_generationInProgress.TryAdd(cacheKey, generationTask))
        {
            try
            {
                return await generationTask;
            }
            finally
            {
                _generationInProgress.TryRemove(cacheKey, out _);
            }
        }
        else
        {
            // Another thread added it first, await that one instead
            Interlocked.Increment(ref stats.Coalesced);
            if (_generationInProgress.TryGetValue(cacheKey, out var otherTask))
            {
                return await otherTask;
            }
            // Race condition: task completed and was removed, try again recursively
            return await GetOrGenerateLargeTileAsync(tenantId, mapId, zoom, x, y);
        }
    }

    /// <summary>
    /// Internal method that performs the actual tile generation.
    /// Separated to support request coalescing via Task sharing.
    /// Semaphore only used for zoom=0 (DB queries) to avoid deadlock with recursive zoom 1-6.
    /// </summary>
    private async Task<byte[]?> GenerateLargeTileInternalAsync(
        string tenantId, int mapId, int zoom, int x, int y,
        string cacheKey, TenantStats stats)
    {
        // Only limit zoom=0 which does DB queries. Zoom 1-6 are recursive and would deadlock.
        var useSemaphore = zoom == 0;
        if (useSemaphore)
        {
            await _generationSemaphore.WaitAsync();
        }

        try
        {
            _logger.LogInformation(
                "{Prefix} GENERATE [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) - on-the-fly request",
                LogPrefix, tenantId, mapId, zoom, x, y);

            var sw = Stopwatch.StartNew();
            var generatedBytes = await GenerateLargeTileAsync(tenantId, mapId, zoom, x, y);
            sw.Stop();

            if (generatedBytes != null)
            {
                Interlocked.Increment(ref stats.OnTheFlyGenerated);
                Interlocked.Add(ref stats.TotalGenerationTimeMs, sw.ElapsedMilliseconds);

                _logger.LogInformation(
                    "{Prefix} GENERATED [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) in {Ms}ms ({Size:F1}KB)",
                    LogPrefix, tenantId, mapId, zoom, x, y, sw.ElapsedMilliseconds, generatedBytes.Length / 1024.0);

                AddToCache(cacheKey, generatedBytes);
                return generatedBytes;
            }
            else
            {
                Interlocked.Increment(ref stats.OnTheFlyFailed);

                // Add to negative cache to avoid repeated checks
                AddToNegativeCache(cacheKey);

                // Use Debug level - this is expected for unexplored areas, not a warning
                _logger.LogDebug(
                    "{Prefix} FAILED [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) - no source tiles found",
                    LogPrefix, tenantId, mapId, zoom, x, y);
                return null;
            }
        }
        finally
        {
            if (useSemaphore)
            {
                _generationSemaphore.Release();
            }
        }
    }

    private void AddToCache(string key, byte[] data)
    {
        // Evict oldest entries if cache is full
        if (_tileCache.Count >= MaxCacheEntries)
        {
            EvictOldestCacheEntries(MaxCacheEntries / 10); // Evict 10% at a time
        }

        _tileCache[key] = new CacheEntry
        {
            Data = data,
            LastAccess = Interlocked.Increment(ref _cacheAccessCounter)
        };
    }

    private void EvictOldestCacheEntries(int count)
    {
        // Filter out null values to handle race condition where entries are removed
        // by another thread while we're iterating
        var oldest = _tileCache
            .Where(kv => kv.Value != null)
            .OrderBy(kv => kv.Value.LastAccess)
            .Take(count)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldest)
        {
            _tileCache.TryRemove(key, out _);
        }
    }

    private void AddToNegativeCache(string key)
    {
        // Evict oldest entries if cache is too large
        if (_nonExistentTileCache.Count >= MaxNegativeCacheEntries)
        {
            EvictExpiredNegativeCacheEntries();
        }

        _nonExistentTileCache[key] = DateTime.UtcNow.AddMinutes(NegativeCacheTtlMinutes);
    }

    private void EvictExpiredNegativeCacheEntries()
    {
        var now = DateTime.UtcNow;
        var expired = _nonExistentTileCache
            .Where(kv => kv.Value < now)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
        {
            _nonExistentTileCache.TryRemove(key, out _);
        }

        // If still too large after removing expired, remove oldest 10%
        if (_nonExistentTileCache.Count >= MaxNegativeCacheEntries)
        {
            var oldest = _nonExistentTileCache
                .OrderBy(kv => kv.Value)
                .Take(MaxNegativeCacheEntries / 10)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldest)
            {
                _nonExistentTileCache.TryRemove(key, out _);
            }
        }
    }

    private void InvalidateCacheEntry(string tenantId, int mapId, int zoom, int x, int y)
    {
        var cacheKey = $"{tenantId}/{mapId}/{zoom}/{x}_{y}";
        _tileCache.TryRemove(cacheKey, out _);
        // Also clear from negative cache - tile might exist now after upload
        _nonExistentTileCache.TryRemove(cacheKey, out _);
    }

    public async Task MarkDirtyAsync(string tenantId, int mapId, int baseX, int baseY)
    {
        var stats = GetStats(tenantId);
        stats.LastActivity = DateTime.UtcNow;

        // Calculate which large tile this base tile belongs to
        var largeTileX = (int)Math.Floor(baseX / (double)TilesPerLargeTile);
        var largeTileY = (int)Math.Floor(baseY / (double)TilesPerLargeTile);

        var deletedCount = 0;
        var deletedZooms = new List<int>();

        // Delete the large tile at zoom 0 and all parent zoom levels
        for (int zoom = 0; zoom <= 6; zoom++)
        {
            // Invalidate in-memory cache
            InvalidateCacheEntry(tenantId, mapId, zoom, largeTileX, largeTileY);

            var path = GetLargeTilePath(tenantId, mapId, zoom, largeTileX, largeTileY);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    deletedCount++;
                    deletedZooms.Add(zoom);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Prefix} DIRTY-DELETE-FAILED [{Tenant}] map={MapId} z={Zoom} ({X},{Y})",
                        LogPrefix, tenantId, mapId, zoom, largeTileX, largeTileY);
                }
            }

            // Calculate parent coordinate for next zoom level
            largeTileX = (int)Math.Floor(largeTileX / 2.0);
            largeTileY = (int)Math.Floor(largeTileY / 2.0);
        }

        Interlocked.Add(ref stats.DirtyMarked, deletedCount);

        if (deletedCount > 0)
        {
            _logger.LogInformation(
                "{Prefix} DIRTY [{Tenant}] map={MapId} base=({BaseX},{BaseY}) -> invalidated {Count} tiles at zooms [{Zooms}]",
                LogPrefix, tenantId, mapId, baseX, baseY, deletedCount, string.Join(",", deletedZooms));
        }
        else
        {
            _logger.LogDebug(
                "{Prefix} DIRTY [{Tenant}] map={MapId} base=({BaseX},{BaseY}) -> no cached tiles to invalidate",
                LogPrefix, tenantId, mapId, baseX, baseY);
        }
    }

    public async Task<int> GenerateMissingTilesAsync(string tenantId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var generatedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;
        var zoomStats = new ConcurrentDictionary<int, (int generated, int skipped)>();

        // Get all maps for this tenant
        var maps = await _dbContext.Maps
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct);

        if (maps.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation(
            "{Prefix} BATCH-START [{Tenant}] scanning {MapCount} maps for missing tiles (parallelism={Parallelism})",
            LogPrefix, tenantId, maps.Count, MaxParallelism);

        foreach (var map in maps)
        {
            if (ct.IsCancellationRequested) break;

            var mapGeneratedCount = 0;
            var mapSkippedCount = 0;

            // Pre-load all base tiles for this map in a single query
            var baseTileFiles = await _dbContext.Tiles
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && t.MapId == map.Id && t.Zoom == 0)
                .ToDictionaryAsync(t => (t.CoordX, t.CoordY), t => t.File, ct);

            if (baseTileFiles.Count == 0) continue;

            // Calculate which large tiles are needed
            var largeTileCoords = baseTileFiles.Keys
                .Select(t => (
                    X: (int)Math.Floor(t.CoordX / (double)TilesPerLargeTile),
                    Y: (int)Math.Floor(t.CoordY / (double)TilesPerLargeTile)
                ))
                .Distinct()
                .ToList();

            // Generate zoom 0 tiles in parallel
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelism,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(largeTileCoords, parallelOptions, async (coord, token) =>
            {
                var path = GetLargeTilePath(tenantId, map.Id, 0, coord.X, coord.Y);
                if (File.Exists(path))
                {
                    Interlocked.Increment(ref mapSkippedCount);
                    zoomStats.AddOrUpdate(0, (0, 1), (_, v) => (v.generated, v.skipped + 1));
                }
                else
                {
                    var result = await GenerateLargeTileWithPreloadAsync(tenantId, map.Id, 0, coord.X, coord.Y, baseTileFiles);
                    if (result != null)
                    {
                        Interlocked.Increment(ref mapGeneratedCount);
                        zoomStats.AddOrUpdate(0, (1, 0), (_, v) => (v.generated + 1, v.skipped));
                    }
                    else
                    {
                        Interlocked.Increment(ref failedCount);
                    }
                }
            });

            // Generate zoom levels 1-6 (bottom-up, ensuring children exist first)
            // Use file-based generation (no DbContext) since children were just generated above
            for (int zoom = 1; zoom <= 6 && !ct.IsCancellationRequested; zoom++)
            {
                // Calculate parent coordinates from previous zoom level
                largeTileCoords = largeTileCoords
                    .Select(c => (
                        X: (int)Math.Floor(c.X / 2.0),
                        Y: (int)Math.Floor(c.Y / 2.0)
                    ))
                    .Distinct()
                    .ToList();

                var currentZoom = zoom; // Capture for closure
                await Parallel.ForEachAsync(largeTileCoords, parallelOptions, async (coord, token) =>
                {
                    var path = GetLargeTilePath(tenantId, map.Id, currentZoom, coord.X, coord.Y);
                    if (File.Exists(path))
                    {
                        Interlocked.Increment(ref mapSkippedCount);
                        zoomStats.AddOrUpdate(currentZoom, (0, 1), (_, v) => (v.generated, v.skipped + 1));
                    }
                    else
                    {
                        // Use file-based generation that doesn't touch DbContext
                        var result = await GenerateZoomNFromFilesAsync(tenantId, map.Id, currentZoom, coord.X, coord.Y);
                        if (result != null)
                        {
                            Interlocked.Increment(ref mapGeneratedCount);
                            zoomStats.AddOrUpdate(currentZoom, (1, 0), (_, v) => (v.generated + 1, v.skipped));
                        }
                        else
                        {
                            Interlocked.Increment(ref failedCount);
                        }
                    }
                });
            }

            Interlocked.Add(ref generatedCount, mapGeneratedCount);
            Interlocked.Add(ref skippedCount, mapSkippedCount);

            // Log per-map summary if any work was done
            if (mapGeneratedCount > 0)
            {
                _logger.LogInformation(
                    "{Prefix} BATCH-MAP [{Tenant}] map={MapId} \"{MapName}\" generated={Generated} skipped={Skipped}",
                    LogPrefix, tenantId, map.Id, map.Name, mapGeneratedCount, mapSkippedCount);
            }
        }

        sw.Stop();

        // Log overall summary
        if (generatedCount > 0 || failedCount > 0)
        {
            var zoomSummary = string.Join(" ", zoomStats
                .OrderBy(kv => kv.Key)
                .Select(kv => $"z{kv.Key}:{kv.Value.generated}/{kv.Value.generated + kv.Value.skipped}"));

            _logger.LogInformation(
                "{Prefix} BATCH-COMPLETE [{Tenant}] generated={Generated} skipped={Skipped} failed={Failed} time={Ms}ms [{ZoomStats}]",
                LogPrefix, tenantId, generatedCount, skippedCount, failedCount, sw.ElapsedMilliseconds, zoomSummary);
        }

        return generatedCount;
    }

    private async Task<byte[]?> GenerateLargeTileAsync(string tenantId, int mapId, int zoom, int x, int y)
    {
        try
        {
            if (zoom == 0)
            {
                return await GenerateZoom0LargeTileAsync(tenantId, mapId, x, y);
            }
            else
            {
                return await GenerateZoomNLargeTileAsync(tenantId, mapId, zoom, x, y);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} ERROR [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) - generation failed",
                LogPrefix, tenantId, mapId, zoom, x, y);
            return null;
        }
    }

    /// <summary>
    /// Generates zoom 0 tile using pre-loaded tile file dictionary (avoids N+1 queries in batch mode).
    /// </summary>
    private async Task<byte[]?> GenerateLargeTileWithPreloadAsync(
        string tenantId, int mapId, int zoom, int x, int y,
        Dictionary<(int CoordX, int CoordY), string> baseTileFiles)
    {
        if (zoom != 0)
        {
            return await GenerateLargeTileAsync(tenantId, mapId, zoom, x, y);
        }

        try
        {
            var baseMinX = x * TilesPerLargeTile;
            var baseMinY = y * TilesPerLargeTile;

            // Get tiles from pre-loaded dictionary
            var tiles = new List<(int LocalX, int LocalY, string File)>();
            for (int dx = 0; dx < TilesPerLargeTile; dx++)
            {
                for (int dy = 0; dy < TilesPerLargeTile; dy++)
                {
                    var key = (baseMinX + dx, baseMinY + dy);
                    if (baseTileFiles.TryGetValue(key, out var file))
                    {
                        tiles.Add((dx, dy, file));
                    }
                }
            }

            if (tiles.Count == 0)
            {
                return null;
            }

            // Create 400x400 transparent canvas
            using var img = new Image<Rgba32>(LargeTileSize, LargeTileSize);
            img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

            var hasAnyTile = false;

            foreach (var (localX, localY, file) in tiles)
            {
                var sourcePath = Path.Combine(_gridStorage, file);
                if (File.Exists(sourcePath))
                {
                    try
                    {
                        using var baseTileImg = await Image.LoadAsync<Rgba32>(sourcePath);
                        img.Mutate(ctx => ctx.DrawImage(baseTileImg, new Point(localX * BaseTileSize, localY * BaseTileSize), 1f));
                        hasAnyTile = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load base tile: {Path}", sourcePath);
                    }
                }
            }

            if (!hasAnyTile)
            {
                return null;
            }

            // Save to filesystem and return bytes
            var outputPath = GetLargeTilePath(tenantId, mapId, 0, x, y);
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var ms = new MemoryStream();
            await img.SaveAsWebpAsync(ms, WebpEncoder);
            var bytes = ms.ToArray();

            // Write to disk asynchronously
            await File.WriteAllBytesAsync(outputPath, bytes);

            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} ERROR [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) - generation failed",
                LogPrefix, tenantId, mapId, zoom, x, y);
            return null;
        }
    }

    /// <summary>
    /// Generates a zoom-0 large tile by combining 4x4 = 16 base 100x100 tiles into a 400x400 tile.
    /// </summary>
    private async Task<byte[]?> GenerateZoom0LargeTileAsync(string tenantId, int mapId, int x, int y)
    {
        // Get the base tile coordinates this large tile covers
        var baseMinX = x * TilesPerLargeTile;
        var baseMinY = y * TilesPerLargeTile;

        // Query base tiles from database with AsNoTracking for performance
        var baseTiles = await _dbContext.Tiles
            .AsNoTracking()
            .Where(t => t.MapId == mapId
                && t.Zoom == 0
                && t.CoordX >= baseMinX && t.CoordX < baseMinX + TilesPerLargeTile
                && t.CoordY >= baseMinY && t.CoordY < baseMinY + TilesPerLargeTile)
            .Select(t => new { t.CoordX, t.CoordY, t.File })
            .ToListAsync();

        if (baseTiles.Count == 0)
        {
            return null; // No source tiles
        }

        // Create 400x400 transparent canvas
        using var img = new Image<Rgba32>(LargeTileSize, LargeTileSize);
        img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        var hasAnyTile = false;

        // Load and place each base tile
        foreach (var tile in baseTiles)
        {
            var localX = tile.CoordX - baseMinX;
            var localY = tile.CoordY - baseMinY;

            var sourcePath = Path.Combine(_gridStorage, tile.File);
            if (File.Exists(sourcePath))
            {
                try
                {
                    using var baseTileImg = await Image.LoadAsync<Rgba32>(sourcePath);
                    img.Mutate(ctx => ctx.DrawImage(baseTileImg, new Point(localX * BaseTileSize, localY * BaseTileSize), 1f));
                    hasAnyTile = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load base tile: {Path}", sourcePath);
                }
            }
        }

        if (!hasAnyTile)
        {
            return null;
        }

        // Encode to bytes and save to filesystem
        var outputPath = GetLargeTilePath(tenantId, mapId, 0, x, y);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var ms = new MemoryStream();
        await img.SaveAsWebpAsync(ms, WebpEncoder);
        var bytes = ms.ToArray();

        await File.WriteAllBytesAsync(outputPath, bytes);

        _logger.LogDebug("Generated zoom-0 large tile: {Path}", outputPath);
        return bytes;
    }

    /// <summary>
    /// Generates a zoom 1-6 large tile by combining 2x2 = 4 child large tiles.
    /// Uses NearestNeighbor resampler for faster resize with crisp pixel-art appearance.
    /// NOTE: This method may call DbContext via GetOrGenerateLargeTileAsync - not safe for parallel execution.
    /// </summary>
    private async Task<byte[]?> GenerateZoomNLargeTileAsync(string tenantId, int mapId, int zoom, int x, int y)
    {
        // Create 400x400 transparent canvas
        using var img = new Image<Rgba32>(LargeTileSize, LargeTileSize);
        img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        var hasAnyChild = false;

        // Load and place each of the 4 child tiles
        for (int dx = 0; dx <= 1; dx++)
        {
            for (int dy = 0; dy <= 1; dy++)
            {
                var childX = x * 2 + dx;
                var childY = y * 2 + dy;

                // Get child tile bytes (from cache, disk, or generate)
                var childBytes = await GetOrGenerateLargeTileAsync(tenantId, mapId, zoom - 1, childX, childY);
                if (childBytes != null)
                {
                    try
                    {
                        using var childImg = Image.Load<Rgba32>(childBytes);
                        // Resize 400x400 child to 200x200 using NearestNeighbor for speed and crisp pixels
                        childImg.Mutate(ctx => ctx.Resize(new ResizeOptions
                        {
                            Size = new Size(LargeTileSize / 2, LargeTileSize / 2),
                            Sampler = KnownResamplers.NearestNeighbor
                        }));
                        // Place in appropriate quadrant
                        img.Mutate(ctx => ctx.DrawImage(childImg, new Point(dx * (LargeTileSize / 2), dy * (LargeTileSize / 2)), 1f));
                        hasAnyChild = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process child tile at z={Zoom} ({X},{Y})", zoom - 1, childX, childY);
                    }
                }
            }
        }

        if (!hasAnyChild)
        {
            return null;
        }

        // Encode to bytes and save to filesystem
        var outputPath = GetLargeTilePath(tenantId, mapId, zoom, x, y);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var ms = new MemoryStream();
        await img.SaveAsWebpAsync(ms, WebpEncoder);
        var bytes = ms.ToArray();

        await File.WriteAllBytesAsync(outputPath, bytes);

        _logger.LogDebug("Generated zoom-{Zoom} large tile: {Path}", zoom, outputPath);
        return bytes;
    }

    /// <summary>
    /// Generates a zoom 1-6 large tile by reading child tiles from filesystem only.
    /// Thread-safe for parallel batch execution - does NOT access DbContext.
    /// Used during batch generation where children are guaranteed to exist on disk.
    /// </summary>
    private async Task<byte[]?> GenerateZoomNFromFilesAsync(string tenantId, int mapId, int zoom, int x, int y)
    {
        // Create 400x400 transparent canvas
        using var img = new Image<Rgba32>(LargeTileSize, LargeTileSize);
        img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        var hasAnyChild = false;

        // Load and place each of the 4 child tiles from filesystem
        for (int dx = 0; dx <= 1; dx++)
        {
            for (int dy = 0; dy <= 1; dy++)
            {
                var childX = x * 2 + dx;
                var childY = y * 2 + dy;

                // Read child tile directly from filesystem (no DbContext access)
                var childPath = GetLargeTilePath(tenantId, mapId, zoom - 1, childX, childY);
                if (File.Exists(childPath))
                {
                    try
                    {
                        var childBytes = await File.ReadAllBytesAsync(childPath);
                        using var childImg = Image.Load<Rgba32>(childBytes);
                        // Resize 400x400 child to 200x200 using NearestNeighbor for speed and crisp pixels
                        childImg.Mutate(ctx => ctx.Resize(new ResizeOptions
                        {
                            Size = new Size(LargeTileSize / 2, LargeTileSize / 2),
                            Sampler = KnownResamplers.NearestNeighbor
                        }));
                        // Place in appropriate quadrant
                        img.Mutate(ctx => ctx.DrawImage(childImg, new Point(dx * (LargeTileSize / 2), dy * (LargeTileSize / 2)), 1f));
                        hasAnyChild = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load child tile from file: {Path}", childPath);
                    }
                }
            }
        }

        if (!hasAnyChild)
        {
            return null;
        }

        // Encode to bytes and save to filesystem
        var outputPath = GetLargeTilePath(tenantId, mapId, zoom, x, y);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var ms = new MemoryStream();
        await img.SaveAsWebpAsync(ms, WebpEncoder);
        var bytes = ms.ToArray();

        await File.WriteAllBytesAsync(outputPath, bytes);

        _logger.LogDebug("Generated zoom-{Zoom} large tile from files: {Path}", zoom, outputPath);
        return bytes;
    }
}
