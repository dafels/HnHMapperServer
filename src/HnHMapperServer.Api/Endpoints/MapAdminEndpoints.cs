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
    /// Deletes a map and all associated data (grids, tiles, markers).
    /// Also deletes physical tile files from disk.
    /// Triggers SSE update and audit log.
    /// </summary>
    private static async Task<IResult> DeleteMap(
        int mapId,
        IMapRepository mapRepository,
        ApplicationDbContext db,
        IAuditService auditService,
        IUpdateNotificationService notificationService,
        ITenantContextAccessor tenantContext,
        IStorageQuotaService quotaService,
        IConfiguration configuration,
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

        var mapName = map.Name;
        var gridStorage = configuration.GetValue<string>("GridStorage") ?? "map";

        // Collect grid IDs for marker deletion and file cleanup
        var grids = await db.Grids
            .Where(g => g.Map == mapId && g.TenantId == tenantId)
            .ToListAsync();
        var gridIds = grids.Select(g => g.Id).ToList();

        // Collect tile file paths before deletion for file system cleanup
        var tiles = await db.Tiles
            .Where(t => t.MapId == mapId && t.TenantId == tenantId)
            .ToListAsync();

        // Delete dirty zoom tiles for this map
        var dirtyTiles = await db.DirtyZoomTiles
            .Where(d => d.MapId == mapId && d.TenantId == tenantId)
            .ToListAsync();
        db.DirtyZoomTiles.RemoveRange(dirtyTiles);

        // Delete all related database records
        db.Grids.RemoveRange(grids);
        db.Tiles.RemoveRange(tiles);

        // Delete markers that belong to grids in this map
        var markers = await db.Markers.Where(m => gridIds.Contains(m.GridId) && m.TenantId == tenantId).ToListAsync();
        db.Markers.RemoveRange(markers);

        var customMarkers = await db.CustomMarkers.Where(cm => cm.MapId == mapId && cm.TenantId == tenantId).ToListAsync();
        db.CustomMarkers.RemoveRange(customMarkers);

        // Delete roads associated with this map
        var roads = await db.Roads.Where(r => r.MapId == mapId && r.TenantId == tenantId).ToListAsync();
        db.Roads.RemoveRange(roads);

        // Delete overlay data for this map
        var overlayData = await db.OverlayData.Where(o => o.MapId == mapId && o.TenantId == tenantId).ToListAsync();
        db.OverlayData.RemoveRange(overlayData);

        // Delete overlay offsets that reference this map (as either current or overlay map)
        var overlayOffsets = await db.OverlayOffsets
            .Where(o => (o.CurrentMapId == mapId || o.OverlayMapId == mapId) && o.TenantId == tenantId)
            .ToListAsync();
        db.OverlayOffsets.RemoveRange(overlayOffsets);

        // Delete the map record
        var mapEntity = await db.Maps.FirstOrDefaultAsync(m => m.Id == mapId && m.TenantId == tenantId);
        if (mapEntity != null)
        {
            db.Maps.Remove(mapEntity);
        }

        // Save all database changes in one transaction
        await db.SaveChangesAsync();

        // Delete physical tile files from disk
        var deletedFiles = 0;
        var deletedDirectories = 0;

        // Delete tile files (zoom tiles stored at tenants/{tenantId}/{mapId}/{zoom}/*.png)
        foreach (var tile in tiles)
        {
            if (!string.IsNullOrEmpty(tile.File))
            {
                var filePath = Path.Combine(gridStorage, tile.File);
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        deletedFiles++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete tile file: {FilePath}", filePath);
                }
            }
        }

        // Delete grid files (stored at tenants/{tenantId}/grids/{gridId}.png)
        foreach (var gridId in gridIds)
        {
            var gridFilePath = Path.Combine(gridStorage, "tenants", tenantId, "grids", $"{gridId}.png");
            try
            {
                if (File.Exists(gridFilePath))
                {
                    File.Delete(gridFilePath);
                    deletedFiles++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete grid file: {FilePath}", gridFilePath);
            }
        }

        // Delete empty map directory and zoom subdirectories
        var mapDirectory = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString());
        if (Directory.Exists(mapDirectory))
        {
            try
            {
                // Delete recursively (all zoom level directories)
                Directory.Delete(mapDirectory, recursive: true);
                deletedDirectories++;
                logger.LogInformation("Deleted map directory: {MapDirectory}", mapDirectory);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete map directory: {MapDirectory}", mapDirectory);
            }
        }

        // Recalculate storage quota after file deletion
        await quotaService.RecalculateStorageUsageAsync(tenantId, gridStorage);

        // Trigger SSE update for real-time UI refresh
        notificationService.NotifyMapDeleted(mapId);

        // Audit log
        var userId = context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name ?? "unknown";
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "MapDeleted",
            EntityType = "Map",
            EntityId = mapId.ToString(),
            OldValue = $"Name: {mapName}, Grids: {grids.Count}, Tiles: {tiles.Count}, Markers: {markers.Count}, CustomMarkers: {customMarkers.Count}, Roads: {roads.Count}, Overlays: {overlayData.Count}, Files: {deletedFiles}",
            NewValue = null
        });

        logger.LogInformation("Deleted map {MapId} '{MapName}': {GridCount} grids, {TileCount} tiles, {MarkerCount} markers, {CustomMarkerCount} custom markers, {RoadCount} roads, {OverlayCount} overlays, {DeletedFiles} files, {DeletedDirs} directories",
            mapId, mapName, grids.Count, tiles.Count, markers.Count, customMarkers.Count, roads.Count, overlayData.Count, deletedFiles, deletedDirectories);

        return Results.Ok(new DeleteMapResponse
        {
            Message = $"Map '{mapName}' deleted with {grids.Count} grids, {tiles.Count} tiles, {markers.Count} markers, {customMarkers.Count} custom markers, {roads.Count} roads, and {deletedFiles} files"
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
