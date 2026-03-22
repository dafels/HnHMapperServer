using System.Collections.Concurrent;

namespace HnHMapperServer.Api.Services;

/// <summary>
/// In-memory cache for per-map revision numbers.
/// Used to append ?v=revision to tile URLs for efficient browser caching.
/// Revision increments on any tile-affecting operation (upload, merge, admin wipe).
/// Seeded from epoch time so revisions are always higher after a restart,
/// preventing stale browser cache hits when tiles are served with immutable headers.
/// </summary>
public class MapRevisionCache
{
    /// <summary>
    /// Base revision derived from server startup time.
    /// Ensures post-restart revisions are always higher than pre-restart values.
    /// </summary>
    private readonly int _baseRevision = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000_000);

    /// <summary>
    /// Thread-safe map from mapId → revision number.
    /// </summary>
    private readonly ConcurrentDictionary<int, int> _mapIdToRevision = new();

    /// <summary>
    /// Gets the current revision for a map.
    /// Returns an epoch-based value if map has not been seen yet.
    /// </summary>
    /// <param name="mapId">The map ID</param>
    /// <returns>Current revision number</returns>
    public int Get(int mapId)
    {
        return _mapIdToRevision.GetOrAdd(mapId, _ => _baseRevision);
    }

    /// <summary>
    /// Increments the revision for a map and returns the new value.
    /// Called after any tile-affecting operation (upload, merge, admin wipe).
    /// </summary>
    /// <param name="mapId">The map ID</param>
    /// <returns>The new revision number after increment</returns>
    public int Increment(int mapId)
    {
        return _mapIdToRevision.AddOrUpdate(mapId,
            addValue: _baseRevision + 1,
            updateValueFactory: (_, currentValue) => currentValue + 1);
    }

    /// <summary>
    /// Gets revisions for all known maps.
    /// Used to send initial revisions to new SSE clients.
    /// </summary>
    /// <returns>Dictionary of mapId → revision</returns>
    public Dictionary<int, int> GetAll()
    {
        return new Dictionary<int, int>(_mapIdToRevision);
    }
}

