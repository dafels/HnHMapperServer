using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Admin endpoints for managing maps within a tenant.
/// Requires TenantAdmin role or SuperAdmin role.
/// </summary>
public static class MapAdminEndpoints
{
    public static void MapMapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/maps")
            .RequireAuthorization("TenantAdmin");

        // GET /admin/maps - List all maps in tenant
        group.MapGet("", GetMaps);

        // PUT /admin/maps/{mapId}/rename - Rename a map
        group.MapPut("{mapId}/rename", RenameMap);

        // PUT /admin/maps/{mapId}/settings - Update map settings (hidden/priority)
        group.MapPut("{mapId}/settings", UpdateMapSettings);

        // DELETE /admin/maps/{mapId} - Delete a map
        // TODO: Disabled for now - too destructive for tenant admins
        // Consider re-enabling for SuperAdmin only in the future
        // group.MapDelete("{mapId}", DeleteMap);
    }

    /// <summary>
    /// GET /admin/maps
    /// Lists all maps in the current tenant with their metadata.
    /// </summary>
    private static async Task<IResult> GetMaps(
        IMapRepository mapRepository,
        ApplicationDbContext db,
        ITenantContextAccessor tenantContext)
    {
        var tenantId = tenantContext.GetCurrentTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return Results.Unauthorized();
        }

        var maps = await mapRepository.GetAllMapsAsync();

        // Convert to AdminMapDto with grid counts
        var mapDtos = new List<AdminMapDto>();
        foreach (var map in maps)
        {
            var gridCount = await db.Grids
                .Where(g => g.Map == map.Id && g.TenantId == tenantId)
                .CountAsync();

            mapDtos.Add(new AdminMapDto
            {
                Id = map.Id,
                Name = map.Name,
                Hidden = map.Hidden,
                Priority = map.Priority
            });
        }

        return Results.Ok(mapDtos);
    }

    /// <summary>
    /// PUT /admin/maps/{mapId}/rename
    /// Renames a map. Triggers SSE update and audit log.
    /// </summary>
    private static async Task<IResult> RenameMap(
        int mapId,
        RenameMapRequest request,
        IMapRepository mapRepository,
        IAuditService auditService,
        IUpdateNotificationService notificationService,
        ITenantContextAccessor tenantContext,
        HttpContext context)
    {
        var tenantId = tenantContext.GetCurrentTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return Results.Unauthorized();
        }

        // Validate request
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = "Map name cannot be empty" });
        }

        if (request.Name.Length > 100)
        {
            return Results.BadRequest(new { error = "Map name cannot exceed 100 characters" });
        }

        // Get existing map (automatically tenant-scoped)
        var map = await mapRepository.GetMapAsync(mapId);
        if (map == null)
        {
            return Results.NotFound(new { error = $"Map {mapId} not found" });
        }

        var oldName = map.Name;
        map.Name = request.Name;

        // Save changes
        await mapRepository.SaveMapAsync(map);

        // Trigger SSE update for real-time UI refresh
        notificationService.NotifyMapUpdated(map);

        // Audit log
        var userId = context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name ?? "unknown";
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "MapRenamed",
            EntityType = "Map",
            EntityId = mapId.ToString(),
            OldValue = $"Name: {oldName}",
            NewValue = $"Name: {request.Name}"
        });

        return Results.Ok(new { message = $"Map renamed to '{request.Name}'" });
    }

    /// <summary>
    /// PUT /admin/maps/{mapId}/settings
    /// Updates map hidden status and priority. Triggers SSE update and audit log.
    /// </summary>
    private static async Task<IResult> UpdateMapSettings(
        int mapId,
        AdminMapDto request,
        IMapRepository mapRepository,
        IAuditService auditService,
        IUpdateNotificationService notificationService,
        ITenantContextAccessor tenantContext,
        HttpContext context)
    {
        var tenantId = tenantContext.GetCurrentTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return Results.Unauthorized();
        }

        // Get existing map (automatically tenant-scoped)
        var map = await mapRepository.GetMapAsync(mapId);
        if (map == null)
        {
            return Results.NotFound(new { error = $"Map {mapId} not found" });
        }

        var oldHidden = map.Hidden;
        var oldPriority = map.Priority;

        map.Hidden = request.Hidden;
        map.Priority = request.Priority;

        // Save changes
        await mapRepository.SaveMapAsync(map);

        // Trigger SSE update for real-time UI refresh
        notificationService.NotifyMapUpdated(map);

        // Audit log
        var userId = context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name ?? "unknown";
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "MapSettingsUpdated",
            EntityType = "Map",
            EntityId = mapId.ToString(),
            OldValue = $"Hidden: {oldHidden}, Priority: {oldPriority}",
            NewValue = $"Hidden: {request.Hidden}, Priority: {request.Priority}"
        });

        return Results.Ok(new { message = "Map settings updated" });
    }

    // TODO: Delete functionality disabled - too destructive for tenant admins
    // Consider re-enabling for SuperAdmin only in the future

    ///// <summary>
    ///// DELETE /admin/maps/{mapId}
    ///// Deletes a map and all associated data (grids, tiles, markers).
    ///// Triggers SSE update and audit log.
    ///// </summary>
    //private static async Task<IResult> DeleteMap(
    //    int mapId,
    //    IMapRepository mapRepository,
    //    ApplicationDbContext db,
    //    IAuditService auditService,
    //    IUpdateNotificationService notificationService,
    //    ITenantContextAccessor tenantContext,
    //    IStorageQuotaService quotaService,
    //    IConfiguration configuration,
    //    HttpContext context)
    //{
    //    var tenantId = tenantContext.GetCurrentTenantId();
    //    if (string.IsNullOrEmpty(tenantId))
    //    {
    //        return Results.Unauthorized();
    //    }
    //
    //    // Get existing map (automatically tenant-scoped)
    //    var map = await mapRepository.GetMapAsync(mapId);
    //    if (map == null)
    //    {
    //        return Results.NotFound(new { error = $"Map {mapId} not found" });
    //    }
    //
    //    var mapName = map.Name;
    //
    //    // Count related data for audit log
    //    var gridCount = await db.Grids.Where(g => g.Map == mapId && g.TenantId == tenantId).CountAsync();
    //    var tileCount = await db.Tiles.Where(t => t.MapId == mapId && t.TenantId == tenantId).CountAsync();
    //
    //    // Markers are related to Grids, so join to find markers for this map
    //    var gridIds = db.Grids.Where(g => g.Map == mapId && g.TenantId == tenantId).Select(g => g.Id).ToList();
    //    var markerCount = await db.Markers.Where(m => gridIds.Contains(m.GridId) && m.TenantId == tenantId).CountAsync();
    //
    //    var customMarkerCount = await db.CustomMarkers.Where(cm => cm.MapId == mapId && cm.TenantId == tenantId).CountAsync();
    //
    //    // Delete all related data (EF Core will handle this via cascade delete if configured)
    //    // Manually delete to ensure tenant isolation
    //    db.Grids.RemoveRange(db.Grids.Where(g => g.Map == mapId && g.TenantId == tenantId));
    //    db.Tiles.RemoveRange(db.Tiles.Where(t => t.MapId == mapId && t.TenantId == tenantId));
    //
    //    // Delete markers that belong to grids in this map
    //    db.Markers.RemoveRange(db.Markers.Where(m => gridIds.Contains(m.GridId) && m.TenantId == tenantId));
    //
    //    db.CustomMarkers.RemoveRange(db.CustomMarkers.Where(cm => cm.MapId == mapId && cm.TenantId == tenantId));
    //
    //    // Delete the map itself
    //    await mapRepository.DeleteMapAsync(mapId);
    //
    //    // Trigger storage quota recalculation
    //    var gridStorage = configuration.GetValue<string>("GridStorage") ?? "map";
    //    await quotaService.RecalculateStorageUsageAsync(tenantId, gridStorage);
    //
    //    // Trigger SSE update for real-time UI refresh
    //    notificationService.NotifyMapDeleted(mapId);
    //
    //    // Audit log
    //    var userId = context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name ?? "unknown";
    //    await auditService.LogAsync(new AuditEntry
    //    {
    //        TenantId = tenantId,
    //        Action = "MapDeleted",
    //        EntityType = "Map",
    //        EntityId = mapId.ToString(),
    //        OldValue = $"Name: {mapName}, Grids: {gridCount}, Tiles: {tileCount}, Markers: {markerCount}, CustomMarkers: {customMarkerCount}",
    //        NewValue = null
    //    });
    //
    //    return Results.Ok(new DeleteMapResponse
    //    {
    //        Message = $"Map '{mapName}' deleted with {gridCount} grids, {tileCount} tiles, {markerCount} markers, and {customMarkerCount} custom markers"
    //    });
    //}
}
