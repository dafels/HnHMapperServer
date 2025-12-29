using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;
using HnHMapperServer.Core.DTOs;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Core.Constants;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Map frontend API endpoints
/// Matches Go implementation routes: /map/api/*
/// </summary>
public static class MapEndpoints
{
    public static void MapMapEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/map/api")
            .RequireAuthorization("TenantMapAccess"); // Require Map permission for map APIs

        group.MapGet("/v1/characters", GetCharacters);
        group.MapGet("/v1/markers", GetMarkers);
        group.MapGet("/v1/overlays", GetOverlays);
        group.MapGet("/config", GetConfig);
        group.MapGet("/maps", GetMaps);
        group.MapGet("/v1/grids", GetGridIds);
        group.MapPost("/admin/wipeTile", WipeTile);
        group.MapPost("/admin/setCoords", SetCoords);
        group.MapPost("/admin/hideMarker", HideMarker);
        group.MapPost("/admin/deleteMarker", DeleteMarker);

        // Overlay offset persistence endpoints
        group.MapGet("/v1/overlay-offset", GetOverlayOffset);
        group.MapPost("/v1/overlay-offset", SaveOverlayOffset);

        app.MapGet("/map/updates", WatchGridUpdates).RequireAuthorization("TenantMapAccess");
        app.MapGet("/map/grids/{**path}", ServeGridTile)
            .RequireAuthorization("TenantMapAccess")
            .CacheOutput(policy => policy
                .Expire(TimeSpan.FromSeconds(60))  // In-memory cache for 60 seconds
                .SetVaryByQuery("v", "cache")      // Vary by revision and cache-bust params
                .SetVaryByRouteValue("path")       // Vary by tile path (mapId/zoom/x_y)
                .Tag("tiles"));                     // Tag for bulk invalidation if needed

        // Public endpoint for Discord webhook preview images (HMAC-signed URLs, rate limited)
        app.MapGet("/map/preview/{previewId}", ServePreviewImage)
            .AllowAnonymous()
            .RequireRateLimiting("PreviewAccess")
            .CacheOutput(policy => policy
                .Expire(TimeSpan.FromHours(48))    // Cache for 48 hours (signed URL expiration)
                .SetVaryByRouteValue("previewId")); // Vary by preview ID
    }

    private static bool HasPermission(ClaimsPrincipal user, Permission permission)
    {
        // SuperAdmin bypasses all permission checks
        if (user.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
            return true;

        var permissionValue = permission.ToClaimValue();
        return user.Claims.Any(c =>
            c.Type == AuthorizationConstants.ClaimTypes.TenantPermission &&
            c.Value.Equals(permissionValue, StringComparison.OrdinalIgnoreCase));
    }

    private static IResult GetCharacters(
        HttpContext context,
        ICharacterService characterService,
        ILogger<Program> logger)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Map permission already enforced by TenantMapAccess policy

        // If no Pointer permission, return empty array instead of 401
        if (!HasPermission(context.User, Permission.Pointer))
            return Results.Json(new List<object>());

        // Extract tenant ID from context (set by TenantContextMiddleware)
        var tenantId = context.Items["TenantId"] as string ?? string.Empty;
        if (string.IsNullOrEmpty(tenantId))
        {
            logger.LogWarning("GetCharacters: No tenant ID in context");
            return Results.Unauthorized();
        }

        var characters = characterService.GetAllCharacters(tenantId);

        // Diagnostic: log when user has proper auth but no characters are available
        if (characters.Count == 0)
        {
            logger.LogDebug("GetCharacters returned 0 characters for tenant {TenantId}. User has Map+Pointer auth but no active positions. Likely causes: (1) No grids registered yet (client must send gridUpdate first), (2) All characters older than 10s (cleaned by CharacterCleanupService), (3) Client not sending positionUpdate.", tenantId);
        }

        return Results.Json(characters);
    }

    private static async Task<IResult> GetMarkers(
        HttpContext context,
        IMarkerService markerService)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Map permission already enforced by TenantMapAccess policy

        // If no Markers permission, return empty array instead of 401
        if (!HasPermission(context.User, Permission.Markers))
            return Results.Json(new List<object>());

        var markers = await markerService.GetAllFrontendMarkersAsync();
        return Results.Json(markers);
    }

    /// <summary>
    /// Get overlay data (claims, villages, provinces) for visible grid coordinates.
    /// Query params:
    ///   mapId: The map ID
    ///   coords: Comma-separated list of x_y coordinates (e.g., "10_20,11_20,12_20")
    /// Returns array of overlay data with base64-encoded bitpacked data.
    /// Uses in-memory caching (30 min TTL) to reduce database load.
    /// </summary>
    private static async Task<IResult> GetOverlays(
        HttpContext context,
        [FromQuery] int mapId,
        [FromQuery] string? coords,
        IOverlayDataRepository overlayRepository,
        IMemoryCache cache,
        ILogger<Program> logger)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Map permission already enforced by TenantMapAccess policy

        if (string.IsNullOrWhiteSpace(coords))
            return Results.Json(new List<object>());

        // Parse coordinates (format: "x1_y1,x2_y2,x3_y3")
        var coordList = new List<(int X, int Y)>();
        foreach (var coordStr in coords.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = coordStr.Trim().Split('_');
            if (parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y))
            {
                coordList.Add((x, y));
            }
        }

        if (coordList.Count == 0)
            return Results.Json(new List<object>());

        // Limit to prevent abuse (max 100 grids per request)
        if (coordList.Count > 100)
        {
            coordList = coordList.Take(100).ToList();
            logger.LogWarning("GetOverlays: Truncated coordinate list to 100 items");
        }

        // Extract tenant ID for cache key
        var tenantId = context.Items["TenantId"] as string ?? string.Empty;

        // Build cache key from tenant, map, and sorted coordinates
        // Sort coords to ensure consistent cache keys regardless of request order
        var sortedCoords = coordList.OrderBy(c => c.X).ThenBy(c => c.Y).ToList();
        var coordsKey = string.Join(",", sortedCoords.Select(c => $"{c.X}_{c.Y}"));
        var cacheKey = $"overlays:{tenantId}:{mapId}:{coordsKey}";

        // Try to get from cache first
        if (cache.TryGetValue(cacheKey, out List<object>? cachedResponse) && cachedResponse != null)
        {
            logger.LogDebug("GetOverlays: Cache hit for {CoordCount} coords on map {MapId}", coordList.Count, mapId);
            return Results.Json(cachedResponse);
        }

        // Cache miss - fetch from database
        var overlays = await overlayRepository.GetOverlaysForGridsAsync(mapId, coordList);

        // Transform to API response format with base64-encoded data
        var response = overlays.Select(o => new
        {
            MapId = o.MapId,
            X = o.Coord.X,
            Y = o.Coord.Y,
            Type = o.OverlayType,
            Data = Convert.ToBase64String(o.Data),
            UpdatedAt = o.UpdatedAt
        }).Cast<object>().ToList();

        // Cache for 30 minutes with sliding expiration
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(30))
            .SetAbsoluteExpiration(TimeSpan.FromHours(2)) // Hard cap at 2 hours
            .SetSize(response.Count + 1); // Size based on number of overlays

        cache.Set(cacheKey, response, cacheOptions);

        logger.LogDebug("GetOverlays: Cache miss for {CoordCount} coords on map {MapId}, found {OverlayCount} overlays",
            coordList.Count, mapId, response.Count);

        return Results.Json(response);
    }

    private static async Task<IResult> GetConfig(
        HttpContext context,
        IConfigRepository configRepository,
        HnHMapperServer.Infrastructure.Data.ApplicationDbContext db,
        ILogger<Program> logger)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Authorization already enforced by TenantMapAccess policy
        // Legacy HasAuth check removed - no longer needed with multi-tenancy

        // Get permissions from current tenant
        var tenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var username = context.User.Identity?.Name ?? "(unknown)";

        var permissions = Array.Empty<string>();
        var tenantRole = "";
        if (string.IsNullOrEmpty(tenantId))
        {
            logger.LogWarning("User {Username} ({UserId}) has no TenantId claim. Tenant selection may not have completed.", username, userId);
            logger.LogWarning("Available claims ({Count}):", context.User.Claims.Count());
            foreach (var claim in context.User.Claims)
            {
                logger.LogWarning("  Claim: {Type} = {Value}", claim.Type, claim.Value);
            }
        }
        else if (string.IsNullOrEmpty(userId))
        {
            logger.LogWarning("User {Username} has no UserId claim. This should not happen.", username);
        }
        else
        {
            var tenantUser = await db.TenantUsers
                .IgnoreQueryFilters()
                .Include(tu => tu.Permissions)
                .FirstOrDefaultAsync(tu => tu.UserId == userId && tu.TenantId == tenantId);

            if (tenantUser == null)
            {
                logger.LogWarning("User {Username} ({UserId}) not found in TenantUsers for tenant {TenantId}", username, userId, tenantId);
            }
            else
            {
                permissions = tenantUser.Permissions
                    .Select(p => p.Permission.ToClaimValue())
                    .ToArray();
                tenantRole = tenantUser.Role.ToClaimValue();
                logger.LogInformation("User {Username} in tenant {TenantId} has {Count} permissions: {Permissions}",
                    username, tenantId, permissions.Length, string.Join(", ", permissions));
            }
        }

        // Check if user is SuperAdmin
        var isSuperAdmin = context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin);
        if (isSuperAdmin)
        {
            tenantRole = "SuperAdmin";
        }

        var config = await configRepository.GetConfigAsync();
        var response = new
        {
            Title = config.Title,
            Permissions = permissions,
            TenantRole = tenantRole,
            MainMapId = config.MainMapId,
            AllowGridUpdates = config.AllowGridUpdates,
            AllowNewMaps = config.AllowNewMaps
        };

        return Results.Json(response);
    }

    private static async Task<IResult> GetMaps(
        HttpContext context,
        IMapRepository mapRepository,
        IConfigRepository configRepository,
        HnHMapperServer.Api.Services.MapRevisionCache revisionCache)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Map permission already enforced by TenantMapAccess policy

        var config = await configRepository.GetConfigAsync();
        var maps = await mapRepository.GetAllMapsAsync();
        var visibleMaps = maps.Where(m => !m.Hidden)
            .OrderByDescending(m => m.Priority)  // Higher priority first (e.g., 1, 0, -1)
            .ThenBy(m => m.Name)
            .Select(m => new
            {
                ID = m.Id,
                MapInfo = new
                {
                    Name = m.Name,
                    Hidden = m.Hidden,
                    Priority = m.Priority,
                    Revision = revisionCache.Get(m.Id),  // Include current revision for initial cache setup
                    IsMainMap = config.MainMapId.HasValue && config.MainMapId.Value == m.Id,
                    DefaultStartX = m.DefaultStartX,
                    DefaultStartY = m.DefaultStartY
                },
                Size = 0  // Size not used in frontend, set to 0
            })
            .ToList();

        return Results.Json(visibleMaps);
    }

    private static async Task<IResult> WipeTile(
        HttpContext context,
        [FromBody] WipeTileRequest request,
        IGridService gridService,
        TileCacheService tileCacheService,
        IConfiguration configuration,
        HnHMapperServer.Api.Services.MapRevisionCache revisionCache,
        IUpdateNotificationService updateNotificationService,
        ILogger<Program> logger)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Require Writer permission (SuperAdmin bypasses automatically)
        if (!HasPermission(context.User, Permission.Writer))
            return Results.Unauthorized();

        var gridStorage = configuration["GridStorage"] ?? "map";
        await gridService.DeleteMapTileAsync(request.map, new Core.Models.Coord(request.x, request.y), gridStorage);

        // Bump map revision and notify clients to invalidate cache
        var newRevision = revisionCache.Increment(request.map);
        updateNotificationService.NotifyMapRevision(request.map, newRevision);
        logger.LogDebug("WipeTile: Bumped revision for map {MapId} to {Revision}", request.map, newRevision);

        // Invalidate tile cache for current tenant (tile was deleted - SSE clients need fresh data)
        var tenantId = context.Items["TenantId"] as string;
        await tileCacheService.InvalidateCacheAsync(tenantId);

        return Results.Ok();
    }

    private static async Task<IResult> SetCoords(
        HttpContext context,
        [FromBody] SetCoordsRequest request,
        IGridService gridService,
        TileCacheService tileCacheService,
        IConfiguration configuration,
        HnHMapperServer.Api.Services.MapRevisionCache revisionCache,
        IUpdateNotificationService updateNotificationService,
        ILogger<Program> logger)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Require Writer permission (SuperAdmin bypasses automatically)
        if (!HasPermission(context.User, Permission.Writer))
            return Results.Unauthorized();

        var gridStorage = configuration["GridStorage"] ?? "map";
        await gridService.SetCoordinatesAsync(
            request.map,
            new Core.Models.Coord(request.fx, request.fy),
            new Core.Models.Coord(request.tx, request.ty),
            gridStorage);

        // Bump map revision and notify clients to invalidate cache
        var newRevision = revisionCache.Increment(request.map);
        updateNotificationService.NotifyMapRevision(request.map, newRevision);
        logger.LogDebug("SetCoords: Bumped revision for map {MapId} to {Revision}", request.map, newRevision);

        // Invalidate tile cache for current tenant (coordinates changed - SSE clients need fresh data)
        var tenantId = context.Items["TenantId"] as string;
        await tileCacheService.InvalidateCacheAsync(tenantId);

        return Results.Ok();
    }

    private static async Task<IResult> HideMarker(
        HttpContext context,
        [FromBody] MarkerIdRequest request,
        IMarkerService markerService)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Require Writer permission (SuperAdmin bypasses automatically)
        if (!HasPermission(context.User, Permission.Writer))
            return Results.Unauthorized();

        await markerService.HideMarkerAsync(request.id);
        return Results.Ok();
    }

    private static async Task<IResult> DeleteMarker(
        HttpContext context,
        [FromBody] MarkerIdRequest request,
        IMarkerService markerService)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Require Writer permission (SuperAdmin bypasses automatically)
        if (!HasPermission(context.User, Permission.Writer))
            return Results.Unauthorized();

        await markerService.DeleteMarkerByIdAsync(request.id);
        return Results.Ok();
    }

    private static async Task WatchGridUpdates(
        HttpContext context,
        IUpdateNotificationService updateNotificationService,
        TileCacheService tileCacheService,
        ICharacterService characterService,
        HnHMapperServer.Api.Services.MapRevisionCache revisionCache,
        IMapRepository mapRepository,
        ILogger<Program> logger)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            context.Response.StatusCode = 401;
            return;
        }

        // Map permission already enforced by TenantMapAccess policy

        // Check if user has Pointer permission for character updates
        bool hasPointerAuth = HasPermission(context.User, Permission.Pointer);

        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        context.Response.Headers.Append("X-Accel-Buffering", "no");  // nginx
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Append("Connection", "keep-alive");
        context.Response.Headers.Append("Pragma", "no-cache");  // HTTP/1.0 proxies
        context.Response.Headers.Append("Expires", "0");

        // Disable Kestrel's MinResponseDataRate to prevent SSE connection timeout
        // Without this, Kestrel will abort connections that don't send ~240 bytes/second
        // SSE heartbeat sends only 15 bytes every 15 seconds, which would cause disconnection
        var minResponseDataRateFeature = context.Features.Get<IHttpMinResponseDataRateFeature>();
        if (minResponseDataRateFeature != null)
        {
            minResponseDataRateFeature.MinDataRate = null;
        }

        // Extract tenant ID from context (set by TenantContextMiddleware)
        var tenantId = context.Items["TenantId"] as string ?? string.Empty;
        if (string.IsNullOrEmpty(tenantId))
        {
            logger.LogWarning("SSE: No tenant ID in context, cannot send tile cache");
            context.Response.StatusCode = 401;
            return;
        }

        // Optional client-side resync optimization:
        // The web UI may reconnect its SSE EventSource when the tab is backgrounded/foregrounded.
        // Re-sending the entire tile cache (~300k entries) on every reconnect can:
        // - Create huge responses (tens of MB)
        // - Cause long blocking `JSON.parse(...)` on the browser main thread
        // - Lead to the "tab freezes for 30+ seconds" symptom
        //
        // To avoid that, the client can pass `?since=<lastSeenCache>` and we'll only send tiles that
        // have a Cache value greater than that number (i.e., changed since the last successful sync).
        //
        // NOTE: Cache is currently treated as a monotonically increasing timestamp-like value.
        // This is a pragmatic best-effort mechanism; if clients provide a too-new value, they may miss
        // updates that occurred while disconnected.
        // NOTE: Tile cache tokens are Unix milliseconds (long). Do NOT use int here.
        // The tile uploader uses DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() (~1.7e12),
        // which overflows int and would break delta reconnect (since would never filter).
        long sinceCache = 0;
        if (context.Request.Query.TryGetValue("since", out var sinceValues) &&
            long.TryParse(sinceValues.ToString(), out var parsedSince) &&
            parsedSince > 0)
        {
            sinceCache = parsedSince;
        }

        // Send initial tile cache (from tenant-specific in-memory cache - prevents blocking database query)
        // SECURITY: Tile cache is now tenant-scoped to prevent cross-tenant data leakage
        var tenantTiles = await tileCacheService.GetAllTilesAsync(tenantId);

        // If the client provided a `since` marker, only send tiles updated after that marker.
        // This dramatically reduces the initial payload on reconnects.
        if (sinceCache > 0)
        {
            tenantTiles = tenantTiles.Where(t => t.Cache > sinceCache).ToList();
            logger.LogInformation("SSE: Client requested tiles since {SinceCache} for tenant {TenantId} - sending {Count} changed tiles",
                sinceCache, tenantId, tenantTiles.Count);
        }

        // IMPORTANT PERFORMANCE NOTE:
        // The full initial tile cache can be very large (~300k entries). Serializing it as one giant JSON array
        // and sending it as a single SSE message forces the browser to do one huge `JSON.parse(...)`, which can
        // freeze the tab for many seconds.
        //
        // Even though we now support `?since=...` to avoid resending the full cache on reconnects, we still want
        // the initial sync (since=0) to be safe.
        //
        // Strategy:
        // - Stream the initial tile cache as multiple smaller SSE messages (default `data:` event).
        // - Each message contains a small JSON array chunk (e.g. 2k tiles).
        // - The client already treats default SSE messages as "tile updates batches", so this is compatible.
        const int initialTileChunkSize = 2000;
        var totalTileCount = tenantTiles.Count;

        logger.LogDebug("SSE: Sending {Count} tiles for tenant {TenantId} (since={Since})",
            totalTileCount, tenantId, sinceCache);

        if (totalTileCount > 0)
        {
            var chunk = new List<TileCacheDto>(initialTileChunkSize);
            foreach (var t in tenantTiles)
            {
                chunk.Add(new TileCacheDto
                {
                    M = t.MapId,
                    X = t.Coord.X,
                    Y = t.Coord.Y,
                    Z = t.Zoom,
                    // IMPORTANT: Cache is Unix milliseconds (long). Do not truncate to int.
                    T = t.Cache
                });

                if (chunk.Count >= initialTileChunkSize)
                {
                    var json = JsonSerializer.Serialize(chunk);
                    await context.Response.WriteAsync($"data: {json}\n\n");
                    await context.Response.Body.FlushAsync();
                    chunk.Clear();
                }
            }

            // Send any remaining tiles in the final chunk.
            if (chunk.Count > 0)
            {
                var json = JsonSerializer.Serialize(chunk);
                await context.Response.WriteAsync($"data: {json}\n\n");
                await context.Response.Body.FlushAsync();
            }
        }

        // Send initial map revisions so clients can display version and set cache-busting immediately
        // SECURITY: Filter by tenant's maps to prevent cross-tenant data leakage
        var tenantMaps = await mapRepository.GetAllMapsAsync();
        var tenantMapIds = tenantMaps.Select(m => m.Id).ToHashSet();
        var allRevisions = revisionCache.GetAll();
        var tenantRevisions = allRevisions.Where(kv => tenantMapIds.Contains(kv.Key)).ToList();

        logger.LogDebug("SSE: Sending {Count} map revisions for tenant {TenantId} (filtered from {Total} total cached)",
            tenantRevisions.Count, tenantId, allRevisions.Count);

        if (tenantRevisions.Count > 0)
        {
            var initialJsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            foreach (var kv in tenantRevisions)
            {
                var irJson = JsonSerializer.Serialize(new { MapId = kv.Key, Revision = kv.Value }, initialJsonOptions);
                await context.Response.WriteAsync($"event: mapRevision\ndata: {irJson}\n\n");
            }
            await context.Response.Body.FlushAsync();
        }

        // Send initial character snapshot if user has Pointer auth
        if (hasPointerAuth)
        {
            // Use tenant ID already extracted above
            var allCharacters = characterService.GetAllCharacters(tenantId);
            logger.LogDebug("SSE: Sending character snapshot for tenant {TenantId} with {Count} characters", tenantId, allCharacters.Count);
            if (allCharacters.Count > 0)
            {
                var first = allCharacters[0];
                logger.LogWarning("SSE: First character - Id={Id}, Name={Name}, Map={Map}, Pos=({X},{Y}), Type={Type}",
                    first.Id, first.Name, first.Map, first.Position.X, first.Position.Y, first.Type);
            }
            // Send characters in the same format as CharacterModel (nested Position)
            // to match what the frontend expects
            var snapshotJson = JsonSerializer.Serialize(allCharacters);
            logger.LogWarning("SSE: Snapshot JSON length: {Length}", snapshotJson.Length);
            await context.Response.WriteAsync($"event: charactersSnapshot\ndata: {snapshotJson}\n\n");
            await context.Response.Body.FlushAsync();
        }
        else
        {
            logger.LogWarning("SSE: User lacks Pointer auth, not sending character snapshot");
        }

        // Subscribe to updates
        var tileUpdates = updateNotificationService.SubscribeToTileUpdates();
        var mergeUpdates = updateNotificationService.SubscribeToMergeUpdates();
        var mapUpdates = updateNotificationService.SubscribeToMapUpdates();
        var mapDeletes = updateNotificationService.SubscribeToMapDeletes();
        var mapRevisions = updateNotificationService.SubscribeToMapRevisions();
        var customMarkerCreated = updateNotificationService.SubscribeToCustomMarkerCreated();
        var customMarkerUpdated = updateNotificationService.SubscribeToCustomMarkerUpdated();
        var customMarkerDeleted = updateNotificationService.SubscribeToCustomMarkerDeleted();
        var pingCreated = updateNotificationService.SubscribeToPingCreated();
        var pingDeleted = updateNotificationService.SubscribeToPingDeleted();
        var roadCreated = updateNotificationService.SubscribeToRoadCreated();
        var roadUpdated = updateNotificationService.SubscribeToRoadUpdated();
        var roadDeleted = updateNotificationService.SubscribeToRoadDeleted();
        var overlayUpdated = updateNotificationService.SubscribeToOverlayUpdated();
        var notificationCreated = updateNotificationService.SubscribeToNotificationCreated();
        var notificationRead = updateNotificationService.SubscribeToNotificationRead();
        var notificationDismissed = updateNotificationService.SubscribeToNotificationDismissed();
        var timerCreated = updateNotificationService.SubscribeToTimerCreated();
        var timerUpdated = updateNotificationService.SubscribeToTimerUpdated();
        var timerCompleted = updateNotificationService.SubscribeToTimerCompleted();
        var timerDeleted = updateNotificationService.SubscribeToTimerDeleted();
        var markerCreated = updateNotificationService.SubscribeToMarkerCreated();
        var markerUpdated = updateNotificationService.SubscribeToMarkerUpdated();
        var markerDeleted = updateNotificationService.SubscribeToMarkerDeleted();
        var characterDeltas = hasPointerAuth ? updateNotificationService.SubscribeToCharacterDelta() : null;

        var tileBatch = new List<TileCacheDto>();
        // Character delta coalescing: accumulate latest state per character ID for 500ms batching
        var characterDeltaBatch = new Dictionary<int, Character>(); // characterId -> latest state
        var characterDeletions = new HashSet<int>(); // characterIds to delete
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        // Use CamelCase serialization for SSE events to match API behavior and client expectations
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        logger.LogWarning("SSE: About to enter main event loop");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500)); // 500ms for character updates
            var idleTicks = 0; // number of ticks without any outgoing data
            int ticksSinceLastTileBatch = 0; // track ticks since last tile batch (tiles batched every 10s = 20 ticks)

            logger.LogWarning("SSE: Entering while loop, cancellation requested: {Cancelled}", cts.Token.IsCancellationRequested);

            while (!cts.Token.IsCancellationRequested)
            {
                // Wait for next tick (500ms) FIRST to prevent infinite loop when channels are empty
                // This guarantees a 500ms delay between iterations
                await timer.WaitForNextTickAsync(cts.Token);
                ticksSinceLastTileBatch++;

                // Check for tile updates
                // SECURITY: Filter by tenant to prevent cross-tenant data leakage
                while (tileUpdates.TryRead(out var tileData))
                {
                    if (tileData.TenantId == tenantId)
                    {
                        tileBatch.Add(new TileCacheDto
                        {
                            M = tileData.MapId,
                            X = tileData.Coord.X,
                            Y = tileData.Coord.Y,
                            Z = tileData.Zoom,
                            // IMPORTANT: Cache is Unix milliseconds (long). Do not truncate to int.
                            T = tileData.Cache
                        });
                    }
                }

                // Check for merge updates
                // SECURITY: Filter by tenant to prevent cross-tenant merge confusion
                while (mergeUpdates.TryRead(out var merge))
                {
                    if (merge.TenantId == tenantId)
                    {
                        var mergeJson = JsonSerializer.Serialize(merge, jsonOptions);
                        await context.Response.WriteAsync($"event: merge\ndata: {mergeJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for map metadata updates (rename, hidden, priority)
                // SECURITY: Filter by tenant to prevent cross-tenant map updates
                while (mapUpdates.TryRead(out var mapInfo))
                {
                    if (mapInfo.TenantId == tenantId)
                    {
                        var mapUpdateDto = new
                        {
                            Id = mapInfo.Id,
                            Name = mapInfo.Name,
                            Hidden = mapInfo.Hidden,
                            Priority = mapInfo.Priority
                        };
                        var mapJson = JsonSerializer.Serialize(mapUpdateDto, jsonOptions);
                        await context.Response.WriteAsync($"event: mapUpdate\ndata: {mapJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for map deletions
                // SECURITY: Filter by tenant - need to check if mapId belongs to tenant
                while (mapDeletes.TryRead(out var mapId))
                {
                    // Check if this map belongs to the tenant
                    if (tenantMapIds.Contains(mapId))
                    {
                        var deleteDto = new { Id = mapId };
                        var deleteJson = JsonSerializer.Serialize(deleteDto, jsonOptions);
                        await context.Response.WriteAsync($"event: mapDelete\ndata: {deleteJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for map revision updates (cache busting)
                // SECURITY: Filter by tenant - only send revisions for tenant's maps
                while (mapRevisions.TryRead(out var revision))
                {
                    if (tenantMapIds.Contains(revision.MapId))
                    {
                        var revisionDto = new { MapId = revision.MapId, Revision = revision.Revision };
                        var revisionJson = JsonSerializer.Serialize(revisionDto, jsonOptions);
                        await context.Response.WriteAsync($"event: mapRevision\ndata: {revisionJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for custom marker creation events
                // SECURITY: Filter by tenant to prevent cross-tenant marker visibility
                while (customMarkerCreated.TryRead(out var customMarker))
                {
                    if (customMarker.TenantId == tenantId)
                    {
                        var markerJson = JsonSerializer.Serialize(customMarker, jsonOptions);
                        await context.Response.WriteAsync($"event: customMarkerCreated\ndata: {markerJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for custom marker update events
                // SECURITY: Filter by tenant to prevent cross-tenant marker visibility
                while (customMarkerUpdated.TryRead(out var customMarkerUpdate))
                {
                    if (customMarkerUpdate.TenantId == tenantId)
                    {
                        var markerJson = JsonSerializer.Serialize(customMarkerUpdate, jsonOptions);
                        await context.Response.WriteAsync($"event: customMarkerUpdated\ndata: {markerJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for custom marker deletion events
                // SECURITY: Filter by tenant to prevent cross-tenant marker visibility
                while (customMarkerDeleted.TryRead(out var markerDeleteEvent))
                {
                    if (markerDeleteEvent.TenantId == tenantId)
                    {
                        var deleteDto = new { Id = markerDeleteEvent.Id };
                        var deleteJson = JsonSerializer.Serialize(deleteDto, jsonOptions);
                        await context.Response.WriteAsync($"event: customMarkerDeleted\ndata: {deleteJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for ping creation events
                // SECURITY: Filter by tenant to prevent cross-tenant ping visibility
                while (pingCreated.TryRead(out var ping))
                {
                    if (ping.TenantId == tenantId)
                    {
                        var pingJson = JsonSerializer.Serialize(ping, jsonOptions);
                        await context.Response.WriteAsync($"event: pingCreated\ndata: {pingJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for ping deletion events
                // SECURITY: Filter by tenant to prevent cross-tenant ping visibility
                while (pingDeleted.TryRead(out var pingDeleteEvent))
                {
                    if (pingDeleteEvent.TenantId == tenantId)
                    {
                        var deleteDto = new { Id = pingDeleteEvent.Id };
                        var deleteJson = JsonSerializer.Serialize(deleteDto, jsonOptions);
                        await context.Response.WriteAsync($"event: pingDeleted\ndata: {deleteJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for road creation events
                // SECURITY: Filter by tenant to prevent cross-tenant road visibility
                while (roadCreated.TryRead(out var road))
                {
                    if (road.TenantId == tenantId)
                    {
                        var roadJson = JsonSerializer.Serialize(road, jsonOptions);
                        await context.Response.WriteAsync($"event: roadCreated\ndata: {roadJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for road update events
                // SECURITY: Filter by tenant to prevent cross-tenant road visibility
                while (roadUpdated.TryRead(out var updatedRoad))
                {
                    if (updatedRoad.TenantId == tenantId)
                    {
                        var roadJson = JsonSerializer.Serialize(updatedRoad, jsonOptions);
                        await context.Response.WriteAsync($"event: roadUpdated\ndata: {roadJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for road deletion events
                // SECURITY: Filter by tenant to prevent cross-tenant road visibility
                while (roadDeleted.TryRead(out var roadDeleteEvent))
                {
                    if (roadDeleteEvent.TenantId == tenantId)
                    {
                        var deleteDto = new { Id = roadDeleteEvent.Id };
                        var deleteJson = JsonSerializer.Serialize(deleteDto, jsonOptions);
                        await context.Response.WriteAsync($"event: roadDeleted\ndata: {deleteJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for overlay update events
                // SECURITY: Filter by tenant to prevent cross-tenant overlay visibility
                while (overlayUpdated.TryRead(out var overlay))
                {
                    if (overlay.TenantId == tenantId)
                    {
                        var overlayJson = JsonSerializer.Serialize(overlay, jsonOptions);
                        await context.Response.WriteAsync($"event: overlayUpdated\ndata: {overlayJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for notification creation events
                // SECURITY: Filter by tenant and user to prevent cross-tenant notification visibility
                while (notificationCreated.TryRead(out var notification))
                {
                    // Only send notifications that are for this tenant AND (for this user OR broadcast to all users)
                    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
                    if (notification.TenantId == tenantId &&
                        (notification.UserId == null || notification.UserId == userId))
                    {
                        var notificationJson = JsonSerializer.Serialize(notification, jsonOptions);
                        await context.Response.WriteAsync($"event: notificationCreated\ndata: {notificationJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for notification read events
                while (notificationRead.TryRead(out var notificationId))
                {
                    var readDto = new { Id = notificationId };
                    var readJson = JsonSerializer.Serialize(readDto, jsonOptions);
                    await context.Response.WriteAsync($"event: notificationRead\ndata: {readJson}\n\n");
                    await context.Response.Body.FlushAsync();
                }

                // Check for notification dismissed events
                while (notificationDismissed.TryRead(out var dismissedId))
                {
                    var dismissDto = new { Id = dismissedId };
                    var dismissJson = JsonSerializer.Serialize(dismissDto, jsonOptions);
                    await context.Response.WriteAsync($"event: notificationDismissed\ndata: {dismissJson}\n\n");
                    await context.Response.Body.FlushAsync();
                }

                // Check for timer creation events
                // SECURITY: Filter by tenant to prevent cross-tenant timer visibility
                while (timerCreated.TryRead(out var timerEvent))
                {
                    if (timerEvent.TenantId == tenantId)
                    {
                        var timerJson = JsonSerializer.Serialize(timerEvent, jsonOptions);
                        await context.Response.WriteAsync($"event: timerCreated\ndata: {timerJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for timer update events
                // SECURITY: Filter by tenant to prevent cross-tenant timer visibility
                while (timerUpdated.TryRead(out var timerUpdate))
                {
                    if (timerUpdate.TenantId == tenantId)
                    {
                        var timerJson = JsonSerializer.Serialize(timerUpdate, jsonOptions);
                        await context.Response.WriteAsync($"event: timerUpdated\ndata: {timerJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for timer completion events
                // SECURITY: Filter by tenant to prevent cross-tenant timer visibility
                while (timerCompleted.TryRead(out var timerDone))
                {
                    if (timerDone.TenantId == tenantId)
                    {
                        var timerJson = JsonSerializer.Serialize(timerDone, jsonOptions);
                        await context.Response.WriteAsync($"event: timerCompleted\ndata: {timerJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for timer deletion events
                while (timerDeleted.TryRead(out var timerId))
                {
                    var deleteDto = new { Id = timerId };
                    var deleteJson = JsonSerializer.Serialize(deleteDto, jsonOptions);
                    await context.Response.WriteAsync($"event: timerDeleted\ndata: {deleteJson}\n\n");
                    await context.Response.Body.FlushAsync();
                }

                // Check for game marker creation events
                // SECURITY: Filter by tenant to prevent cross-tenant marker visibility
                while (markerCreated.TryRead(out var gameMarker))
                {
                    if (gameMarker.TenantId == tenantId)
                    {
                        var markerJson = JsonSerializer.Serialize(gameMarker, jsonOptions);
                        await context.Response.WriteAsync($"event: markerCreated\ndata: {markerJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for game marker update events
                // SECURITY: Filter by tenant to prevent cross-tenant marker visibility
                while (markerUpdated.TryRead(out var gameMarkerUpdate))
                {
                    if (gameMarkerUpdate.TenantId == tenantId)
                    {
                        var markerJson = JsonSerializer.Serialize(gameMarkerUpdate, jsonOptions);
                        await context.Response.WriteAsync($"event: markerUpdated\ndata: {markerJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for game marker deletion events
                // SECURITY: Filter by tenant to prevent cross-tenant marker visibility
                while (markerDeleted.TryRead(out var gameMarkerDelete))
                {
                    if (gameMarkerDelete.TenantId == tenantId)
                    {
                        var deleteJson = JsonSerializer.Serialize(gameMarkerDelete, jsonOptions);
                        await context.Response.WriteAsync($"event: markerDeleted\ndata: {deleteJson}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }

                // Check for character delta events (coalesce updates)
                if (characterDeltas != null)
                {
                    while (characterDeltas.TryRead(out var delta))
                    {
                        // SECURITY: Only process deltas for the current tenant
                        if (delta.TenantId != tenantId)
                        {
                            continue; // Skip deltas from other tenants
                        }

                        // Coalesce updates: convert CharacterDto to Character format
                        foreach (var update in delta.Updates)
                        {
                            var character = new Character
                            {
                                Id = update.Id,
                                Name = update.Name,
                                Map = update.Map,
                                Position = new Position(update.X, update.Y),
                                Type = update.Type,
                                Rotation = update.Rotation,
                                Speed = update.Speed,
                                Updated = DateTime.UtcNow,
                                TenantId = delta.TenantId  // Set TenantId from delta
                            };

                            // Diagnostic logging to trace position updates
                            logger.LogDebug("SSE: Character {CharId} delta - Pos({X},{Y}), DeltaTenant={DeltaTenant}, ConnTenant={ConnTenant}",
                                update.Id, update.X, update.Y, delta.TenantId, tenantId);

                            characterDeltaBatch[update.Id] = character;
                            // Remove from deletions if it was marked for deletion (resurrect)
                            characterDeletions.Remove(update.Id);
                        }

                        // Track deletions
                        foreach (var deletedId in delta.Deletions)
                        {
                            // Remove from updates if it was pending (delete wins)
                            characterDeltaBatch.Remove(deletedId);
                            characterDeletions.Add(deletedId);
                        }
                    }
                }

                bool sentData = false;

                // Send character deltas every 500ms if there are any
                if (characterDeltaBatch.Count > 0 || characterDeletions.Count > 0)
                {
                    logger.LogDebug("SSE: Sending character delta - {Updates} updates, {Deletions} deletions",
                        characterDeltaBatch.Count, characterDeletions.Count);
                    
                    // Send in the same format as initial snapshot (Character with nested Position)
                    var deltaPayload = new
                    {
                        Updates = characterDeltaBatch.Values.ToList(),
                        Deletions = characterDeletions.ToList()
                    };
                    var deltaJson = JsonSerializer.Serialize(deltaPayload);
                    await context.Response.WriteAsync($"event: characterDelta\ndata: {deltaJson}\n\n");
                    await context.Response.Body.FlushAsync();
                    
                    characterDeltaBatch.Clear();
                    characterDeletions.Clear();
                    sentData = true;
                }

                // Send batched tile updates every 10 seconds (20 x 500ms ticks)
                if (ticksSinceLastTileBatch >= 20)
                {
                    if (tileBatch.Count > 0)
                    {
                        var batchJson = JsonSerializer.Serialize(tileBatch);
                        await context.Response.WriteAsync($"data: {batchJson}\n\n");
                        await context.Response.Body.FlushAsync();
                        tileBatch.Clear();
                        sentData = true;
                    }
                    ticksSinceLastTileBatch = 0;
                }

                if (sentData)
                {
                    idleTicks = 0; // we sent data, reset idle counter
                }
                else
                {
                    // Heartbeat: keep the SSE connection alive even when there are no updates
                    // Send a comment event every ~5 seconds (10 x 500ms ticks)
                    // Reduced from 15s to 5s because VPNs/proxies often timeout idle connections at 10s
                    idleTicks++;
                    if (idleTicks % 10 == 0)
                    {
                        await context.Response.WriteAsync(": keep-alive\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }
            }

            logger.LogWarning("SSE: Exited while loop normally");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("SSE: Cancelled - {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in SSE stream /map/updates");
        }

        logger.LogWarning("SSE: WatchGridUpdates method exiting");
    }

    private static async Task<IResult> ServeGridTile(
        HttpContext context,
        [FromRoute] string path,
        ITileService tileService,
        IConfiguration configuration)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Map permission already enforced by TenantMapAccess policy

        // Extract tenant ID from context (set by token validation middleware)
        var tenantId = context.Items["TenantId"] as string;
        if (string.IsNullOrEmpty(tenantId))
        {
            return Results.Unauthorized();
        }

        // Parse path: {mapId}/{zoom}/{x}_{y}.png
        var parts = path.Split('/');
        if (parts.Length != 3)
            return Results.NotFound();

        if (!int.TryParse(parts[0], out var mapId))
            return Results.NotFound();

        if (!int.TryParse(parts[1], out var zoom))
            return Results.NotFound();

        var coordPart = parts[2].Replace(".png", "");
        var coords = coordPart.Split('_');
        if (coords.Length != 2)
            return Results.NotFound();

        if (!int.TryParse(coords[0], out var x))
            return Results.NotFound();

        if (!int.TryParse(coords[1], out var y))
            return Results.NotFound();

        var gridStorage = configuration["GridStorage"] ?? "map";

        string? filePath = null;

        // Performance optimization: only query DB for zoom 0 tiles (which may be stored under grids/{gridId}.png)
        // For zoom >= 1, tiles are always in the standard tenants/{tenantId}/{mapId}/{zoom}/{x}_{y}.png structure
        if (zoom == 0)
        {
            // Try to get tile from database first
            var tile = await tileService.GetTileAsync(mapId, new Core.Models.Coord(x, y), zoom);

            if (tile != null)
            {
                // SECURITY: Verify tile belongs to current tenant (defense-in-depth)
                if (tile.TenantId != tenantId)
                {
                    context.Response.StatusCode = 401;
                    return Results.Unauthorized();
                }

                if (!string.IsNullOrEmpty(tile.File))
                {
                    // Tile found in database - use stored file path (already tenant-specific after migration)
                    filePath = Path.Combine(gridStorage, tile.File);
                }
            }
            else
            {
                // Fallback to direct file system lookup for zoom 0
                // Tenant-specific path: tenants/{tenantId}/{mapId}/{zoom}/{x}_{y}.png
                var tenantPath = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{x}_{y}.png");
                if (File.Exists(tenantPath))
                {
                    filePath = tenantPath;
                }
            }
        }
        else
        {
            // For zoom >= 1, use tenant-specific path (generated tiles follow this pattern after Phase 4)
            // Expected format: tenants/{tenantId}/{mapId}/{zoom}/{x}_{y}.png
            var directPath = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{x}_{y}.png");
            if (File.Exists(directPath))
            {
                filePath = directPath;
            }
        }

        if (filePath == null || !File.Exists(filePath))
        {
            // Return 404 with cache to reduce repeated requests over unmapped areas (5 minutes)
            context.Response.Headers.Append("Cache-Control", "public, max-age=300, stale-while-revalidate=60");
            return Results.NotFound();
        }

        // Get file info for ETag and Last-Modified headers
        var fileInfo = new FileInfo(filePath);
        var lastModified = fileInfo.LastWriteTimeUtc;
        var fileSize = fileInfo.Length;
        
        // Generate ETag from file length + last write time (simple but effective)
        var etagValue = $"\"{fileSize}-{lastModified.Ticks}\"";
        
        // Check If-None-Match header (ETag conditional request)
        var ifNoneMatch = context.Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etagValue)
        {
            // Client has the same version, return 304 Not Modified
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.Headers.Append("ETag", etagValue);
            context.Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");
            return Results.Empty;
        }
        
        // Check If-Modified-Since header (date-based conditional request)
        var ifModifiedSince = context.Request.Headers["If-Modified-Since"].ToString();
        if (!string.IsNullOrEmpty(ifModifiedSince) && 
            DateTime.TryParse(ifModifiedSince, out var ifModifiedSinceDate) &&
            lastModified <= ifModifiedSinceDate.ToUniversalTime())
        {
            // File hasn't been modified, return 304 Not Modified
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.Headers.Append("Last-Modified", lastModified.ToString("R"));
            context.Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");
            return Results.Empty;
        }
        
        // Set caching headers for successful response
        context.Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");
        context.Response.Headers.Append("ETag", etagValue);
        context.Response.Headers.Append("Last-Modified", lastModified.ToString("R"));
        
        return Results.File(filePath, "image/png");
    }

    /// <summary>
    /// Serve map preview images for Discord webhook notifications.
    /// Public endpoint - no authentication required (Discord webhooks can't send auth headers).
    /// Preview ID format: {timestamp}_{mapId}_{coordX}_{coordY}.png
    /// Tenant isolation: Preview filename doesn't contain tenant ID, but validation is done via directory structure.
    /// </summary>
    private static async Task<IResult> ServePreviewImage(
        HttpContext context,
        [FromRoute] string previewId,
        IMapPreviewService previewService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MapPreviewEndpoint");

        // Validate preview ID format (prevent directory traversal attacks)
        if (string.IsNullOrWhiteSpace(previewId) ||
            previewId.Contains("..") ||
            previewId.Contains("/") ||
            previewId.Contains("\\") ||
            !previewId.EndsWith(".png"))
        {
            logger.LogWarning("Invalid preview ID requested: {PreviewId}", previewId);
            return Results.NotFound();
        }

        // Extract signature parameters from query string (HMAC-based security)
        var expires = context.Request.Query["expires"].ToString();
        var signature = context.Request.Query["sig"].ToString();

        if (string.IsNullOrWhiteSpace(expires) || string.IsNullOrWhiteSpace(signature))
        {
            logger.LogWarning("Preview request missing required signature parameters: {PreviewId}", previewId);
            return Results.Unauthorized();
        }

        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var gridStorage = configuration["GridStorage"];
        if (string.IsNullOrWhiteSpace(gridStorage))
        {
            gridStorage = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "map"));
        }
        else if (!Path.IsPathRooted(gridStorage))
        {
            gridStorage = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", gridStorage));
        }

        var previewsDir = Path.Combine(gridStorage, "previews");

        if (!Directory.Exists(previewsDir))
        {
            logger.LogDebug("Previews directory does not exist");
            return Results.NotFound();
        }

        // Search for preview file in all tenant directories
        string? foundPreviewPath = null;
        try
        {
            var tenantDirs = Directory.GetDirectories(previewsDir);
            foreach (var tenantDir in tenantDirs)
            {
                var candidatePath = Path.Combine(tenantDir, previewId);
                if (File.Exists(candidatePath))
                {
                    foundPreviewPath = candidatePath;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error searching for preview {PreviewId}", previewId);
            return Results.Problem("Internal server error");
        }

        if (foundPreviewPath == null)
        {
            logger.LogDebug("Preview not found: {PreviewId}", previewId);
            return Results.NotFound();
        }

        // Extract tenant ID from path for signature validation
        string tenantId;
        try
        {
            var tenantDir = Path.GetFileName(Path.GetDirectoryName(foundPreviewPath));
            if (string.IsNullOrWhiteSpace(tenantDir))
            {
                logger.LogWarning("Could not extract tenant ID from preview path: {Path}", foundPreviewPath);
                return Results.Unauthorized();
            }
            tenantId = tenantDir;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting tenant ID from preview path: {Path}", foundPreviewPath);
            return Results.Unauthorized();
        }

        // Look up tenant's Discord webhook URL and validate signature
        var db = context.RequestServices.GetRequiredService<ApplicationDbContext>();
        var signingService = context.RequestServices.GetRequiredService<IPreviewUrlSigningService>();

        string? webhookUrl = null;
        try
        {
            var tenant = await db.Tenants
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.DiscordWebhookUrl })
                .FirstOrDefaultAsync();

            if (tenant == null)
            {
                logger.LogWarning("Tenant not found for preview: {TenantId}", tenantId);
                return Results.Unauthorized();
            }

            webhookUrl = tenant.DiscordWebhookUrl;

            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                logger.LogWarning("No Discord webhook configured for tenant {TenantId}, cannot validate signature", tenantId);
                return Results.Unauthorized();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error looking up tenant webhook for signature validation");
            return Results.Unauthorized();
        }

        // Validate HMAC signature
        var isValidSignature = signingService.ValidateSignedUrl(previewId, expires, signature, webhookUrl);
        if (!isValidSignature)
        {
            logger.LogWarning("Invalid signature for preview {PreviewId}, tenant {TenantId}", previewId, tenantId);
            return Results.Unauthorized();
        }

        // Signature validated - serve with caching headers
        var fileInfo = new FileInfo(foundPreviewPath);
        var lastModified = fileInfo.LastWriteTimeUtc;
        var etagValue = $"\"{lastModified.Ticks}\"";

        // Check If-None-Match (ETag)
        var ifNoneMatch = context.Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etagValue)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.Headers.Append("ETag", etagValue);
            context.Response.Headers.Append("Cache-Control", "public, max-age=172800"); // 48 hours
            return Results.Empty;
        }

        // Check If-Modified-Since
        var ifModifiedSince = context.Request.Headers["If-Modified-Since"].ToString();
        if (!string.IsNullOrEmpty(ifModifiedSince) &&
            DateTime.TryParse(ifModifiedSince, out var ifModifiedSinceDate) &&
            lastModified <= ifModifiedSinceDate.ToUniversalTime())
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.Headers.Append("Last-Modified", lastModified.ToString("R"));
            context.Response.Headers.Append("Cache-Control", "public, max-age=172800"); // 48 hours
            return Results.Empty;
        }

        // Set caching headers for successful response (48 hours = 172800 seconds)
        context.Response.Headers.Append("Cache-Control", "public, max-age=172800");
        context.Response.Headers.Append("ETag", etagValue);
        context.Response.Headers.Append("Last-Modified", lastModified.ToString("R"));

        logger.LogDebug("Serving preview {PreviewId} ({Size}KB)", previewId, fileInfo.Length / 1024);

        return Results.File(foundPreviewPath, "image/png");
    }

    // Request DTOs for admin endpoints
    private record WipeTileRequest(int map, int x, int y);
    private record SetCoordsRequest(int map, int fx, int fy, int tx, int ty);
    private record MarkerIdRequest(int id);

    // Overlay offset endpoints
    private static async Task<IResult> GetOverlayOffset(
        [FromQuery] int currentMapId,
        [FromQuery] int overlayMapId,
        IOverlayOffsetRepository offsetRepository)
    {
        if (currentMapId <= 0 || overlayMapId <= 0)
            return Results.BadRequest("Invalid map IDs");

        var offset = await offsetRepository.GetOffsetAsync(currentMapId, overlayMapId);

        return offset.HasValue
            ? Results.Ok(new OverlayOffsetResponse(currentMapId, overlayMapId, offset.Value.offsetX, offset.Value.offsetY))
            : Results.Ok(new OverlayOffsetResponse(currentMapId, overlayMapId, 0, 0)); // Default to (0,0) if not found
    }

    private static async Task<IResult> SaveOverlayOffset(
        [FromBody] SaveOverlayOffsetRequest request,
        IOverlayOffsetRepository offsetRepository)
    {
        if (request.CurrentMapId <= 0 || request.OverlayMapId <= 0)
            return Results.BadRequest("Invalid map IDs");

        await offsetRepository.SaveOffsetAsync(
            request.CurrentMapId,
            request.OverlayMapId,
            request.OffsetX,
            request.OffsetY);

        return Results.NoContent();
    }

    // DTOs for overlay offset
    private record OverlayOffsetResponse(int CurrentMapId, int OverlayMapId, double OffsetX, double OffsetY);
    private record SaveOverlayOffsetRequest(int CurrentMapId, int OverlayMapId, double OffsetX, double OffsetY);

    /// <summary>
    /// Get grid IDs for tiles in a specified bounds.
    /// Used to display grid IDs on the map viewer.
    /// </summary>
    private static async Task<IResult> GetGridIds(
        HttpContext context,
        [FromQuery] int mapId,
        [FromQuery] int minX,
        [FromQuery] int maxX,
        [FromQuery] int minY,
        [FromQuery] int maxY,
        ApplicationDbContext db,
        ILogger<Program> logger)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Limit bounds to prevent excessive queries
        var maxRange = 50;
        if (maxX - minX > maxRange || maxY - minY > maxRange)
        {
            return Results.BadRequest($"Coordinate range too large. Maximum {maxRange} tiles per dimension.");
        }

        try
        {
            // Query grids within the bounds for the specified map
            // Global query filter automatically applies tenant isolation
            var grids = await db.Grids
                .AsNoTracking()
                .Where(g => g.Map == mapId &&
                           g.CoordX >= minX && g.CoordX <= maxX &&
                           g.CoordY >= minY && g.CoordY <= maxY)
                .Select(g => new { x = g.CoordX, y = g.CoordY, gridId = g.Id })
                .ToListAsync();

            return Results.Json(grids);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching grid IDs for map {MapId} bounds ({MinX},{MinY})-({MaxX},{MaxY})",
                mapId, minX, minY, maxX, maxY);
            return Results.Problem("Failed to fetch grid IDs");
        }
    }
}
