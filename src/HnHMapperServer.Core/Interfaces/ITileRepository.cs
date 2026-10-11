using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Models;

namespace HnHMapperServer.Core.Interfaces;

public interface ITileRepository
{
    Task<TileData?> GetTileAsync(int mapId, Coord coord, int zoom);
    Task SaveTileAsync(TileData tileData);
    /// <summary>
    /// Tile tokens (map, coords, zoom, Cache) of ONE tenant, every zoom; since &gt; 0 keeps only rows
    /// with Cache &gt; since (cache-wipe tombstones, Cache = -1, then never match). Explicit tenant
    /// filter: safe without an HttpContext.
    /// </summary>
    Task<List<TileCacheDto>> GetTileCacheTokensAsync(string tenantId, long since);
    Task DeleteTilesByMapAsync(int mapId);

    /// <summary>
    /// Tile count per map at the given zoom (default 0 = mapped grid cells),
    /// for the current tenant. Maps with no tiles are absent from the result.
    /// </summary>
    Task<Dictionary<int, int>> GetTileCountsByMapAsync(int zoom = 0);

    // Batch operations for optimized import
    Task SaveTilesBatchAsync(IEnumerable<TileData> tiles, bool skipExistenceCheck = false);
}
