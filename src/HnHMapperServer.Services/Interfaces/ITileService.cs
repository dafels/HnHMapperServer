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
    /// True when any tile row of a LIVE map of the tenant (every map except <paramref name="excludeMapId"/>)
    /// references a file inside <paramref name="relativeDirectory"/> (relative to GridStorage, either
    /// separator). Same reference rule as the integrity scan/purge: rows of dead map ids and empty
    /// File tombstones never protect a directory.
    /// </summary>
    Task<bool> AnyLiveTileReferencesDirectoryAsync(string tenantId, string relativeDirectory, int excludeMapId);

    /// <summary>
    /// Marks the zoom 1-6 parents of many base coords dirty in one deduped pass, plus the WebP
    /// cells covering them (DirtyWebpCells, MarkedAt bumped on re-mark). Used by bulk writers
    /// (imports, merges, rebuilds); the WebP cell marks are the durable regeneration backstop.
    /// </summary>
    Task MarkParentTilesDirtyBatchAsync(string tenantId, int mapId, IReadOnlyCollection<Coord> baseCoords);

    /// <summary>
    /// True when the map has a zoom-0 row at <paramref name="coord"/> whose file exists on disk.
    /// A row whose file is gone (or a wipe tombstone) is not a tile anything can draw.
    /// </summary>
    Task<bool> HasRenderableTileAsync(int mapId, Coord coord, string gridStorage);

    /// <summary>
    /// Replaces the zoom-0 / legacy zoom PNG at <paramref name="fullPath"/> (temp file + rename;
    /// a failed write leaves the previous file intact) and charges the quota with the size change
    /// (new minus replaced). Returns the bytes charged.
    /// </summary>
    Task<long> ReplaceTileFileAsync(string tenantId, string fullPath, byte[] bytes);

    /// <summary>
    /// Deletes a merged-away map's WebP pyramid (never credited: reconciled-only) and its per-map
    /// directory unless a live-map row still references a file inside it (credited on success),
    /// charging <paramref name="rehomedBytes"/> (files the merge copied into the grid pool) minus
    /// the freed bytes in one update. Returns the bytes charged.
    /// </summary>
    Task<long> DeleteRetiredMapDirectoriesAsync(string tenantId, int mapId, string gridStorage, long rehomedBytes);

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
