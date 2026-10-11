using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// The one map-delete teardown, shared by the tenant-admin and superadmin deletes. Removes every
/// row of the map (grids, tiles of all zooms, markers on its grids, custom markers, roads, pings,
/// overlays and overlay offsets, pending dirty marks, the map itself), its tile files unless
/// another live map's tile row still references them, its per-map directory (unless such a
/// reference lives inside it) and its WebP pyramid (tenants/{t}/large/{mapId}), then recalculates
/// the tenant's storage usage. Explicit tenant id + IgnoreQueryFilters throughout, so it works
/// from any request context.
/// </summary>
public interface IMapDeletionService
{
    /// <exception cref="ArgumentException">The map does not exist in that tenant.</exception>
    Task<MapDeletionResultDto> DeleteMapAsync(string tenantId, int mapId, CancellationToken ct = default);
}
