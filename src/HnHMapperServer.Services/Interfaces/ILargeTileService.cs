namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service for generating and managing 400x400 WebP tiles from base 100x100 tiles.
/// Provides on-the-fly generation with background pre-generation support.
/// </summary>
public interface ILargeTileService
{
    /// <summary>
    /// Gets or generates a 400x400 WebP tile. If the tile doesn't exist, it's generated on-the-fly.
    /// Results are cached in-memory for fast repeated access.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="mapId">Map ID</param>
    /// <param name="zoom">Zoom level (0-6)</param>
    /// <param name="x">X coordinate in 400x400 tile system</param>
    /// <param name="y">Y coordinate in 400x400 tile system</param>
    /// <returns>WebP tile bytes, or null if no source tiles exist</returns>
    Task<byte[]?> GetOrGenerateLargeTileAsync(string tenantId, int mapId, int zoom, int x, int y);

    /// <summary>
    /// Marks a tile as dirty by invalidating in-memory caches.
    /// Old WebP files remain on disk until overwritten by the background processor.
    /// Called when a base tile is uploaded or updated.
    /// </summary>
    Task MarkDirtyAsync(string tenantId, int mapId, int baseX, int baseY);

    /// <summary>
    /// Force-regenerates a tile from source data, bypassing all caches.
    /// Used by ZoomTileProcessorService to regenerate tiles after uploads.
    /// </summary>
    Task<byte[]?> ForceRegenerateLargeTileAsync(string tenantId, int mapId, int zoom, int x, int y);

    /// <summary>
    /// Generates all missing large tiles for a tenant's maps.
    /// Used by background job for pre-generation and migration.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of tiles generated</returns>
    Task<int> GenerateMissingTilesAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the filesystem path for a large tile (may not exist yet).
    /// </summary>
    string GetLargeTilePath(string tenantId, int mapId, int zoom, int x, int y);

    /// <summary>
    /// Invalidates all in-memory cached tiles for a tenant.
    /// Used for cross-process cache invalidation (API notifying Web).
    /// </summary>
    void InvalidateTenantCache(string tenantId);

    /// <summary>
    /// Invalidates in-memory cache entries for a specific tile and its parent zoom chain.
    /// Used for cross-process cache invalidation (API notifying Web after generating new tiles on disk).
    /// </summary>
    void InvalidateCachedTile(string tenantId, int mapId, int baseX, int baseY);
}
