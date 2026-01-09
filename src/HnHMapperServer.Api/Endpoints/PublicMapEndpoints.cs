using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Public map endpoints - no authentication required
/// Serves pre-generated tiles from public/{slug}/
/// </summary>
public static class PublicMapEndpoints
{
    public static void MapPublicMapEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/public")
            .AllowAnonymous()
            .WithTags("Public Maps");

        // Serve pre-generated tiles
        group.MapGet("/{slug}/tiles/{**path}", ServePublicTile)
            .CacheOutput(policy => policy
                .Expire(TimeSpan.FromSeconds(60))
                .SetVaryByRouteValue("slug", "path")
                .SetVaryByQuery("v")
                .Tag("public-tiles"))
            .WithName("GetPublicTile")
            .WithSummary("Serve a pre-generated public map tile");

        // Get public map bounds/info
        group.MapGet("/{slug}/info", GetPublicMapInfo)
            .CacheOutput(policy => policy
                .Expire(TimeSpan.FromSeconds(60))
                .SetVaryByRouteValue("slug"))
            .WithName("GetPublicMapInfo")
            .WithSummary("Get public map bounds and metadata");

        // Get public map thingwall markers
        group.MapGet("/{slug}/markers", GetPublicMapMarkers)
            .CacheOutput(policy => policy
                .Expire(TimeSpan.FromSeconds(60))
                .SetVaryByRouteValue("slug"))
            .WithName("GetPublicMapMarkers")
            .WithSummary("Get thingwall markers for a public map");

        // List active public maps
        group.MapGet("/", ListActivePublicMaps)
            .CacheOutput(policy => policy
                .Expire(TimeSpan.FromSeconds(60)))
            .WithName("ListActivePublicMaps")
            .WithSummary("List all active public maps");
    }

    private static async Task<IResult> ServePublicTile(
        HttpContext context,
        [FromRoute] string slug,
        [FromRoute] string path,
        IPublicMapService publicMapService,
        IConfiguration configuration,
        ILogger<Program> logger)
    {
        // Verify public map exists and is active
        var publicMap = await publicMapService.GetPublicMapAsync(slug);
        if (publicMap == null || !publicMap.IsActive)
        {
            return Results.NotFound(new { error = "Public map not found" });
        }

        // Parse path: {zoom}/{x}_{y}.png
        var parts = path.Split('/');
        if (parts.Length != 2)
            return Results.NotFound();

        if (!int.TryParse(parts[0], out var zoom))
            return Results.NotFound();

        var coordPart = parts[1].Replace(".png", "");
        var coords = coordPart.Split('_');
        if (coords.Length != 2)
            return Results.NotFound();

        if (!int.TryParse(coords[0], out var x))
            return Results.NotFound();

        if (!int.TryParse(coords[1], out var y))
            return Results.NotFound();

        var gridStorage = configuration["GridStorage"] ?? "map";

        // Build file path: public/{slug}/{zoom}/{x}_{y}.png
        var filePath = Path.Combine(gridStorage, "public", slug, zoom.ToString(), $"{x}_{y}.png");

        if (!File.Exists(filePath))
        {
            // Return 404 with cache to reduce repeated requests (5 minutes)
            context.Response.Headers.Append("Cache-Control", "public, max-age=300, stale-while-revalidate=60");
            return Results.NotFound();
        }

        // Get file info for ETag and Last-Modified headers
        var fileInfo = new FileInfo(filePath);
        var lastModified = fileInfo.LastWriteTimeUtc;
        var fileSize = fileInfo.Length;

        // Generate ETag from file length + last write time
        var etagValue = $"\"{fileSize}-{lastModified.Ticks}\"";

        // Check If-None-Match header (ETag conditional request)
        var ifNoneMatch = context.Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etagValue)
        {
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

    private static async Task<IResult> GetPublicMapInfo(
        [FromRoute] string slug,
        IPublicMapService publicMapService)
    {
        var bounds = await publicMapService.GetBoundsAsync(slug);
        if (bounds == null)
        {
            return Results.NotFound(new { error = "Public map not found" });
        }

        return Results.Ok(bounds);
    }

    private static async Task<IResult> GetPublicMapMarkers(
        [FromRoute] string slug,
        IPublicMapService publicMapService,
        IConfiguration configuration)
    {
        // Verify public map exists and is active
        var publicMap = await publicMapService.GetPublicMapAsync(slug);
        if (publicMap == null || !publicMap.IsActive)
        {
            return Results.NotFound(new { error = "Public map not found" });
        }

        var gridStorage = configuration["GridStorage"] ?? "map";
        var markersPath = Path.Combine(gridStorage, "public", slug, "markers.json");

        if (!File.Exists(markersPath))
        {
            // Return empty array if no markers file exists (backwards compatibility)
            return Results.Ok(Array.Empty<object>());
        }

        // Read and return markers JSON directly
        var json = await File.ReadAllTextAsync(markersPath);
        return Results.Content(json, "application/json");
    }

    private static async Task<IResult> ListActivePublicMaps(
        IPublicMapService publicMapService)
    {
        var maps = await publicMapService.GetActivePublicMapsAsync();

        // Return simplified list for public consumption
        var result = maps.Select(m => new
        {
            m.Id,
            m.Name,
            Url = $"/public/{m.Id}",
            m.MinX,
            m.MaxX,
            m.MinY,
            m.MaxY,
            HasBounds = m.MinX.HasValue && m.MaxX.HasValue && m.MinY.HasValue && m.MaxY.HasValue
        });

        return Results.Ok(result);
    }
}
