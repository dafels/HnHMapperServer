using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Wipes a tenant's *content* while leaving the tenant itself intact.
///
/// Removed: maps, grids, tiles (rows and files on disk), game and custom markers, roads,
/// pings, overlays, timers, and notifications.
///
/// Kept: the tenant row, its users, roles, permissions, invitations, API tokens, the whole
/// cookbook (foods, variants, panels/favorites — player contributions no re-import can
/// restore) and its config (minus the now-dangling main map pointer).
///
/// The point of the operation is reclaiming disk, so the result reports bytes freed.
/// </summary>
public interface ITenantDataPurgeService
{
    Task<PurgeTenantDataResultDto> PurgeAsync(string tenantId, CancellationToken ct = default);
}
