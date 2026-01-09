using System.Collections.Concurrent;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// In-memory cache for public map tiles.
/// Loads all tiles on startup for instant serving without disk I/O.
/// </summary>
public class PublicTileCacheService
{
    private readonly ConcurrentDictionary<string, byte[]> _tileCache = new();
    private readonly ILogger<PublicTileCacheService> _logger;
    private readonly string _gridStorage;
    private bool _isLoaded = false;

    public PublicTileCacheService(IConfiguration config, ILogger<PublicTileCacheService> logger)
    {
        _gridStorage = config["GridStorage"] ?? "map";
        _logger = logger;
    }

    /// <summary>
    /// Load all public map tiles into memory.
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
        long totalBytes = 0;

        foreach (var slugDir in slugDirs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var slug = Path.GetFileName(slugDir);
            var tileFiles = Directory.GetFiles(slugDir, "*.png", SearchOption.AllDirectories);

            _logger.LogDebug("Loading {Count} tiles for public map: {Slug}", tileFiles.Length, slug);

            foreach (var file in tileFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var relativePath = Path.GetRelativePath(publicDir, file);
                    var cacheKey = relativePath.Replace('\\', '/');
                    var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                    _tileCache[cacheKey] = bytes;
                    totalTiles++;
                    totalBytes += bytes.Length;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load tile: {File}", file);
                }
            }
        }

        _isLoaded = true;
        _logger.LogInformation("Loaded {Count} public map tiles ({Size:F1} MB) into memory",
            totalTiles, totalBytes / 1024.0 / 1024.0);
    }

    /// <summary>
    /// Try to get a tile from the cache.
    /// </summary>
    public bool TryGetTile(string slug, string path, out byte[]? data)
    {
        var key = $"{slug}/{path}";
        return _tileCache.TryGetValue(key, out data);
    }

    /// <summary>
    /// Add a tile to the cache (for tiles loaded after startup).
    /// </summary>
    public void AddTile(string slug, string path, byte[] data)
    {
        var key = $"{slug}/{path}";
        _tileCache[key] = data;
    }

    /// <summary>
    /// Invalidate all tiles for a specific slug.
    /// Call this when a public map is regenerated.
    /// </summary>
    public void InvalidateSlug(string slug)
    {
        var keysToRemove = _tileCache.Keys.Where(k => k.StartsWith($"{slug}/")).ToList();
        foreach (var key in keysToRemove)
        {
            _tileCache.TryRemove(key, out _);
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
                _tileCache[cacheKey] = bytes;
                loadedCount++;
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
    public long MemoryUsageBytes => _tileCache.Values.Sum(v => (long)v.Length);
}
