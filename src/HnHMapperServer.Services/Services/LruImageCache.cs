using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// A bounded LRU (Least Recently Used) cache for Image objects.
/// When capacity is reached, the least recently used images are evicted and disposed.
/// Thread-safe for concurrent access.
/// </summary>
public class LruImageCache : IDisposable
{
    private readonly int _maxSize;
    private readonly Dictionary<string, LinkedListNode<(string Key, Image<Rgba32> Image)>> _cache;
    private readonly LinkedList<(string Key, Image<Rgba32> Image)> _lruList;
    private readonly System.Threading.Lock _lock = new();
    private bool _disposed;

    public LruImageCache(int maxSize = 50)
    {
        _maxSize = maxSize;
        _cache = new Dictionary<string, LinkedListNode<(string Key, Image<Rgba32> Image)>>(maxSize);
        _lruList = new LinkedList<(string Key, Image<Rgba32> Image)>();
    }

    /// <summary>
    /// Gets an image from the cache, moving it to the front (most recently used).
    /// Returns null if the key is not found.
    /// </summary>
    public Image<Rgba32>? Get(string key)
    {
        lock (_lock)
        {
            if (_disposed) return null;

            if (_cache.TryGetValue(key, out var node))
            {
                // Move to front (most recently used)
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                return node.Value.Image;
            }
            return null;
        }
    }

    /// <summary>
    /// Tries to get an image from the cache without modifying LRU order.
    /// </summary>
    public bool TryGet(string key, out Image<Rgba32>? image)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                image = null;
                return false;
            }

            if (_cache.TryGetValue(key, out var node))
            {
                image = node.Value.Image;
                return true;
            }
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Adds an image to the cache. If at capacity, evicts the least recently used image.
    /// The cache takes ownership of the image and will dispose it when evicted or cleared.
    /// </summary>
    public void Add(string key, Image<Rgba32> image)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                image.Dispose();
                return;
            }

            // If key already exists, update it
            if (_cache.TryGetValue(key, out var existingNode))
            {
                existingNode.Value.Image.Dispose();
                _lruList.Remove(existingNode);
                _cache.Remove(key);
            }

            // Evict oldest if at capacity
            while (_cache.Count >= _maxSize && _lruList.Last != null)
            {
                var oldest = _lruList.Last;
                _cache.Remove(oldest.Value.Key);
                _lruList.RemoveLast();
                oldest.Value.Image.Dispose();
            }

            // Add new entry at front
            var node = _lruList.AddFirst((key, image));
            _cache[key] = node;
        }
    }

    /// <summary>
    /// Checks if the cache contains the specified key.
    /// </summary>
    public bool Contains(string key)
    {
        lock (_lock)
        {
            return !_disposed && _cache.ContainsKey(key);
        }
    }

    /// <summary>
    /// Gets the current number of items in the cache.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>
    /// Clears all images from the cache and disposes them.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var node in _lruList)
            {
                node.Image.Dispose();
            }
            _lruList.Clear();
            _cache.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var node in _lruList)
            {
                node.Image.Dispose();
            }
            _lruList.Clear();
            _cache.Clear();
        }
    }
}
