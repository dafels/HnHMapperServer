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

    // Top of the pyramid: the viewer requests 0..6 (leaflet-config.js HnHMaxZoom 7, zoomReverse),
    // and InvalidateCachedTile, MarkDirtyAsync and the batch loop stop here. Change them together.
    private const int MaxZoom = 6;

    private static readonly WebpEncoder WebpEncoder = new()
    {
        Quality = 85,
        Method = WebpEncodingMethod.Default
    };

    // LRU-style in-memory cache for tile bytes
    private static readonly ConcurrentDictionary<string, CacheEntry> _tileCache = new();
    private static long _cacheAccessCounter = 0;

    // Request deduplication: one in-progress generation per tile, shared by concurrent requests.
    // Lazy so that a request losing the registration race never starts a duplicate generation.
    private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _generationInProgress = new();

    // Negative cache: remember tiles that don't exist to avoid repeated DB queries
    private const int NegativeCacheTtlMinutes = 5;
    private const int MaxNegativeCacheEntries = 10000;
    private static readonly NegativeTileCache _negativeCache =
        new(MaxNegativeCacheEntries, TimeSpan.FromMinutes(NegativeCacheTtlMinutes));

    // Concurrency limiter: prevent DB overload from too many simultaneous generations
    private static readonly SemaphoreSlim _generationSemaphore = new(8, 8);

    // Track generation statistics for logging
    private static readonly ConcurrentDictionary<string, TenantStats> _tenantStats = new();

    // Last SOURCES-MISSING dark-cell count warned per "{tenant}/{map}" (see ReportMissingSources).
    private static readonly ConcurrentDictionary<string, int> _sourcesMissingWarned = new();

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
                    LogPrefix, tenantId, stats.MemoryCacheHits, stats.CacheHits, stats.NegativeCacheHits, stats.Coalesced, stats.OnTheFlyGenerated, stats.OnTheFlyFailed, stats.DirtyMarked, avgMs, _tileCache.Count, _negativeCache.Count);
            }
        }
    }

    public string GetLargeTilePath(string tenantId, int mapId, int zoom, int x, int y)
    {
        return Path.Combine(_gridStorage, "tenants", tenantId, "large", mapId.ToString(), zoom.ToString(), $"{x}_{y}.webp");
    }

    /// <summary>
    /// True when (zoom, x, y) can exist: zoom 0..MaxZoom and a footprint whose base-tile
    /// coordinates fit in int. Outside that the footprint maths wraps: <c>4 &lt;&lt; zoom</c> masks the
    /// shift to 5 bits (zoom -4 spans a whole quadrant, and the recursion only stops when the span
    /// wraps to 0), and <c>x * span</c> overflows onto a real footprint (z6 x=2^24 renders the data
    /// at x=0 and writes a copy of its pyramid). Children of an in-pyramid tile are always in the
    /// pyramid (their footprints are sub-ranges), so only the entry points check.
    /// </summary>
    private static bool IsInPyramid(int zoom, int x, int y)
    {
        if ((uint)zoom > MaxZoom)
        {
            return false;
        }

        long span = (long)TilesPerLargeTile << zoom;
        return x * span >= int.MinValue && (x + 1L) * span <= int.MaxValue
            && y * span >= int.MinValue && (y + 1L) * span <= int.MaxValue;
    }

    public async Task<byte[]?> GetOrGenerateLargeTileAsync(string tenantId, int mapId, int zoom, int x, int y)
    {
        // No such tile: 404 like any empty tile, not a generation failure (503).
        if (!IsInPyramid(zoom, x, y))
        {
            return null;
        }

        try
        {
            return await GetOrGenerateCoreAsync(tenantId, mapId, zoom, x, y);
        }
        catch (Exception ex)
        {
            // Logged once here, at the tile the caller asked for; the recursion below only throws.
            _logger.LogError(ex, "{Prefix} ERROR [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) - generation failed",
                LogPrefix, tenantId, mapId, zoom, x, y);
            throw;
        }
    }

    /// <summary>
    /// Cache → negative cache → disk → coalesced generation. Throws when generation fails, so
    /// a failure is never confused with "no source tiles": a failed tile is not negative-cached,
    /// and a parent built from it fails too instead of being written to disk with a hole.
    /// A file on disk that is not a complete WebP (see <see cref="WebpTileFile"/>) is discarded
    /// and regenerated like a missing one, so empty bytes are never served or cached.
    /// </summary>
    private async Task<byte[]?> GetOrGenerateCoreAsync(string tenantId, int mapId, int zoom, int x, int y)
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
        if (_negativeCache.Contains(cacheKey))
        {
            Interlocked.Increment(ref stats.NegativeCacheHits);
            return null;
        }

        var path = GetLargeTilePath(tenantId, mapId, zoom, x, y);

        // Check filesystem cache. File.Exists keeps misses exception-free; the read itself
        // tolerates a file deleted in between (REGEN-EMPTY or a rebuild in the other process).
        if (File.Exists(path) && await WebpTileFile.TryReadAsync(path) is { } bytes)
        {
            if (WebpTileFile.IsComplete(bytes))
            {
                Interlocked.Increment(ref stats.CacheHits);
                AddToCache(cacheKey, bytes);
                return bytes;
            }

            // Empty/truncated residue of an interrupted write: regenerate it below.
            DiscardCorruptTile(tenantId, mapId, zoom, x, y, bytes.Length);
        }

        // Coalesce concurrent requests for the same tile. Registering before starting means a
        // request that loses the race awaits the winner instead of running a duplicate.
        var generation = new Lazy<Task<byte[]?>>(
            () => GenerateLargeTileInternalAsync(tenantId, mapId, zoom, x, y, cacheKey, stats));
        var registered = _generationInProgress.GetOrAdd(cacheKey, generation);
        if (!ReferenceEquals(registered, generation))
        {
            Interlocked.Increment(ref stats.Coalesced);
            return await registered.Value;
        }

        try
        {
            return await generation.Value;
        }
        finally
        {
            _generationInProgress.TryRemove(KeyValuePair.Create(cacheKey, generation));
        }
    }

    /// <summary>
    /// Deletes a pyramid file that is not a usable WebP so it gets regenerated, and drops its
    /// memory-cache entry. Only this tile: ancestors composed without it are left to the drift
    /// scan's derivation rule and the audited rebuild, never deleted from a hot path.
    /// </summary>
    private void DiscardCorruptTile(string tenantId, int mapId, int zoom, int x, int y, long observedLength)
    {
        _tileCache.TryRemove($"{tenantId}/{mapId}/{zoom}/{x}_{y}", out _);

        var path = GetLargeTilePath(tenantId, mapId, zoom, x, y);
        try
        {
            // Gone already, or replaced by a concurrent atomic write: nothing to discard.
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != observedLength)
            {
                return;
            }

            File.Delete(path);
            _logger.LogWarning(
                "{Prefix} CORRUPT [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) {Length} bytes - not a complete WebP, deleted for regeneration",
                LogPrefix, tenantId, mapId, zoom, x, y, observedLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{Prefix} Failed to delete corrupt tile file {Path}", LogPrefix, path);
        }
    }

    /// <summary>
    /// Decodes a child tile for compositing. A child that is not a complete, decodable WebP is
    /// discarded and fails its parent (InvalidDataException) — composing around it would save
    /// the parent with a hole, or make a force-regeneration delete a valid parent as "empty".
    /// </summary>
    private Image<Rgba32> LoadChild(string tenantId, int mapId, int zoom, int x, int y, byte[] bytes)
    {
        if (!WebpTileFile.IsComplete(bytes))
        {
            DiscardCorruptTile(tenantId, mapId, zoom, x, y, bytes.Length);
            throw new InvalidDataException($"Corrupt child tile z={zoom} ({x},{y}): {bytes.Length} bytes");
        }

        try
        {
            return Image.Load<Rgba32>(bytes);
        }
        catch (ImageFormatException ex)
        {
            DiscardCorruptTile(tenantId, mapId, zoom, x, y, bytes.Length);
            throw new InvalidDataException($"Undecodable child tile z={zoom} ({x},{y})", ex);
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
            var sw = Stopwatch.StartNew();
            var generatedBytes = await GenerateLargeTileAsync(tenantId, mapId, zoom, x, y);
            sw.Stop();

            if (generatedBytes != null)
            {
                Interlocked.Increment(ref stats.OnTheFlyGenerated);
                Interlocked.Add(ref stats.TotalGenerationTimeMs, sw.ElapsedMilliseconds);

                _logger.LogDebug(
                    "{Prefix} GENERATED [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) in {Ms}ms ({Size:F1}KB)",
                    LogPrefix, tenantId, mapId, zoom, x, y, sw.ElapsedMilliseconds, generatedBytes.Length / 1024.0);

                AddToCache(cacheKey, generatedBytes);
                return generatedBytes;
            }
            else
            {
                Interlocked.Increment(ref stats.OnTheFlyFailed);

                // Add to negative cache to avoid repeated checks. Only reached for a genuine
                // "no source tiles": a generation error throws past this instead.
                _negativeCache.Add(cacheKey);

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
        // Sort a ToArray() snapshot, never the live dictionary: LINQ over a ConcurrentDictionary
        // sizes its buffer from Count and then calls CopyTo, and a concurrent add or remove in
        // between throws or yields empty entries (see NegativeTileCache).
        var oldest = _tileCache.ToArray()
            .OrderBy(kv => kv.Value.LastAccess)
            .Take(count)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldest)
        {
            _tileCache.TryRemove(key, out _);
        }
    }

    private void InvalidateCacheEntry(string tenantId, int mapId, int zoom, int x, int y)
    {
        var cacheKey = $"{tenantId}/{mapId}/{zoom}/{x}_{y}";
        _tileCache.TryRemove(cacheKey, out _);
        // Also clear from negative cache - tile might exist now after upload
        _negativeCache.Remove(cacheKey);
    }

    public void InvalidateTenantCache(string tenantId)
    {
        RemoveCacheEntriesByPrefix($"{tenantId}/");
    }

    public void InvalidateMapCache(string tenantId, int mapId)
    {
        RemoveCacheEntriesByPrefix($"{tenantId}/{mapId}/");
    }

    private static void RemoveCacheEntriesByPrefix(string prefix)
    {
        foreach (var key in _tileCache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _tileCache.TryRemove(key, out _);
            }
        }

        _negativeCache.RemoveByPrefix(prefix);
    }

    public (int FilesDeleted, long BytesFreed) DeleteMapWebpTiles(string tenantId, int mapId)
    {
        var mapDir = Path.Combine(_gridStorage, "tenants", tenantId, "large", mapId.ToString());
        var filesDeleted = 0;
        long bytesFreed = 0;

        if (Directory.Exists(mapDir))
        {
            foreach (var file in Directory.EnumerateFiles(mapDir, "*.webp", SearchOption.AllDirectories))
            {
                try
                {
                    bytesFreed += new FileInfo(file).Length;
                    filesDeleted++;
                }
                catch
                {
                    // Size bookkeeping only — deletion below is what matters.
                }
            }

            try
            {
                Directory.Delete(mapDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Prefix} Failed to delete webp tile directory {Dir}", LogPrefix, mapDir);
            }
        }

        InvalidateMapCache(tenantId, mapId);

        _logger.LogInformation(
            "{Prefix} DELETED map pyramid [{Tenant}] map={MapId}: {Files} files ({MB:F1} MB)",
            LogPrefix, tenantId, mapId, filesDeleted, bytesFreed / 1024.0 / 1024.0);

        return (filesDeleted, bytesFreed);
    }

    /// <summary>
    /// Invalidates in-memory cache entries for a specific tile and its parent zoom chain.
    /// Used for cross-process cache invalidation (API notifying Web after generating new tiles on disk).
    /// </summary>
    public void InvalidateCachedTile(string tenantId, int mapId, int baseX, int baseY)
    {
        var largeTileX = (int)Math.Floor(baseX / (double)TilesPerLargeTile);
        var largeTileY = (int)Math.Floor(baseY / (double)TilesPerLargeTile);

        for (int zoom = 0; zoom <= MaxZoom; zoom++)
        {
            InvalidateCacheEntry(tenantId, mapId, zoom, largeTileX, largeTileY);
            largeTileX = (int)Math.Floor(largeTileX / 2.0);
            largeTileY = (int)Math.Floor(largeTileY / 2.0);
        }
    }

    public async Task MarkDirtyAsync(string tenantId, int mapId, int baseX, int baseY)
    {
        var stats = GetStats(tenantId);
        stats.LastActivity = DateTime.UtcNow;

        // Calculate which large tile this base tile belongs to
        var largeTileX = (int)Math.Floor(baseX / (double)TilesPerLargeTile);
        var largeTileY = (int)Math.Floor(baseY / (double)TilesPerLargeTile);

        var invalidatedCount = 0;

        // Invalidate in-memory caches only — do NOT delete files from disk.
        // Old WebP files remain on disk and continue serving requests until
        // ZoomTileProcessorService overwrites them with fresh tiles.
        // This avoids the gap where no file exists and on-the-fly generation kicks in.
        for (int zoom = 0; zoom <= MaxZoom; zoom++)
        {
            var cacheKey = $"{tenantId}/{mapId}/{zoom}/{largeTileX}_{largeTileY}";
            // IMPORTANT: must clear BOTH caches unconditionally. A previous `||` short-circuit
            // here meant that if the positive cache had a stale entry, the negative cache
            // entry for the same key was never evicted — so the next request returned a
            // negative-cache hit and 404'd, even though we'd just received an upload that
            // populated source tiles for that coord.
            var removedPositive = _tileCache.TryRemove(cacheKey, out _);
            var removedNegative = _negativeCache.Remove(cacheKey);
            if (removedPositive || removedNegative)
            {
                invalidatedCount++;
            }

            // Calculate parent coordinate for next zoom level
            largeTileX = (int)Math.Floor(largeTileX / 2.0);
            largeTileY = (int)Math.Floor(largeTileY / 2.0);
        }

        Interlocked.Add(ref stats.DirtyMarked, invalidatedCount);

        _logger.LogDebug(
            "{Prefix} DIRTY [{Tenant}] map={MapId} base=({BaseX},{BaseY}) -> invalidated {Count} in-memory cache entries",
            LogPrefix, tenantId, mapId, baseX, baseY, invalidatedCount);
    }

    public async Task<byte[]?> ForceRegenerateLargeTileAsync(string tenantId, int mapId, int zoom, int x, int y)
    {
        // Falling through would take the "no sources" branch below: negative-cache the key and
        // delete whatever file sits at that path.
        if (!IsInPyramid(zoom, x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), $"z={zoom} ({x},{y}) is outside the tile pyramid");
        }

        var cacheKey = $"{tenantId}/{mapId}/{zoom}/{x}_{y}";
        var stats = GetStats(tenantId);
        stats.LastActivity = DateTime.UtcNow;

        // Generation errors are tracked separately from "no sources": only the latter may delete
        // the existing file below. Deleting on a transient error (DB lock, IO hiccup) would throw
        // away a perfectly good tile.
        byte[]? generatedBytes;
        var generationFailed = false;
        var sw = Stopwatch.StartNew();
        try
        {
            generatedBytes = zoom == 0
                ? await GenerateZoom0LargeTileAsync(tenantId, mapId, x, y)
                : await GenerateZoomNLargeTileAsync(tenantId, mapId, zoom, x, y);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} ERROR [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) - regeneration failed",
                LogPrefix, tenantId, mapId, zoom, x, y);
            generatedBytes = null;
            generationFailed = true;
        }
        sw.Stop();

        if (generatedBytes != null)
        {
            AddToCache(cacheKey, generatedBytes);

            _logger.LogDebug(
                "{Prefix} REGEN [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) in {Ms}ms ({Size:F1}KB)",
                LogPrefix, tenantId, mapId, zoom, x, y, sw.ElapsedMilliseconds, generatedBytes.Length / 1024.0);
        }
        else
        {
            // Clear from caches — source tiles may have been removed
            _tileCache.TryRemove(cacheKey, out _);

            // A failed regeneration must not mark the tile missing: the negative cache is
            // checked before the disk, so it would hide the existing file for five minutes.
            if (!generationFailed)
            {
                _negativeCache.Add(cacheKey);

                // Source tiles are genuinely gone: a stale file left on disk would keep serving
                // ghost imagery forever (GetOrGenerateLargeTileAsync serves any complete existing
                // file as-is). Deleting it makes the emptiness propagate — parent regeneration
                // recomposites without this tile.
                var path = GetLargeTilePath(tenantId, mapId, zoom, x, y);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        _logger.LogInformation(
                            "{Prefix} REGEN-EMPTY [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) - no source tiles, deleted stale file",
                            LogPrefix, tenantId, mapId, zoom, x, y);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Prefix} Failed to delete stale tile file {Path}", LogPrefix, path);
                }
            }
        }

        return generatedBytes;
    }

    public Task<int> GenerateMissingTilesAsync(string tenantId, CancellationToken ct = default, ISet<int>? writtenMapIds = null)
    {
        return GenerateMissingTilesForMapsAsync(tenantId, mapIds: null, ct, writtenMapIds);
    }

    public async Task<int> GenerateMissingTilesForMapsAsync(
        string tenantId, HashSet<int>? mapIds, CancellationToken ct = default, ISet<int>? writtenMapIds = null)
    {
        var sw = Stopwatch.StartNew();
        var generatedCount = 0;
        var skippedCount = 0;
        var emptyCount = 0;          // nothing renderable (no source file on disk): no tile to draw
        var failedCount = 0;         // generation errors, plus parents left out because a child failed
        var missingSourcesCount = 0; // dark zoom-0 cells: tile rows whose source files are all gone
        var zoomStats = new ConcurrentDictionary<int, (int generated, int skipped)>();

        // Get maps for this tenant (filtered by mapIds if provided)
        var query = _dbContext.Maps
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId);

        if (mapIds != null)
        {
            query = query.Where(m => mapIds.Contains(m.Id));
        }

        var maps = await query
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct);

        if (maps.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation(
            "{Prefix} BATCH-START [{Tenant}] scanning {MapCount} maps for missing tiles (parallelism={Parallelism}){Filter}",
            LogPrefix, tenantId, maps.Count, MaxParallelism,
            mapIds != null ? $" [filtered to {mapIds.Count} dirty maps]" : "");

        foreach (var map in maps)
        {
            if (ct.IsCancellationRequested) break;

            var mapGeneratedCount = 0;
            var mapSkippedCount = 0;
            var mapDarkCells = 0;
            var mapMissingRows = 0;

            // Pre-load all base tiles for this map in a single query. Wipe tombstones (File "")
            // have nothing to render, so they make no cell.
            var baseTileFiles = await _dbContext.Tiles
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && t.MapId == map.Id && t.Zoom == 0 && t.File != "")
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

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelism,
                CancellationToken = ct
            };

            // One tile per call: an error is logged with its coordinates and recorded in this
            // level's failed set instead of aborting the rest of the tenant; a tile written to disk
            // is recorded in this level's generated set (its parent must be refreshed) and evicted
            // from this process's caches. The batch composes from files, but ZoomQ's
            // ForceRegenerate composes children through the cache: a stale cached sibling would be
            // baked straight back into z2-z6 with a fresh mtime, hiding it from every freshness check.
            async Task GenerateCellAsync(
                int zoom, (int X, int Y) coord,
                ConcurrentDictionary<(int X, int Y), byte> failedHere,
                ConcurrentDictionary<(int X, int Y), byte> generatedHere,
                Func<Task<(byte[]? Bytes, int SourcesMissing)>> generate)
            {
                try
                {
                    var (bytes, sourcesMissing) = await generate();
                    if (bytes != null)
                    {
                        InvalidateCacheEntry(tenantId, map.Id, zoom, coord.X, coord.Y);
                        generatedHere.TryAdd(coord, 0);
                        Interlocked.Increment(ref mapGeneratedCount);
                        zoomStats.AddOrUpdate(zoom, (1, 0), (_, v) => (v.generated + 1, v.skipped));
                    }
                    else
                    {
                        Interlocked.Increment(ref emptyCount);
                        if (sourcesMissing > 0)
                        {
                            // Dark cell: tile rows, but none of their source files on disk.
                            Interlocked.Increment(ref mapDarkCells);
                            Interlocked.Add(ref mapMissingRows, sourcesMissing);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedHere.TryAdd(coord, 0);
                    Interlocked.Increment(ref failedCount);
                    _logger.LogError(ex, "{Prefix} ERROR [{Tenant}] map={MapId} z={Zoom} ({X},{Y}) - generation failed",
                        LogPrefix, tenantId, map.Id, zoom, coord.X, coord.Y);
                }
            }

            // Generate zoom 0 tiles in parallel
            var failedBelow = new ConcurrentDictionary<(int X, int Y), byte>();
            var generatedBelow = new ConcurrentDictionary<(int X, int Y), byte>();
            await Parallel.ForEachAsync(largeTileCoords, parallelOptions, async (coord, token) =>
            {
                if (HasUsableTileFile(tenantId, map.Id, 0, coord.X, coord.Y))
                {
                    Interlocked.Increment(ref mapSkippedCount);
                    zoomStats.AddOrUpdate(0, (0, 1), (_, v) => (v.generated, v.skipped + 1));
                    return;
                }

                await GenerateCellAsync(0, coord, failedBelow, generatedBelow,
                    () => GenerateLargeTileWithPreloadAsync(tenantId, map.Id, coord.X, coord.Y, baseTileFiles));
            });

            // Generate zoom levels 1-6 (bottom-up, ensuring children exist first)
            // Use file-based generation (no DbContext) since children were just generated above
            for (int zoom = 1; zoom <= MaxZoom && !ct.IsCancellationRequested; zoom++)
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
                var childFailed = failedBelow;
                var childGenerated = generatedBelow;
                var failedHere = new ConcurrentDictionary<(int X, int Y), byte>();
                var generatedHere = new ConcurrentDictionary<(int X, int Y), byte>();
                await Parallel.ForEachAsync(largeTileCoords, parallelOptions, async (coord, token) =>
                {
                    // An existing parent of a child written in this pass predates that child, so it
                    // was composed without it (the gap-fill used to skip it, leaving exactly the
                    // "older than child" drift). Refresh it; every other existing file is kept.
                    if (!HasChildIn(childGenerated, coord)
                        && HasUsableTileFile(tenantId, map.Id, currentZoom, coord.X, coord.Y))
                    {
                        Interlocked.Increment(ref mapSkippedCount);
                        zoomStats.AddOrUpdate(currentZoom, (0, 1), (_, v) => (v.generated, v.skipped + 1));
                        return;
                    }

                    // The file-based compose reads a missing child as empty space, so a parent
                    // over a failed child would be saved with a hole. Leave it missing (failed
                    // too); the next scan retries the whole chain.
                    if (HasChildIn(childFailed, coord))
                    {
                        failedHere.TryAdd(coord, 0);
                        Interlocked.Increment(ref failedCount);
                        return;
                    }

                    // File-based generation that doesn't touch DbContext (overwrites atomically)
                    await GenerateCellAsync(currentZoom, coord, failedHere, generatedHere,
                        async () => (await GenerateZoomNFromFilesAsync(tenantId, map.Id, currentZoom, coord.X, coord.Y), 0));
                });

                failedBelow = failedHere;
                generatedBelow = generatedHere;
            }

            Interlocked.Add(ref generatedCount, mapGeneratedCount);
            Interlocked.Add(ref skippedCount, mapSkippedCount);
            missingSourcesCount += mapDarkCells;
            if (mapGeneratedCount > 0)
            {
                writtenMapIds?.Add(map.Id);
            }

            ReportMissingSources(tenantId, map.Id, map.Name, mapDarkCells, mapMissingRows);

            // Log per-map summary if any work was done
            if (mapGeneratedCount > 0)
            {
                _logger.LogInformation(
                    "{Prefix} BATCH-MAP [{Tenant}] map={MapId} \"{MapName}\" generated={Generated} skipped={Skipped}",
                    LogPrefix, tenantId, map.Id, map.Name, mapGeneratedCount, mapSkippedCount);
            }
        }

        sw.Stop();

        // Log overall summary. empty = cells with nothing renderable (dark cells and their
        // parents); failed = generation errors (each also an ERROR line with its coordinates)
        // plus the parents left missing above them.
        if (generatedCount > 0 || emptyCount > 0 || failedCount > 0)
        {
            var zoomSummary = string.Join(" ", zoomStats
                .OrderBy(kv => kv.Key)
                .Select(kv => $"z{kv.Key}:{kv.Value.generated}/{kv.Value.generated + kv.Value.skipped}"));

            _logger.LogInformation(
                "{Prefix} BATCH-COMPLETE [{Tenant}] generated={Generated} skipped={Skipped} empty={Empty} failed={Failed} missingSources={MissingSources} time={Ms}ms [{ZoomStats}]",
                LogPrefix, tenantId, generatedCount, skippedCount, emptyCount, failedCount, missingSourcesCount,
                sw.ElapsedMilliseconds, zoomSummary);
        }

        return generatedCount;
    }

    /// <summary>
    /// Stat-only existence check for the batch (as cheap as File.Exists). A file shorter than a
    /// WebP header — zero-length residue of an interrupted write — counts as missing and is
    /// discarded, so this same bottom-up pass regenerates it and then its missing ancestors.
    /// A longer truncated file passes here; the parent that reads it (LoadChild) discards it.
    /// </summary>
    private bool HasUsableTileFile(string tenantId, int mapId, int zoom, int x, int y)
    {
        var info = new FileInfo(GetLargeTilePath(tenantId, mapId, zoom, x, y));
        if (!info.Exists)
        {
            return false;
        }

        if (info.Length >= WebpTileFile.HeaderLength)
        {
            return true;
        }

        DiscardCorruptTile(tenantId, mapId, zoom, x, y, info.Length);
        return false;
    }

    /// <summary>True when any of the parent's four children is in the given per-level set.</summary>
    private static bool HasChildIn(ConcurrentDictionary<(int X, int Y), byte> children, (int X, int Y) parent)
    {
        if (children.IsEmpty)
        {
            return false;
        }

        for (int dx = 0; dx <= 1; dx++)
        {
            for (int dy = 0; dy <= 1; dy++)
            {
                if (children.ContainsKey((parent.X * 2 + dx, parent.Y * 2 + dy)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// One SOURCES-MISSING warning per map whenever its number of dark zoom-0 cells changes (not
    /// on every 5-minute scan). A dark cell has tile rows but none of their source PNGs on disk —
    /// deleted by a merge or lost on disk — so nothing can draw it: it stays empty at every zoom
    /// until the files are restored to the paths the rows name, or a player re-walks the area.
    /// </summary>
    private void ReportMissingSources(string tenantId, int mapId, string mapName, int darkCells, int missingRows)
    {
        var key = $"{tenantId}/{mapId}";
        if (darkCells == 0)
        {
            _sourcesMissingWarned.TryRemove(key, out _);
            return;
        }

        if (_sourcesMissingWarned.TryGetValue(key, out var previous) && previous == darkCells)
        {
            return;
        }

        _sourcesMissingWarned[key] = darkCells;
        _logger.LogWarning(
            "{Prefix} SOURCES-MISSING [{Tenant}] map={MapId} \"{MapName}\": {Cells} zoom-0 cell(s) have tile rows but none of their {Rows} source file(s) exist on disk; these areas stay dark at every zoom until the files are restored. See SuperAdmin -> Map Integrity -> Missing tile sources.",
            LogPrefix, tenantId, mapId, mapName, darkCells, missingRows);
    }

    /// <summary>
    /// Returns null only when the tile has no source tiles. Errors propagate: catching them here
    /// used to turn every failure into "empty", so the failed tile was negative-cached and its
    /// parents were composed without it and written to disk.
    /// </summary>
    private async Task<byte[]?> GenerateLargeTileAsync(string tenantId, int mapId, int zoom, int x, int y)
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

    /// <summary>
    /// Generates a zoom-0 tile from the pre-loaded tile file dictionary (avoids N+1 queries in
    /// batch mode). Bytes are null only when the cell has nothing renderable; SourcesMissing
    /// counts its rows whose source file does not exist (null bytes + SourcesMissing > 0 = a dark
    /// cell). Errors propagate, so the batch records the cell as failed instead of reading it as
    /// empty and composing its parent around it.
    /// </summary>
    private async Task<(byte[]? Bytes, int SourcesMissing)> GenerateLargeTileWithPreloadAsync(
        string tenantId, int mapId, int x, int y,
        Dictionary<(int CoordX, int CoordY), string> baseTileFiles)
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
            return (null, 0);
        }

        // Create 400x400 transparent canvas
        using var img = new Image<Rgba32>(LargeTileSize, LargeTileSize);
        img.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        var hasAnyTile = false;
        var sourcesMissing = 0;

        foreach (var (localX, localY, file) in tiles)
        {
            var sourcePath = Path.Combine(_gridStorage, file);
            if (!File.Exists(sourcePath))
            {
                sourcesMissing++;
                continue;
            }

            // A source PNG cannot be regenerated, so an undecodable one is skipped (logged) and
            // the cell drawn without it: the degraded result beats no tile.
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

        if (!hasAnyTile)
        {
            return (null, sourcesMissing);
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

        await AtomicFile.WriteAllBytesAsync(outputPath, bytes);

        return (bytes, sourcesMissing);
    }

    /// <summary>
    /// Generates a zoom-0 large tile by combining 4x4 = 16 base 100x100 tiles into a 400x400 tile.
    /// </summary>
    private async Task<byte[]?> GenerateZoom0LargeTileAsync(string tenantId, int mapId, int x, int y)
    {
        // Get the base tile coordinates this large tile covers
        var baseMinX = x * TilesPerLargeTile;
        var baseMinY = y * TilesPerLargeTile;

        // Query base tiles from database with AsNoTracking for performance (wipe tombstones,
        // File "", have nothing to draw)
        var baseTiles = await _dbContext.Tiles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId
                && t.MapId == mapId
                && t.Zoom == 0
                && t.CoordX >= baseMinX && t.CoordX < baseMinX + TilesPerLargeTile
                && t.CoordY >= baseMinY && t.CoordY < baseMinY + TilesPerLargeTile
                && t.File != "")
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

        await AtomicFile.WriteAllBytesAsync(outputPath, bytes);

        _logger.LogDebug("Generated zoom-0 large tile: {Path}", outputPath);
        return bytes;
    }

    /// <summary>
    /// Generates a zoom 1-6 large tile by combining 2x2 = 4 child large tiles.
    /// Uses a Box resampler (2x2 average) so downscaled zooms do not alias.
    /// NOTE: This method uses DbContext (footprint check, and children via GetOrGenerateCoreAsync) - not safe for parallel execution.
    /// </summary>
    private async Task<byte[]?> GenerateZoomNLargeTileAsync(string tenantId, int mapId, int zoom, int x, int y)
    {
        // Only a footprint holding at least one zoom-0 source tile can render anything. Without
        // this check an unexplored zoom-6 tile recursed through all 5,461 descendants: 4,096
        // zoom-0 queries per request, and one negative-cache entry each, which kept that cache
        // full and evicting constantly. One indexed query answers it instead (~2 ms worst case).
        // Wipe tombstones (File "") are not sources. The entry points' pyramid-bounds guard keeps
        // span and the footprint inside int.
        var span = TilesPerLargeTile << zoom; // base tiles per side under this tile
        var baseMinX = x * span;
        var baseMinY = y * span;
        var hasSourceTiles = await _dbContext.Tiles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(t => t.TenantId == tenantId
                && t.MapId == mapId
                && t.Zoom == 0
                && t.CoordX >= baseMinX && t.CoordX < baseMinX + span
                && t.CoordY >= baseMinY && t.CoordY < baseMinY + span
                && t.File != "");
        if (!hasSourceTiles)
        {
            return null;
        }

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

                // Get child tile bytes (from cache, disk, or generate). A child that fails —
                // including one that cannot be decoded (LoadChild discards it) — throws and fails
                // this tile too, rather than leaving a hole that gets saved to disk, or reading
                // as "no sources" so that ForceRegenerate deletes a valid parent.
                var childBytes = await GetOrGenerateCoreAsync(tenantId, mapId, zoom - 1, childX, childY);
                if (childBytes != null)
                {
                    using var childImg = LoadChild(tenantId, mapId, zoom - 1, childX, childY, childBytes);
                    // Resize 400x400 child to 200x200 with a Box (2x2 average) filter to avoid aliasing
                    childImg.Mutate(ctx => ctx.Resize(new ResizeOptions
                    {
                        Size = new Size(LargeTileSize / 2, LargeTileSize / 2),
                        Sampler = KnownResamplers.Box
                    }));
                    // Place in appropriate quadrant
                    img.Mutate(ctx => ctx.DrawImage(childImg, new Point(dx * (LargeTileSize / 2), dy * (LargeTileSize / 2)), 1f));
                    hasAnyChild = true;
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

        await AtomicFile.WriteAllBytesAsync(outputPath, bytes);

        _logger.LogDebug("Generated zoom-{Zoom} large tile: {Path}", zoom, outputPath);
        return bytes;
    }

    /// <summary>
    /// Generates a zoom 1-6 large tile by reading child tiles from filesystem only.
    /// Thread-safe for parallel batch execution - does NOT access DbContext.
    /// Used during batch generation, bottom-up: a missing child file means empty space (no data
    /// there), so the batch must not call this for a parent whose child FAILED (its failed set).
    /// A corrupt or undecodable child is discarded and throws — never composited as a hole.
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
                if (!File.Exists(childPath))
                {
                    continue; // no data there: an empty quadrant
                }

                if (await WebpTileFile.TryReadAsync(childPath) is not { } childBytes)
                {
                    continue; // deleted concurrently (REGEN-EMPTY in the other process)
                }

                using var childImg = LoadChild(tenantId, mapId, zoom - 1, childX, childY, childBytes);
                // Resize 400x400 child to 200x200 with a Box (2x2 average) filter to avoid aliasing
                childImg.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(LargeTileSize / 2, LargeTileSize / 2),
                    Sampler = KnownResamplers.Box
                }));
                // Place in appropriate quadrant
                img.Mutate(ctx => ctx.DrawImage(childImg, new Point(dx * (LargeTileSize / 2), dy * (LargeTileSize / 2)), 1f));
                hasAnyChild = true;
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

        await AtomicFile.WriteAllBytesAsync(outputPath, bytes);

        _logger.LogDebug("Generated zoom-{Zoom} large tile from files: {Path}", zoom, outputPath);
        return bytes;
    }
}
