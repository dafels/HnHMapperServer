using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
        group.MapDelete("{mapId}", DeleteMap);

        // PUT /admin/maps/{mapId}/default-position - Update map default starting position
        group.MapPut("{mapId}/default-position", UpdateDefaultPosition);
    }

    /// <summary>
    /// GET /admin/maps
    /// Lists maps in the current tenant with server-side pagination.
    /// </summary>
    private static async Task<IResult> GetMaps(
        int page,
        int pageSize,
        string? search,
        ApplicationDbContext db,
        ITenantContextAccessor tenantContext)
    {
        var tenantId = tenantContext.GetCurrentTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return Results.Unauthorized();
        }

        // Sanitize pagination params
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 25;
        if (pageSize > 100) pageSize = 100;

        // Build query with tenant filter
        var query = db.Maps
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId);

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(m => m.Name.Contains(search));
        }

        // Get total count
        var totalCount = await query.CountAsync();

        // Get paginated maps
        var maps = await query
            .OrderBy(m => m.Priority)
            .ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new AdminMapDto
            {
                Id = m.Id,
                Name = m.Name,
                Hidden = m.Hidden,
                Priority = m.Priority,
                DefaultStartX = m.DefaultStartX,
                DefaultStartY = m.DefaultStartY
            })
            .ToListAsync();

        return Results.Ok(new PagedResult<AdminMapDto>
        {
            Items = maps,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
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

    /// <summary>
    /// DELETE /admin/maps/{mapId}
    /// Deletes a map and all associated data (grids, tiles, markers, roads, overlays) plus its
    /// tile files and WebP pyramid — the shared <see cref="IMapDeletionService"/> teardown, which
    /// keeps files other maps' tile rows still use. Triggers SSE update and audit log.
    /// </summary>
    private static async Task<IResult> DeleteMap(
        int mapId,
        IMapRepository mapRepository,
        IMapDeletionService mapDeletionService,
        IAuditService auditService,
        IUpdateNotificationService notificationService,
        ITenantContextAccessor tenantContext,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        HttpContext context)
    {
        var logger = loggerFactory.CreateLogger("MapAdminEndpoints");
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

        var result = await mapDeletionService.DeleteMapAsync(tenantId, mapId);

        // The Web process keeps its own in-memory WebP cache (the service evicted the API's).
        await SuperadminEndpoints.InvalidateWebMapTileCacheAsync(httpClientFactory, tenantId, mapId, logger);

        // Trigger SSE update for real-time UI refresh
        notificationService.NotifyMapDeleted(mapId);

        // Audit log
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "MapDeleted",
            EntityType = "Map",
            EntityId = mapId.ToString(),
            OldValue = $"Name: {result.MapName}, Grids: {result.Grids}, Tiles: {result.Tiles}, Markers: {result.Markers}, CustomMarkers: {result.CustomMarkers}, Roads: {result.Roads}, Overlays: {result.Overlays}, Files: {result.FilesDeleted}, WebpFiles: {result.WebpFilesDeleted}, SharedFilesKept: {result.FilesKeptShared}",
            NewValue = null
        });

        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("Deleting map {MapId}: {Warning}", mapId, warning);
        }

        return Results.Ok(new DeleteMapResponse
        {
            Message = $"Map '{result.MapName}' deleted with {result.Grids} grids, {result.Tiles} tiles, {result.Markers} markers, {result.CustomMarkers} custom markers, {result.Roads} roads, and {result.FilesDeleted + result.WebpFilesDeleted} files"
        });
    }

    private record DeleteMapResponse
    {
        public required string Message { get; init; }
    }

    /// <summary>
    /// PUT /admin/maps/{mapId}/default-position
    /// Updates the default starting position for a map.
    /// </summary>
    private static async Task<IResult> UpdateDefaultPosition(
        int mapId,
        UpdateDefaultPositionRequest request,
        IMapRepository mapRepository,
        IAuditService auditService,
        ITenantContextAccessor tenantContext,
        IUpdateNotificationService notificationService,
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

        var oldX = map.DefaultStartX;
        var oldY = map.DefaultStartY;

        map.DefaultStartX = request.X;
        map.DefaultStartY = request.Y;

        // Save changes
        await mapRepository.SaveMapAsync(map);

        // Notify viewers so Reset View picks up the new position without a refresh
        notificationService.NotifyMapUpdated(map);

        // Audit log
        var userId = context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name ?? "unknown";
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "MapDefaultPositionUpdated",
            EntityType = "Map",
            EntityId = mapId.ToString(),
            OldValue = $"DefaultStartX: {oldX}, DefaultStartY: {oldY}",
            NewValue = $"DefaultStartX: {request.X}, DefaultStartY: {request.Y}"
        });

        return Results.Ok(new { message = "Map default position updated" });
    }

    private record UpdateDefaultPositionRequest(int? X, int? Y);
}
