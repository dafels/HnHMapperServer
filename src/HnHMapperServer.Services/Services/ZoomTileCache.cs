using HnHMapperServer.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Cached tile with reference counting for memory management.
/// When RefCount reaches 0, the image can be disposed.
/// </summary>
internal sealed class CachedTile
{
    public Image<Rgba32> Image { get; }
    public int RefCount { get; set; }

    public CachedTile(Image<Rgba32> image, int refCount)
    {
        Image = image;
        RefCount = refCount;
    }
}

/// <summary>
/// Pending disk write (path + PNG data).
/// </summary>
internal readonly record struct PendingWrite(string Path, byte[] Data);

/// <summary>
/// In-memory cache for zoom tile generation during .hmap imports.
/// Implements cascading cache pattern where tiles from zoom level N
/// are kept in memory for generating zoom level N+1.
///
/// Features:
/// - Reference counting for automatic memory management
/// - Batched disk writes for I/O efficiency
/// - Collects tile metadata for batched database writes
/// - Thread-safe: producer threads read, consumer thread writes
/// </summary>
internal sealed class ZoomTileCache : IDisposable
{
    // Tile cache: (zoom, coord) -> CachedTile
    private readonly Dictionary<(int zoom, Coord coord), CachedTile> _cache = new();

    // Lock for thread-safe access to _cache
    private readonly System.Threading.Lock _cacheLock = new();

    // Pending disk writes
    private readonly List<PendingWrite> _pendingWrites = new();

    // Tile metadata for batch DB write
    private readonly List<TileData> _pendingTileData = new();

    // Total storage size for quota update
    private double _totalStorageMB;

    private bool _disposed;

    /// <summary>
    /// Adds a tile to the cache with a reference count.
    /// RefCount should be 1 for tiles used by next zoom level, 0 for final zoom.
    /// Thread-safe.
    /// </summary>
    public void AddTile(int zoom, Coord coord, Image<Rgba32> image, int refCount)
    {
        ThrowIfDisposed();

        var key = (zoom, coord);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                // Replace existing tile (shouldn't happen in normal flow)
                existing.Image.Dispose();
            }

            _cache[key] = new CachedTile(image, refCount);
        }
    }

    /// <summary>
    /// Gets a tile from the cache. Returns null if not found.
    /// Does NOT decrement reference count - call DecrementRef separately.
    /// Thread-safe.
    /// </summary>
    public Image<Rgba32>? GetTile(int zoom, Coord coord)
    {
        ThrowIfDisposed();

        var key = (zoom, coord);
        lock (_cacheLock)
        {
            return _cache.TryGetValue(key, out var cached) ? cached.Image : null;
        }
    }

    /// <summary>
    /// Decrements the reference count for a tile.
    /// When count reaches 0, the image is disposed and removed from cache.
    /// Thread-safe.
    /// </summary>
    public void DecrementRef(int zoom, Coord coord)
    {
        ThrowIfDisposed();

        var key = (zoom, coord);
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out var cached))
                return;

            cached.RefCount--;
            if (cached.RefCount <= 0)
            {
                cached.Image.Dispose();
                _cache.Remove(key);
            }
        }
    }

    /// <summary>
    /// Queues a tile for disk write and adds metadata for DB batch.
    /// </summary>
    public void QueueWrite(string path, byte[] pngData, TileData metadata)
    {
        ThrowIfDisposed();

        _pendingWrites.Add(new PendingWrite(path, pngData));
        _pendingTileData.Add(metadata);
        _totalStorageMB += metadata.FileSizeBytes / (1024.0 * 1024.0);
    }

    /// <summary>
    /// Number of pending disk writes.
    /// </summary>
    public int PendingWriteCount => _pendingWrites.Count;

    /// <summary>
    /// Returns true when pending writes exceed threshold, suggesting incremental flush.
    /// </summary>
    public bool ShouldFlush => _pendingWrites.Count >= 100;

    /// <summary>
    /// Flushes pending disk writes in parallel batches.
    /// </summary>
    public async Task FlushWritesAsync(int batchSize = 50, int maxParallelism = 8)
    {
        ThrowIfDisposed();

        if (_pendingWrites.Count == 0)
            return;

        // Process in batches
        for (int i = 0; i < _pendingWrites.Count; i += batchSize)
        {
            var batch = _pendingWrites.Skip(i).Take(batchSize).ToList();

            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
                async (write, ct) =>
                {
                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(write.Path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    await File.WriteAllBytesAsync(write.Path, write.Data, ct);
                });
        }

        _pendingWrites.Clear();
    }

    /// <summary>
    /// Gets all pending tile metadata and total storage for batch DB write.
    /// Clears the pending lists after extraction.
    /// </summary>
    public (List<TileData> tiles, double totalMB) ExtractPendingMetadata()
    {
        ThrowIfDisposed();

        var tiles = _pendingTileData.ToList();
        var totalMB = _totalStorageMB;

        _pendingTileData.Clear();
        _totalStorageMB = 0;

        return (tiles, totalMB);
    }

    /// <summary>
    /// Gets current cache statistics for logging.
    /// Thread-safe.
    /// </summary>
    public (int cachedTiles, long estimatedMemoryBytes) GetStats()
    {
        ThrowIfDisposed();

        lock (_cacheLock)
        {
            int count = _cache.Count;
            // Each 100x100 RGBA image is ~40KB
            long estimatedBytes = count * 100 * 100 * 4;
            return (count, estimatedBytes);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ZoomTileCache));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_cacheLock)
        {
            // Dispose all cached images
            foreach (var cached in _cache.Values)
            {
                cached.Image.Dispose();
            }
            _cache.Clear();
        }
        _pendingWrites.Clear();
        _pendingTileData.Clear();
    }
}
