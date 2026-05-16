using HnHMapperServer.Web.Services.Map;

namespace HnHMapperServer.Web.Components.Map;

/// <summary>
/// Per-component tile registry. Tracks which tiles are visible, holds an LRU cap so
/// we don't accumulate unbounded entries during long sessions, and a short-lived
/// negative cache so a 404 tile doesn't trigger a refetch storm on every pan tick.
/// Revision numbers (for cache busting) are read from <see cref="MapNavigationService"/>.
/// </summary>
public sealed class TileGrid
{
    public readonly record struct TileKey(int MapId, int Zoom, int X, int Y);

    public sealed record VisibleTile(TileKey Key, int Left, int Top, string Url);

    private const int LruCap = 5000;
    private const long NegativeCacheTtlTicks = TimeSpan.TicksPerSecond * 30;

    private readonly MapNavigationService _navigation;
    private readonly LinkedList<TileKey> _lru = new();
    private readonly Dictionary<TileKey, LinkedListNode<TileKey>> _index = new();
    private readonly Dictionary<TileKey, long> _negativeCache = new();

    public TileGrid(MapNavigationService navigation)
    {
        _navigation = navigation;
    }

    /// <summary>
    /// Enumerate the tiles that should be rendered for the current viewport, in stable order.
    /// Each tile gets a screen position and a fully-formed URL (with cache-busting revision).
    /// </summary>
    public IEnumerable<VisibleTile> GetVisibleTiles(MapViewport vp, int mapId, int marginTiles = 1)
    {
        if (mapId <= 0) yield break;

        var (minX, minY, maxX, maxY) = MapMath.VisibleTileRange(vp, marginTiles);
        var revision = _navigation.GetMapRevision(mapId);

        for (var ty = minY; ty <= maxY; ty++)
        {
            for (var tx = minX; tx <= maxX; tx++)
            {
                var key = new TileKey(mapId, vp.Zoom, tx, ty);
                if (IsNegativeCached(key)) continue;
                Touch(key);
                var (left, top) = MapMath.TileScreenPosition(vp, tx, ty);
                yield return new VisibleTile(key, left, top, BuildUrl(key, revision));
            }
        }
    }

    /// <summary>URL for one tile, including the cache-bust revision.</summary>
    public string BuildUrl(TileKey key, int revision)
        => $"/map/grids/{key.MapId}/{key.Zoom}/{key.X}_{key.Y}.png?v={revision}";

    /// <summary>URL for one tile, looking up revision automatically.</summary>
    public string BuildUrl(TileKey key)
        => BuildUrl(key, _navigation.GetMapRevision(key.MapId));

    /// <summary>Record a tile load failure so we don't keep trying it.</summary>
    public void MarkLoadFailed(TileKey key)
    {
        _negativeCache[key] = DateTime.UtcNow.Ticks + NegativeCacheTtlTicks;
    }

    /// <summary>
    /// Drop a tile from the negative cache (called when the user might expect a retry,
    /// e.g. on map revision bump).
    /// </summary>
    public void ClearNegative(TileKey key) => _negativeCache.Remove(key);

    /// <summary>
    /// Clear all negative cache entries for a given map (used when its revision bumps —
    /// failed tiles may now exist).
    /// </summary>
    public void ClearNegativeForMap(int mapId)
    {
        if (_negativeCache.Count == 0) return;
        var stale = _negativeCache.Keys.Where(k => k.MapId == mapId).ToList();
        foreach (var k in stale) _negativeCache.Remove(k);
    }

    /// <summary>Periodically expire negative-cache entries.</summary>
    public void EvictExpiredNegatives()
    {
        if (_negativeCache.Count == 0) return;
        var now = DateTime.UtcNow.Ticks;
        var stale = _negativeCache.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        foreach (var k in stale) _negativeCache.Remove(k);
    }

    private bool IsNegativeCached(TileKey key)
    {
        if (!_negativeCache.TryGetValue(key, out var expires)) return false;
        if (expires <= DateTime.UtcNow.Ticks)
        {
            _negativeCache.Remove(key);
            return false;
        }
        return true;
    }

    private void Touch(TileKey key)
    {
        if (_index.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return;
        }
        node = _lru.AddFirst(key);
        _index[key] = node;
        TrimLru();
    }

    private void TrimLru()
    {
        while (_index.Count > LruCap && _lru.Last is { } tail)
        {
            _lru.RemoveLast();
            _index.Remove(tail.Value);
        }
    }
}
