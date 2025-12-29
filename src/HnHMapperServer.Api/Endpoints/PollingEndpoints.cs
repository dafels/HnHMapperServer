using System.Security.Claims;
using HnHMapperServer.Api.Services;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Mvc;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Polling API endpoint as fallback for SSE when connections fail (e.g., VPN users).
/// Returns unified response with tiles, characters, and map revisions.
/// </summary>
public static class PollingEndpoints
{
    public static void MapPollingEndpoints(this IEndpointRouteBuilder app)
    {
        // Single polling endpoint that returns all real-time data
        app.MapGet("/map/api/v1/poll", PollUpdates)
            .RequireAuthorization("TenantMapAccess"); // Require Map permission
    }

    /// <summary>
    /// Unified polling endpoint returning tiles, characters, and map revisions.
    /// Supports delta updates via 'since' parameter to minimize payload.
    /// </summary>
    private static async Task<IResult> PollUpdates(
        HttpContext context,
        [FromQuery] long? since,  // Tile cache token for delta updates
        ICharacterService characterService,
        TileCacheService tileCacheService,
        MapRevisionCache revisionCache,
        IMapRepository mapRepository)
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        // Extract tenant ID from context (set by TenantContextMiddleware)
        var tenantId = context.Items["TenantId"] as string ?? string.Empty;
        if (string.IsNullOrEmpty(tenantId))
            return Results.Unauthorized();

        var response = new PollResponseDto
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // 1. Get tiles (with delta support)
        var tiles = await tileCacheService.GetAllTilesAsync(tenantId);

        // Filter to tiles changed since last poll if 'since' provided
        if (since.HasValue && since.Value > 0)
        {
            tiles = tiles.Where(t => t.Cache > since.Value).ToList();
        }

        response.Tiles = tiles.Select(t => new TileCacheDto
        {
            M = t.MapId,
            X = t.Coord.X,
            Y = t.Coord.Y,
            Z = t.Zoom,
            T = t.Cache
        }).ToList();

        // 2. Get characters if user has Pointer permission
        if (HasPermission(context.User, Permission.Pointer))
        {
            var characters = characterService.GetAllCharacters(tenantId);
            response.Characters = characters.Select(c => new CharacterDto
            {
                Id = c.Id,
                Name = c.Name,
                Map = c.Map,
                X = c.Position.X,
                Y = c.Position.Y,
                Type = c.Type,
                Rotation = c.Rotation,
                Speed = c.Speed
            }).ToList();
        }

        // 3. Get map revisions (only for maps that exist)
        var maps = await mapRepository.GetAllMapsAsync();
        var mapIds = maps.Select(m => m.Id).ToHashSet();
        var allRevisions = revisionCache.GetAll();

        response.MapRevisions = allRevisions
            .Where(kv => mapIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return Results.Json(response);
    }

    private static bool HasPermission(ClaimsPrincipal user, Permission permission)
    {
        // SuperAdmin bypasses all permission checks
        if (user.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
            return true;

        // Check for specific permission claim
        return user.HasClaim(AuthorizationConstants.ClaimTypes.TenantPermission, permission.ToString());
    }
}
