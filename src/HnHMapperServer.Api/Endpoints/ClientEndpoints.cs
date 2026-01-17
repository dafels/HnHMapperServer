using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Client endpoints for game client communication
/// Matches Go implementation routes: /client/{token}/*
/// </summary>
public static partial class ClientEndpoints
{
    private const string VERSION = "4";

    // Security: Maximum tile file size (1 MB)
    private const int MaxTileSizeBytes = 1_048_576;

    // Security: Expected tile dimensions
    private const int ExpectedTileWidth = 100;
    private const int ExpectedTileHeight = 100;

    /// <summary>
    /// Validates that the byte array starts with PNG magic bytes.
    /// PNG signature: 89 50 4E 47 0D 0A 1A 0A
    /// </summary>
    private static bool IsValidPng(byte[] data)
    {
        if (data.Length < 8) return false;
        return data[0] == 0x89 && data[1] == 0x50 &&
               data[2] == 0x4E && data[3] == 0x47 &&
               data[4] == 0x0D && data[5] == 0x0A &&
               data[6] == 0x1A && data[7] == 0x0A;
    }

    /// <summary>
    /// Logs a rejected upload to the audit service.
    /// </summary>
    private static async Task AuditRejectedUploadAsync(
        IAuditService auditService,
        string tenantId,
        string? userId,
        string gridId,
        string reason,
        string? threatName,
        string? fileName,
        long fileSizeBytes,
        string? scanEngine = null)
    {
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = "UploadRejected",
            EntityType = "GridTile",
            EntityId = gridId,
            NewValue = JsonSerializer.Serialize(new
            {
                Reason = reason,
                ThreatName = threatName,
                FileName = fileName,
                FileSizeBytes = fileSizeBytes,
                ScanEngine = scanEngine
            })
        });
    }

    public static void MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        // Phase 7: Apply per-tenant rate limiting to all client endpoints
        var group = app.MapGroup("/client/{token}")
            .RequireRateLimiting("PerTenant");

        group.MapPost("/checkVersion", CheckVersion).DisableAntiforgery();
        group.MapGet("/checkVersion", CheckVersion); // Support Ender client (uses GET)
        group.MapGet("/locate", Locate);
        group.MapPost("/gridUpdate", GridUpdate).DisableAntiforgery();

        // Phase 7: Additional stricter rate limit for tile uploads (20/min)
        group.MapPost("/gridUpload", GridUpload)
            .DisableAntiforgery()
            .RequireRateLimiting("TileUpload");

        // Overlay upload (same rate limit as gridUpload)
        group.MapPost("/overlayUpload", OverlayUpload)
            .DisableAntiforgery()
            .RequireRateLimiting("TileUpload");

        group.MapPost("/positionUpdate", PositionUpdate).DisableAntiforgery();
        group.MapPost("/markerBulkUpload", MarkerBulkUpload).DisableAntiforgery();
        group.MapPost("/markerDelete", MarkerDelete).DisableAntiforgery();
        group.MapPost("/markerUpdate", MarkerUpdate).DisableAntiforgery();
        group.MapPost("/markerReadyTime", MarkerReadyTime).DisableAntiforgery();
        group.MapGet("", RedirectToMap);
    }

    private static IResult RedirectToMap()
    {
        return Results.Redirect("/map/");
    }

    private static IResult CheckVersion([FromRoute] string token, [FromQuery] string version)
    {
        return version == VERSION ? Results.Ok() : Results.BadRequest();
    }

    private static async Task<IResult> Locate(
        [FromRoute] string token,
        [FromQuery] string gridID,
        HttpContext context,
        ApplicationDbContext db,
        ITokenService tokenService,
        IGridService gridService,
        ILogger<Program> logger)
    {
        if (!await ClientTokenHelpers.HasUploadAsync(context, db, tokenService, token, logger))
            return Results.Unauthorized();

        var location = await gridService.LocateGridAsync(gridID);
        if (location == null)
            return Results.NotFound();

        var (mapId, coord) = location.Value;
        return Results.Text($"{mapId};{coord.X};{coord.Y}");
    }

    private static async Task<IResult> GridUpdate(
        [FromRoute] string token,
        HttpContext context,
        ApplicationDbContext db,
        ITokenService tokenService,
        IGridService gridService,
        IConfiguration configuration,
        ITenantActivityService activityService,
        ILogger<Program> logger)
    {
        if (!await ClientTokenHelpers.HasUploadAsync(context, db, tokenService, token, logger))
            return Results.Unauthorized();

        // Record tenant activity
        var tenantId = context.Items["TenantId"] as string;
        if (!string.IsNullOrEmpty(tenantId))
            activityService.RecordActivity(tenantId);

        // Read raw JSON and deserialize with case-insensitive options (Go client sends lowercase)
        var gridUpdate = await context.Request.ReadFromJsonAsync<GridUpdateDto>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (gridUpdate == null)
            return Results.BadRequest("Invalid grid update payload");

        var gridStorage = configuration["GridStorage"] ?? "map";

        GridRequestDto response;
        try
        {
            response = await gridService.ProcessGridUpdateAsync(gridUpdate, gridStorage);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("disabled"))
        {
            return Results.Json(new { error = ex.Message }, statusCode: 403);
        }


        return Results.Json(response);
    }

    private static async Task<IResult> GridUpload(
        [FromRoute] string token,
        HttpContext context,
        ApplicationDbContext db,
        ITokenService tokenService,
        IGridRepository gridRepository,
        ITileService tileService,
        TileCacheService tileCacheService,
        IConfiguration configuration,
        IStorageQuotaService quotaService,
        ITenantFilePathService filePathService,
        ITenantActivityService activityService,
        IConfigRepository configRepository,
        ILargeTileService largeTileService,
        IAuditService auditService,
        ILogger<Program> logger)
    {
        if (!await ClientTokenHelpers.HasUploadAsync(context, db, tokenService, token, logger))
            return Results.Unauthorized();

        // Get tenant ID from context (set by token validation)
        var tenantId = context.Items["TenantId"] as string;
        if (string.IsNullOrEmpty(tenantId))
        {
            logger.LogError("GridUpload: TenantId not found in context");
            return Results.Unauthorized();
        }

        // Get config for later use
        var config = await configRepository.GetConfigAsync();

        // Record tenant activity
        activityService.RecordActivity(tenantId);

        var request = context.Request;

        if (!request.HasFormContentType)
            return Results.BadRequest("Expected multipart/form-data");

        var form = await request.ReadFormAsync();
        var id = form["id"].ToString();
        var extraData = form["extraData"].ToString();

        // Check for winter season skip logic
        if (!string.IsNullOrEmpty(extraData))
        {
            try
            {
                var ed = JsonSerializer.Deserialize<Dictionary<string, object>>(extraData);
                // Case-insensitive lookup for "season" field (Ender client sends lowercase)
                object? season = null;
                if (ed != null)
                {
                    foreach (var kvp in ed)
                    {
                        if (kvp.Key.Equals("Season", StringComparison.OrdinalIgnoreCase))
                        {
                            season = kvp.Value;
                            break;
                        }
                    }
                }

                if (season != null)
                {
                    var seasonInt = Convert.ToInt32(season.ToString());
                    if (seasonInt == 3)
                    {
                        var winterGrid = await gridRepository.GetGridAsync(id);
                        if (winterGrid != null)
                        {
                            var tile = await tileService.GetTileAsync(winterGrid.Map, winterGrid.Coord, 0);
                            if (tile != null && !string.IsNullOrEmpty(tile.File))
                            {
                                // Already have tile, skip winter upload
                                if (DateTime.UtcNow < winterGrid.NextUpdate)
                                {
                                    logger.LogInformation("Ignoring tile upload: winter");
                                    return Results.Ok();
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        var file = form.Files["file"];
        if (file == null)
            return Results.BadRequest("No file provided");

        // Get userId for audit logging
        var userId = context.Items["UserId"] as string;

        // --- SECURITY: Per-file size limit (1 MB) ---
        if (file.Length > MaxTileSizeBytes)
        {
            logger.LogWarning(
                "Security: Upload rejected for tenant {TenantId}, grid {GridId}. Reason: FileTooLarge, Size: {Size} bytes",
                tenantId, id, file.Length);

            await AuditRejectedUploadAsync(auditService, tenantId, userId, id,
                "FileTooLarge", null, file.FileName, file.Length);

            return Results.Json(new
            {
                error = "File too large",
                detail = $"Tile exceeds maximum size limit of {MaxTileSizeBytes / 1024} KB. Uploaded: {file.Length / 1024} KB."
            }, statusCode: 400);
        }

        // Read file bytes for validation
        byte[] fileBytes;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }

        // --- SECURITY: PNG magic byte validation ---
        if (!IsValidPng(fileBytes))
        {
            logger.LogWarning(
                "Security: Upload rejected for tenant {TenantId}, grid {GridId}. Reason: InvalidPng",
                tenantId, id);

            await AuditRejectedUploadAsync(auditService, tenantId, userId, id,
                "InvalidPng", null, file.FileName, file.Length);

            return Results.Json(new
            {
                error = "Invalid file format",
                detail = "File must be a valid PNG image."
            }, statusCode: 400);
        }

        // --- SECURITY: Image dimension validation & re-encoding ---
        byte[] sanitizedBytes;
        try
        {
            using var image = Image.Load(fileBytes);

            // Validate dimensions
            if (image.Width != ExpectedTileWidth || image.Height != ExpectedTileHeight)
            {
                logger.LogWarning(
                    "Security: Upload rejected for tenant {TenantId}, grid {GridId}. Reason: InvalidDimensions, Dimensions: {Width}x{Height}",
                    tenantId, id, image.Width, image.Height);

                await AuditRejectedUploadAsync(auditService, tenantId, userId, id,
                    "InvalidDimensions", $"{image.Width}x{image.Height}", file.FileName, file.Length);

                return Results.Json(new
                {
                    error = "Invalid tile dimensions",
                    detail = $"Tile must be {ExpectedTileWidth}x{ExpectedTileHeight} pixels. Uploaded: {image.Width}x{image.Height}."
                }, statusCode: 400);
            }

            // Re-encode to strip any embedded payloads (polyglot prevention)
            using var cleanStream = new MemoryStream();
            image.SaveAsPng(cleanStream);
            sanitizedBytes = cleanStream.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Security: Upload rejected for tenant {TenantId}, grid {GridId}. Reason: ImageProcessingFailed",
                tenantId, id);

            await AuditRejectedUploadAsync(auditService, tenantId, userId, id,
                "ImageProcessingFailed", ex.Message, file.FileName, file.Length);

            return Results.Json(new
            {
                error = "Invalid image",
                detail = "Failed to process the uploaded image. Ensure it is a valid PNG file."
            }, statusCode: 400);
        }

        var grid = await gridRepository.GetGridAsync(id);
        if (grid == null)
        {
            // Grid doesn't exist for current tenant
            return Results.BadRequest($"Unknown grid id: {id}");
        }

        // Check if tile updates are allowed (only block if tile already exists)
        if (!config.AllowGridUpdates)
        {
            var existingTile = await tileService.GetTileAsync(grid.Map, grid.Coord, 0);
            if (existingTile != null && !string.IsNullOrEmpty(existingTile.File))
            {
                logger.LogWarning("GridUpload: Tile update blocked for grid {GridId} - updates disabled for tenant {TenantId}", id, tenantId);
                return Results.Json(new { error = "Tile updates are disabled for this tenant" }, statusCode: 403);
            }
            // No existing tile - allow new tile upload
        }

        var gridStorage = configuration["GridStorage"] ?? "map";
        var updateTile = DateTime.UtcNow > grid.NextUpdate;

        if (updateTile)
        {
            // Use sanitized bytes size for quota calculation
            var fileSizeMB = sanitizedBytes.Length / 1024.0 / 1024.0;
            var canUpload = await quotaService.CheckQuotaAsync(tenantId, fileSizeMB);

            if (!canUpload)
            {
                var currentUsage = await quotaService.GetCurrentUsageAsync(tenantId);
                var quotaLimit = await quotaService.GetQuotaLimitAsync(tenantId);

                logger.LogWarning(
                    "GridUpload: Quota exceeded for tenant {TenantId}. Current: {Current}MB, Quota: {Quota}MB, Upload: {Upload}MB",
                    tenantId, currentUsage, quotaLimit, fileSizeMB);

                await AuditRejectedUploadAsync(auditService, tenantId, userId, id,
                    "QuotaExceeded", null, file.FileName, sanitizedBytes.Length);

                return Results.Json(new
                {
                    error = "Storage quota exceeded",
                    detail = $"Your tenant has reached its storage limit. Current usage: {currentUsage:F2} MB / {quotaLimit} MB. Attempted upload: {fileSizeMB:F2} MB."
                }, statusCode: 413);
            }

            grid.NextUpdate = DateTime.UtcNow.AddMinutes(30);
            await gridRepository.SaveGridAsync(grid);

            // Ensure tenant directories exist
            filePathService.EnsureTenantDirectoriesExist(tenantId, gridStorage);

            // Use tenant-specific file path
            var filePath = filePathService.GetGridFilePath(tenantId, grid.Id, gridStorage);
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                Directory.CreateDirectory(fileDir);
            }

            // Save sanitized file and increment quota atomically
            using (var transaction = await db.Database.BeginTransactionAsync())
            {
                try
                {
                    // Save sanitized (re-encoded) file to disk
                    await File.WriteAllBytesAsync(filePath, sanitizedBytes);

                    // Increment storage usage
                    await quotaService.IncrementStorageUsageAsync(tenantId, fileSizeMB);

                    await transaction.CommitAsync();

                    logger.LogDebug(
                        "GridUpload: Saved tile for tenant {TenantId}, grid {GridId}, size {SizeMB:F2}MB",
                        tenantId, grid.Id, fileSizeMB);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    logger.LogError(ex, "GridUpload: Failed to save tile for tenant {TenantId}, grid {GridId}", tenantId, grid.Id);

                    // Clean up file if it was created
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            File.Delete(filePath);
                        }
                        catch { }
                    }

                    throw;
                }
            }

            var relativePath = filePathService.GetGridRelativePath(tenantId, grid.Id);
            var fileSizeBytes = sanitizedBytes.Length;
            await tileService.SaveTileAsync(
                grid.Map,
                grid.Coord,
                0,
                relativePath,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                tenantId,
                fileSizeBytes);

            // Mark the corresponding large tile as dirty for regeneration
            await largeTileService.MarkDirtyAsync(tenantId, grid.Map, grid.Coord.X, grid.Coord.Y);

            // Update zoom levels
            var c = grid.Coord;
            for (int z = 1; z <= 6; z++)
            {
                c = c.Parent();
                await tileService.UpdateZoomLevelAsync(grid.Map, c, z, tenantId, gridStorage);
            }

            await tileCacheService.InvalidateCacheAsync(tenantId);
        }

        return Results.Ok();
    }

    /// <summary>
    /// Upload overlay data for a grid (claims, villages, provinces)
    /// </summary>
    private static async Task<IResult> OverlayUpload(
        [FromRoute] string token,
        HttpContext context,
        ApplicationDbContext db,
        ITokenService tokenService,
        IGridRepository gridRepository,
        IOverlayDataRepository overlayRepository,
        IUpdateNotificationService updateNotificationService,
        ILogger<Program> logger)
    {
        // 1. Validate token
        if (!await ClientTokenHelpers.HasUploadAsync(context, db, tokenService, token, logger))
            return Results.Unauthorized();

        var tenantId = context.Items["TenantId"] as string;
        if (string.IsNullOrEmpty(tenantId))
        {
            logger.LogError("OverlayUpload: TenantId not found in context");
            return Results.Unauthorized();
        }

        // 2. Parse request
        var request = await context.Request.ReadFromJsonAsync<OverlayUploadDto>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (request == null || string.IsNullOrEmpty(request.GridId))
        {
            logger.LogWarning("OverlayUpload: Invalid payload - missing gridId");
            return Results.BadRequest("Invalid overlay upload payload");
        }

        // 3. Look up grid to get MapId and Coord
        var grid = await gridRepository.GetGridAsync(request.GridId);
        if (grid == null)
        {
            logger.LogWarning("OverlayUpload: Unknown grid id {GridId}", request.GridId);
            return Results.BadRequest($"Unknown grid id: {request.GridId}");
        }

        // 4. Convert and save overlays
        var overlays = new List<OverlayData>();
        foreach (var item in request.Overlays)
        {
            var overlayType = ParseOverlayType(item.ResourceName);
            if (overlayType == null)
            {
                logger.LogDebug("OverlayUpload: Skipping unknown overlay type from resource {ResourceName}", item.ResourceName);
                continue;
            }

            overlays.Add(new OverlayData
            {
                MapId = grid.Map,
                Coord = grid.Coord,
                OverlayType = overlayType,
                Data = TilesToBitpack(item.Tiles),
                TenantId = tenantId,
                UpdatedAt = DateTime.UtcNow
            });
        }

        if (overlays.Count > 0)
        {
            await overlayRepository.UpsertBatchAsync(overlays);

            logger.LogDebug("OverlayUpload: Saved {Count} overlays for grid {GridId} on map {MapId}",
                overlays.Count, request.GridId, grid.Map);

            // 5. Notify SSE clients
            foreach (var overlay in overlays)
            {
                updateNotificationService.NotifyOverlayUpdated(new OverlayEventDto
                {
                    MapId = overlay.MapId,
                    CoordX = overlay.Coord.X,
                    CoordY = overlay.Coord.Y,
                    OverlayType = overlay.OverlayType,
                    TenantId = tenantId
                });
            }
        }

        return Results.Ok();
    }

    /// <summary>
    /// Convert array of tile indices (0-9999) to bitpacked byte array (1250 bytes)
    /// </summary>
    private static byte[] TilesToBitpack(List<int> tiles)
    {
        var data = new byte[1250];  // 100*100/8 = 1250 bytes
        foreach (var tileIndex in tiles)
        {
            if (tileIndex < 0 || tileIndex >= 10000) continue;
            var byteIndex = tileIndex / 8;
            var bitOffset = tileIndex % 8;
            data[byteIndex] |= (byte)(1 << bitOffset);
        }
        return data;
    }

    /// <summary>
    /// Parse overlay type from resource name (e.g., "gfx/tiles/overlay/cplot-f" -> "ClaimFloor")
    /// </summary>
    private static string? ParseOverlayType(string resourceName)
    {
        if (string.IsNullOrEmpty(resourceName))
            return null;

        // Normalize path separators and get filename
        var normalized = resourceName.Replace('\\', '/').ToLowerInvariant();
        var parts = normalized.Split('/');
        var filename = parts.Length > 0 ? parts[^1] : normalized;

        return filename switch
        {
            "cplot-f" => "ClaimFloor",
            "cplot-o" => "ClaimOutline",
            "vlg-f" => "VillageFloor",
            "vlg-o" => "VillageOutline",
            "vlg-sar" => "VillageSAR",
            "0" when parts.Length > 1 && parts[^2] == "prov" => "Province0",
            "1" when parts.Length > 1 && parts[^2] == "prov" => "Province1",
            "2" when parts.Length > 1 && parts[^2] == "prov" => "Province2",
            "3" when parts.Length > 1 && parts[^2] == "prov" => "Province3",
            "4" when parts.Length > 1 && parts[^2] == "prov" => "Province4",
            _ => null
        };
    }

    private static async Task<IResult> PositionUpdate(
        [FromRoute] string token,
        HttpContext context,
        ApplicationDbContext db,
        ITokenService tokenService,
        IGridRepository gridRepository,
        ICharacterService characterService,
        ITenantActivityService activityService,
        ILogger<Program> logger)
    {
        if (!await ClientTokenHelpers.HasUploadAsync(context, db, tokenService, token, logger))
            return Results.Unauthorized();

        // Record tenant activity
        var tenantId = context.Items["TenantId"] as string;
        if (!string.IsNullOrEmpty(tenantId))
            activityService.RecordActivity(tenantId);

        // Read raw JSON with case-insensitive options (Go client sends lowercase: coords, gridID, name, etc.)
        var positions = await context.Request.ReadFromJsonAsync<Dictionary<string, Dictionary<string, object>>>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (positions == null)
            return Results.BadRequest("Invalid position update payload");

        // Diagnostics: track processing stats
        int total = 0, processed = 0, skippedMissingGrid = 0, skippedMissingCoords = 0;
        var unknownGrids = new HashSet<string>();
        int loggedMissingCoords = 0;
        int loggedMissingGrid = 0;

        foreach (var (id, data) in positions)
        {
            total++;

            // Helper: case-insensitive key lookup in dictionary
            object? GetValue(string key)
            {
                foreach (var kvp in data)
                {
                    if (kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return kvp.Value;
                }
                return null;
            }

            // Safely get values from dictionary, handling missing keys (case-insensitive)
            var name = GetValue("Name")?.ToString() ?? "";
            var gridId = GetValue("GridID")?.ToString() ?? "";
            var coordsObj = GetValue("Coords");
            var coords = coordsObj as JsonElement?;
            var type = GetValue("Type")?.ToString() ?? "";
            var rotation = Convert.ToInt32(GetValue("Rotation")?.ToString() ?? "0");
            var speed = Convert.ToInt32(GetValue("Speed")?.ToString() ?? "0");

            if (coords == null)
            {
                skippedMissingCoords++;
                if (loggedMissingCoords < 5)
                {
                    // Log available keys to help diagnose payload structure
                    var availableKeys = string.Join(", ", data.Keys);
                    logger.LogWarning("PositionUpdate: Skipping character ID={CharacterId} Name={Name} GridID={GridId} - missing Coords field. Available keys in payload: [{Keys}]. Expected: Coords={{X,Y}}", id, name, gridId, availableKeys);
                    loggedMissingCoords++;
                }
                continue;
            }

            // Extract X and Y from coords (case-insensitive: try "X" then "x", "Y" then "y")
            int x = 0, y = 0;
            try
            {
                if (coords.Value.TryGetProperty("X", out var xProp))
                    x = xProp.GetInt32();
                else if (coords.Value.TryGetProperty("x", out var xPropLower))
                    x = xPropLower.GetInt32();

                if (coords.Value.TryGetProperty("Y", out var yProp))
                    y = yProp.GetInt32();
                else if (coords.Value.TryGetProperty("y", out var yPropLower))
                    y = yPropLower.GetInt32();
            }
            catch (Exception ex)
            {
                skippedMissingCoords++;
                if (loggedMissingCoords < 5)
                {
                    logger.LogWarning(ex, "PositionUpdate: Failed to parse coords X/Y for character ID={CharacterId} Name={Name}", id, name);
                    loggedMissingCoords++;
                }
                continue;
            }

            var grid = await gridRepository.GetGridAsync(gridId);
            if (grid == null)
            {
                skippedMissingGrid++;
                unknownGrids.Add(gridId);
                if (loggedMissingGrid < 5)
                {
                    logger.LogWarning("PositionUpdate: Skipping character ID={CharacterId} Name={Name} - unknown GridID={GridId}. Client must send gridUpdate first to register this grid.", id, name, gridId);
                    loggedMissingGrid++;
                }
                continue;
            }

            // Use tenant ID from context (set by TenantContextMiddleware via token validation)
            var characterTenantId = context.Items["TenantId"] as string ?? string.Empty;

            var character = new Character
            {
                Name = name,
                Id = int.Parse(id),
                Map = grid.Map,
                Position = new Position(
                    x + (grid.Coord.X * 100),
                    y + (grid.Coord.Y * 100)),
                Type = type,
                Rotation = rotation,
                Speed = speed,
                Updated = DateTime.UtcNow,
                TenantId = characterTenantId
            };

            characterService.UpdateCharacter(id, character);
            processed++;
        }

        // Log summary for every positionUpdate
        if (total > 0)
        {
            logger.LogDebug("PositionUpdate: total={Total}, processed={Processed}, skippedMissingGrid={SkippedMissingGrid}, skippedMissingCoords={SkippedMissingCoords} | unknownGridsSample={Sample}",
                total, processed, skippedMissingGrid, skippedMissingCoords, string.Join(",", unknownGrids.Take(3)));
        }

        return Results.Ok();
    }

    private static async Task<IResult> MarkerBulkUpload(
        [FromRoute] string token,
        HttpContext context,
        ApplicationDbContext db,
        ITokenService tokenService,
        IMarkerService markerService,
        IGridRepository gridRepository,
        IUpdateNotificationService updateNotificationService,
        ILogger<Program> logger)
    {
        if (!await ClientTokenHelpers.HasUploadAsync(context, db, tokenService, token, logger))
            return Results.Unauthorized();

        var tenantId = context.Items["TenantId"] as string ?? string.Empty;

        // Read raw JSON with case-insensitive options (Go client sends lowercase keys)
        var markers = await context.Request.ReadFromJsonAsync<List<Dictionary<string, object>>>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (markers == null)
            return Results.BadRequest("Invalid marker bulk upload payload");

        // Helper: case-insensitive key lookup in dictionary
        object? GetValue(Dictionary<string, object> dict, string key)
        {
            foreach (var kvp in dict)
            {
                if (kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            return null;
        }

        var markerList = markers.Select(m => (
            GridId: GetValue(m, "GridID")?.ToString() ?? "",
            X: Convert.ToInt32(GetValue(m, "X")?.ToString() ?? "0"),
            Y: Convert.ToInt32(GetValue(m, "Y")?.ToString() ?? "0"),
            Name: GetValue(m, "Name")?.ToString() ?? "",
            Image: GetValue(m, "Image")?.ToString() ?? ""
        )).ToList();

        await markerService.BulkUploadMarkersAsync(markerList);

        // Broadcast SSE events for each marker
        foreach (var m in markerList)
        {
            var grid = await gridRepository.GetGridAsync(m.GridId);
            if (grid != null)
            {
                updateNotificationService.NotifyMarkerCreated(new MarkerEventDto
                {
                    MapId = grid.Map,
                    GridId = m.GridId,
                    X = m.X,
                    Y = m.Y,
                    Name = m.Name,
                    Image = string.IsNullOrEmpty(m.Image) ? "gfx/terobjs/mm/custom" : m.Image,
                    Hidden = false,
                    Ready = false,
                    MaxReady = -1,
                    MinReady = -1,
                    TenantId = tenantId
                });
            }
        }

        return Results.Ok();
    }

    private static async Task<IResult> MarkerDelete(
        [FromRoute] string token,
        HttpContext context,
        ApplicationDbContext db,
        ITokenService tokenService,
        IMarkerService markerService,
        IUpdateNotificationService updateNotificationService,
        ILogger<Program> logger)
    {
        if (!await ClientTokenHelpers.HasUploadAsync(context, db, tokenService, token, logger))
            return Results.Unauthorized();

        var tenantId = context.Items["TenantId"] as string ?? string.Empty;

        // Read raw JSON with case-insensitive options (Go client sends lowercase keys)
        var markers = await context.Request.ReadFromJsonAsync<List<Dictionary<string, object>>>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (markers == null)
            return Results.BadRequest("Invalid marker delete payload");

        // Helper: case-insensitive key lookup in dictionary
        object? GetValue(Dictionary<string, object> dict, string key)
        {
            foreach (var kvp in dict)
            {
                if (kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            return null;
        }

        var markerList = markers.Select(m => (
            GridId: GetValue(m, "GridID")?.ToString() ?? "",
            X: Convert.ToInt32(GetValue(m, "X")?.ToString() ?? "0"),
            Y: Convert.ToInt32(GetValue(m, "Y")?.ToString() ?? "0")
        )).ToList();

        await markerService.DeleteMarkersAsync(markerList);

        // Broadcast SSE delete events
        foreach (var m in markerList)
        {
            updateNotificationService.NotifyMarkerDeleted(new MarkerDeleteEventDto
            {
                GridId = m.GridId,
                X = m.X,
                Y = m.Y,
                TenantId = tenantId
            });
        }

        return Results.Ok();
    }

    private static async Task<IResult> MarkerUpdate(
        [FromRoute] string token,
        HttpContext context,
        ApplicationDbContext db,
        ITokenService tokenService,
        IMarkerService markerService,
        IGridRepository gridRepository,
        IUpdateNotificationService updateNotificationService,
        ILogger<Program> logger)
    {
        if (!await ClientTokenHelpers.HasUploadAsync(context, db, tokenService, token, logger))
            return Results.Unauthorized();

        var tenantId = context.Items["TenantId"] as string ?? string.Empty;

        // Read raw JSON with case-insensitive options (Go client sends lowercase keys)
        var markers = await context.Request.ReadFromJsonAsync<List<Dictionary<string, object>>>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (markers == null)
            return Results.BadRequest("Invalid marker update payload");

        // Helper: case-insensitive key lookup in dictionary
        object? GetValue(Dictionary<string, object> dict, string key)
        {
            foreach (var kvp in dict)
            {
                if (kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            return null;
        }

        logger.LogWarning("MarkerUpdate: Received {Count} markers", markers.Count);

        foreach (var m in markers)
        {
            // Safely get values from dictionary, handling missing keys (case-insensitive)
            var gridId = GetValue(m, "GridID")?.ToString() ?? "";
            var x = Convert.ToInt32(GetValue(m, "X")?.ToString() ?? "0");
            var y = Convert.ToInt32(GetValue(m, "Y")?.ToString() ?? "0");
            var name = GetValue(m, "Name")?.ToString() ?? "";
            var image = GetValue(m, "Image")?.ToString() ?? "";
            var ready = Convert.ToBoolean(GetValue(m, "Ready")?.ToString() ?? "false");

            logger.LogWarning(
                "MarkerUpdate: Processing marker - GridID={GridId}, X={X}, Y={Y}, Name={Name}, Image={Image}, Ready={Ready}",
                gridId, x, y, name, image, ready);

            await markerService.UpdateMarkerAsync(gridId, x, y, name, image, ready);

            // Broadcast SSE update event
            var grid = await gridRepository.GetGridAsync(gridId);
            if (grid != null)
            {
                updateNotificationService.NotifyMarkerUpdated(new MarkerEventDto
                {
                    MapId = grid.Map,
                    GridId = gridId,
                    X = x,
                    Y = y,
                    Name = name,
                    Image = string.IsNullOrEmpty(image) ? "gfx/terobjs/mm/custom" : image,
                    Hidden = false,
                    Ready = ready,
                    MaxReady = -1,
                    MinReady = -1,
                    TenantId = tenantId
                });
            }
        }

        return Results.Ok();
    }

    private static async Task<IResult> MarkerReadyTime(
        [FromRoute] string token,
        HttpContext context,
        ApplicationDbContext db,
        ITokenService tokenService,
        IMarkerService markerService,
        IMarkerRepository markerRepository,
        IGridRepository gridRepository,
        IUpdateNotificationService updateNotificationService,
        ILogger<Program> logger)
    {
        if (!await ClientTokenHelpers.HasUploadAsync(context, db, tokenService, token, logger))
            return Results.Unauthorized();

        var tenantId = context.Items["TenantId"] as string ?? string.Empty;

        // Read raw JSON with case-insensitive options (Go client sends lowercase keys)
        var markers = await context.Request.ReadFromJsonAsync<List<Dictionary<string, object>>>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (markers == null)
            return Results.BadRequest("Invalid marker ready time payload");

        // Helper: case-insensitive key lookup in dictionary
        object? GetValue(Dictionary<string, object> dict, string key)
        {
            foreach (var kvp in dict)
            {
                if (kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            return null;
        }

        foreach (var m in markers)
        {
            // Safely get values from dictionary, handling missing keys (case-insensitive)
            var gridId = GetValue(m, "GridID")?.ToString() ?? "";
            var x = Convert.ToInt32(GetValue(m, "X")?.ToString() ?? "0");
            var y = Convert.ToInt32(GetValue(m, "Y")?.ToString() ?? "0");
            var maxReady = Convert.ToInt64(GetValue(m, "MaxReady")?.ToString() ?? "-1");
            var minReady = Convert.ToInt64(GetValue(m, "MinReady")?.ToString() ?? "-1");

            await markerService.UpdateMarkerReadyTimeAsync(gridId, x, y, maxReady, minReady);

            // Broadcast SSE update event with updated ready times
            var key = $"{gridId}_{x}_{y}";
            var marker = await markerRepository.GetMarkerByKeyAsync(key);
            var grid = await gridRepository.GetGridAsync(gridId);
            if (marker != null && grid != null)
            {
                updateNotificationService.NotifyMarkerUpdated(new MarkerEventDto
                {
                    Id = marker.Id,
                    MapId = grid.Map,
                    GridId = gridId,
                    X = x,
                    Y = y,
                    Name = marker.Name,
                    Image = marker.Image,
                    Hidden = marker.Hidden,
                    Ready = marker.Ready,
                    MaxReady = marker.MaxReady,
                    MinReady = marker.MinReady,
                    TenantId = tenantId
                });
            }
        }

        return Results.Ok();
    }

    private static class ClientTokenHelpers
    {
        /// <summary>
        /// Validates token using TokenService, extracts tenant information, and stores tenant context.
        /// Returns true if token is valid for upload operations, false otherwise.
        ///
        /// Token format: {tenantId}_{secret} (e.g., "warrior-shield-42_abc123...")
        /// </summary>
        public static async Task<bool> HasUploadAsync(
            HttpContext httpContext,
            ApplicationDbContext db,
            ITokenService tokenService,
            string fullToken,
            ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(fullToken))
            {
                logger?.LogWarning("Token validation failed: empty or null token provided");
                return false;
            }

            // Use TokenService to validate token and extract tenant
            var validationResult = await tokenService.ValidateTokenAsync(fullToken);

            if (!validationResult.IsValid)
            {
                logger?.LogWarning(
                    "Token validation failed: {ErrorMessage}",
                    validationResult.ErrorMessage ?? "Unknown error");
                return false;
            }

            // Store tenant ID and user ID in HttpContext.Items for ITenantContextAccessor
            httpContext.Items["TenantId"] = validationResult.TenantId;
            httpContext.Items["UserId"] = validationResult.UserId;

            logger?.LogDebug(
                "Token validation successful. TenantId={TenantId}, UserId={UserId}",
                validationResult.TenantId, validationResult.UserId);

            return true;
        }
    }
}

