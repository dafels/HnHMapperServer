using HnHMapperServer.Core.Models;

namespace HnHMapperServer.Services.Interfaces;

public interface ITileService
{
    /// <summary>
    /// Saves a tile to the repository and publishes update notification
    /// </summary>
    Task SaveTileAsync(int mapId, Coord coord, int zoom, string file, long timestamp, string tenantId, int fileSizeBytes);

    /// <summary>
    /// Gets a tile from the repository
    /// </summary>
    Task<TileData?> GetTileAsync(int mapId, Coord coord, int zoom);

    /// <summary>
    /// Deletes all tile rows (all zoom levels) for a map. Used when a merge retires the source map.
    /// </summary>
    Task DeleteTilesByMapAsync(int mapId);

    /// <summary>
    /// Marks the zoom 1-6 parents of many base coords dirty in one deduped pass.
    /// Used by bulk writers (imports, merges, rebuilds) as the durable regeneration backstop.
    /// </summary>
    Task MarkParentTilesDirtyBatchAsync(string tenantId, int mapId, IReadOnlyCollection<Coord> baseCoords);

    /// <summary>
    /// Updates the zoom level by combining 4 sub-tiles into one parent tile
    /// </summary>
    Task UpdateZoomLevelAsync(int mapId, Coord coord, int zoom, string tenantId, string gridStorage, List<TileData>? preloadedTiles = null);

    /// <summary>
    /// Rebuilds all zoom levels for all tiles (admin operation)
    /// NOTE: Not yet updated for multi-tenancy
    /// </summary>
    Task RebuildZoomsAsync(string gridStorage);

    /// <summary>
    /// Rebuilds incomplete zoom tiles where new sub-tiles have been added since the zoom tile was created
    /// Returns the number of tiles rebuilt
    /// </summary>
    Task<int> RebuildIncompleteZoomTilesAsync(string tenantId, string gridStorage, int maxTilesToRebuild);

    /// <summary>
    /// Checks if tenant has any dirty tiles pending rebuild.
    /// Used by ZoomTileRebuildService for fast skip check.
    /// </summary>
    Task<bool> HasDirtyZoomTilesAsync(string tenantId);

    /// <summary>
    /// Gets the count of dirty tiles for a tenant.
    /// Used for monitoring and logging.
    /// </summary>
    Task<int> GetDirtyZoomTileCountAsync(string tenantId);

    /// <summary>
    /// Gets the distinct map IDs that have dirty zoom tiles for a tenant.
    /// Used by LargeTileGenerationService to only scan maps with recent uploads.
    /// </summary>
    Task<HashSet<int>> GetDirtyMapIdsAsync(string tenantId);
}
