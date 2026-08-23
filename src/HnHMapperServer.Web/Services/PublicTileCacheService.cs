using System.Collections.Concurrent;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Bounded in-memory cache for public map tiles.
/// <para>
/// Tiles are served from here to avoid disk I/O, with the tile endpoint falling back to
/// the filesystem on a miss (and re-populating the cache), so this is purely an
/// optimization — nothing breaks when a tile is not resident.
/// </para>
/// <para>
/// It used to hold every public tile on disk forever, which made the Web process's
/// baseline grow with the public-map library (~1.2 GB at the time this cap was added) and
/// left one unbounded allocation between a growing map set and the container's memory
/// limit. It is now capped by a byte budget with least-recently-used eviction: hot tiles
/// stay resident, cold ones fall back to disk, and the ceiling is a number you can set.
/// Tiles are served with <c>immutable</c>, year-long cache headers, so browsers and the
/// reverse proxy absorb most repeat traffic anyway.
/// </para>
/// <para>Budget: <c>PublicTileCache:BudgetMB</c> (default 256 MB, 0 disables caching).</para>
/// </summary>
public class PublicTileCacheService
{
    private sealed class Entry
    {
        public required byte[] Data { get; init; }
        public long LastAccessTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _tileCache = new(StringComparer.Ordinal);
    private readonly ILogger<PublicTileCacheService> _logger;
    private readonly string _gridStorage;
    private readonly long _budgetBytes;
    private readonly object _evictionGate = new();
    private long _bytes;
    private bool _isLoaded;
    private bool _budgetReported;

    public PublicTileCacheService(IConfiguration config, ILogger<PublicTileCacheService> logger)
    {
        _gridStorage = config["GridStorage"] ?? "map";
        _logger = logger;
        var budgetMb = config.GetValue<int?>("PublicTileCache:BudgetMB") ?? 256;
        _budgetBytes = Math.Max(0, (long)budgetMb) * 1024 * 1024;
    }

    /// <summary>
    /// Fills the cache from disk up to the budget. Tiles beyond it are left on disk and
    /// served (and cached, evicting something colder) on first request.
    /// </summary>
    public async Task LoadAllTilesAsync(CancellationToken cancellationToken = default)
    {
        var publicDir = Path.Combine(_gridStorage, "public");
        if (!Directory.Exists(publicDir))
        {
            _logger.LogInformation("Public maps directory does not exist: {Path}", publicDir);
            _isLoaded = true;
            return;
        }

        var slugDirs = Directory.GetDirectories(publicDir);
        var totalTiles = 0;
        var skippedTiles = 0;

        foreach (var slugDir in slugDirs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var slug = Path.GetFileName(slugDir);
            var tileFiles = Directory.GetFiles(slugDir, "*.png", SearchOption.AllDirectories);

            _logger.LogDebug("Loading {Count} tiles for public map: {Slug}", tileFiles.Length, slug);

            foreach (var file in tileFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Preloading deliberately stops at the budget instead of evicting: the
                // first tiles read are as good a guess as any before traffic arrives, and
                // thrashing the cache during startup would only slow boot.
                if (Volatile.Read(ref _bytes) >= _budgetBytes)
                {
                    skippedTiles++;
                    continue;
                }

                try
                {
                    var relativePath = Path.GetRelativePath(publicDir, file);
                    var cacheKey = relativePath.Replace('\\', '/');
                    var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                    if (Store(cacheKey, bytes, evictIfNeeded: false))
                    {
                        totalTiles++;
                    }
                    else
                    {
                        skippedTiles++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load tile: {File}", file);
                }
            }
        }

        _isLoaded = true;
        if (skippedTiles > 0)
        {
            _logger.LogInformation(
                "Public tile cache ready: {Count} tiles ({Size:F1} MB) resident, {Skipped} left on disk (budget {Budget} MB; they load on demand)",
                totalTiles, Volatile.Read(ref _bytes) / 1024.0 / 1024.0, skippedTiles, _budgetBytes / 1024 / 1024);
        }
        else
        {
            _logger.LogInformation("Loaded {Count} public map tiles ({Size:F1} MB) into memory",
                totalTiles, Volatile.Read(ref _bytes) / 1024.0 / 1024.0);
        }
    }

    /// <summary>
    /// Try to get a tile from the cache.
    /// </summary>
    public bool TryGetTile(string slug, string path, out byte[]? data)
    {
        var key = $"{slug}/{path}";
        if (_tileCache.TryGetValue(key, out var entry))
        {
            Volatile.Write(ref entry.LastAccessTicks, DateTime.UtcNow.Ticks);
            data = entry.Data;
            return true;
        }

        data = null;
        return false;
    }

    /// <summary>
    /// Add a tile to the cache (for tiles loaded after startup), evicting the least
    /// recently used tiles when the budget is reached.
    /// </summary>
    public void AddTile(string slug, string path, byte[] data)
    {
        var key = $"{slug}/{path}";
        Store(key, data, evictIfNeeded: true);
    }

    private bool Store(string key, byte[] data, bool evictIfNeeded)
    {
        if (_budgetBytes == 0 || data.LongLength > _budgetBytes)
        {
            return false;
        }

        if (Volatile.Read(ref _bytes) + data.LongLength > _budgetBytes)
        {
            if (!evictIfNeeded)
            {
                return false;
            }

            EvictTo(_budgetBytes - data.LongLength);
        }

        var entry = new Entry { Data = data, LastAccessTicks = DateTime.UtcNow.Ticks };
        if (_tileCache.TryGetValue(key, out var existing))
        {
            Interlocked.Add(ref _bytes, data.LongLength - existing.Data.LongLength);
        }
        else
        {
            Interlocked.Add(ref _bytes, data.LongLength);
        }

        _tileCache[key] = entry;
        return true;
    }

    /// <summary>Evicts least-recently-used tiles until the cache is at or below <paramref name="targetBytes"/>.</summary>
    private void EvictTo(long targetBytes)
    {
        lock (_evictionGate)
        {
            if (Volatile.Read(ref _bytes) <= targetBytes)
            {
                return; // another thread already made room
            }

            // Evict in one pass with headroom (down to ~90% of the target) so a busy map
            // doesn't re-sort the whole cache on every single tile request.
            var goal = (long)(targetBytes * 0.9);
            foreach (var (key, entry) in _tileCache.OrderBy(kv => Volatile.Read(ref kv.Value.LastAccessTicks)))
            {
                if (Volatile.Read(ref _bytes) <= goal)
                {
                    break;
                }

                if (_tileCache.TryRemove(key, out var removed))
                {
                    Interlocked.Add(ref _bytes, -removed.Data.LongLength);
                }
            }

            if (!_budgetReported)
            {
                _budgetReported = true;
                _logger.LogInformation(
                    "Public tile cache reached its {Budget} MB budget; least-recently-used tiles now fall back to disk",
                    _budgetBytes / 1024 / 1024);
            }
        }
    }

    /// <summary>
    /// Invalidate all tiles for a specific slug.
    /// Call this when a public map is regenerated.
    /// </summary>
    public void InvalidateSlug(string slug)
    {
        var prefix = $"{slug}/";
        var keysToRemove = _tileCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in keysToRemove)
        {
            if (_tileCache.TryRemove(key, out var removed))
            {
                Interlocked.Add(ref _bytes, -removed.Data.LongLength);
            }
        }
        _logger.LogInformation("Invalidated {Count} cached tiles for slug: {Slug}", keysToRemove.Count, slug);
    }

    /// <summary>
    /// Reload tiles for a specific slug.
    /// </summary>
    public async Task ReloadSlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        InvalidateSlug(slug);

        var slugDir = Path.Combine(_gridStorage, "public", slug);
        if (!Directory.Exists(slugDir))
        {
            _logger.LogWarning("Public map directory does not exist: {Path}", slugDir);
            return;
        }

        var tileFiles = Directory.GetFiles(slugDir, "*.png", SearchOption.AllDirectories);
        var publicDir = Path.Combine(_gridStorage, "public");
        var loadedCount = 0;

        foreach (var file in tileFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var relativePath = Path.GetRelativePath(publicDir, file);
                var cacheKey = relativePath.Replace('\\', '/');
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                if (Store(cacheKey, bytes, evictIfNeeded: false))
                {
                    loadedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load tile: {File}", file);
            }
        }

        _logger.LogInformation("Reloaded {Count} tiles for slug: {Slug}", loadedCount, slug);
    }

    public bool IsLoaded => _isLoaded;
    public int TileCount => _tileCache.Count;
    public long MemoryUsageBytes => Volatile.Read(ref _bytes);
}
