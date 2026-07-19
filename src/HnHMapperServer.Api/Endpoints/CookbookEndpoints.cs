using System.Security.Claims;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Cookbook endpoints: the food catalog is tenant-scoped reference data readable by
/// any authenticated user; imports (wipe-and-replace) are a tenant-admin operation
/// on the admin's own tenant.
/// </summary>
public static class CookbookEndpoints
{
    public static void MapCookbookEndpoints(this IEndpointRouteBuilder app)
    {
        var catalog = app.MapGroup("/api/v1/cookbook")
            .RequireAuthorization();

        catalog.MapGet("/foods", GetFoods);
        catalog.MapGet("/foods/{id:int}/variations", GetVariations);
        catalog.MapGet("/recipe-index", GetRecipeIndex);

        // Per-user food panels (Favorites + custom, optionally tenant-shared)
        catalog.MapGet("/panels", GetPanels);
        catalog.MapPost("/panels", CreatePanel);
        catalog.MapPut("/panels/{id:int}", UpdatePanel);
        catalog.MapDelete("/panels/{id:int}", DeletePanel);
        catalog.MapPost("/panels/{id:int}/items", AddPanelItem);
        catalog.MapPost("/panels/{id:int}/items/remove", RemovePanelItem);
        catalog.MapPut("/panels/{id:int}/order", ReorderPanel);
        catalog.MapPost("/panels/favorites/toggle", ToggleFavorite);

        // Tenant-admin import/status, scoped to the admin's own tenant. The policy
        // checks the role; the route tenant is matched to the caller in-handler
        // (same pattern as TenantAdminEndpoints).
        var admin = app.MapGroup("/api/tenants/{tenantId}/cookbook")
            .RequireAuthorization("TenantAdmin");

        admin.MapGet("/status", GetStatus);
        admin.MapPost("/import", ImportCookbook);
        admin.MapDelete("", ClearCookbook);
    }

    /// <summary>
    /// GET /api/v1/cookbook/foods
    /// Returns the full food catalog (cached server-side).
    /// </summary>
    private static async Task<IResult> GetFoods(
        IFoodCatalogService foodCatalogService,
        ILogger<Program> logger)
    {
        try
        {
            var foods = await foodCatalogService.GetCatalogAsync();
            return Results.Ok(foods);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading cookbook catalog");
            return Results.Problem("Failed to load cookbook catalog");
        }
    }

    /// <summary>
    /// GET /api/v1/cookbook/foods/{id}/variations
    /// Returns all recorded recipe variations of one food, best first.
    /// </summary>
    private static async Task<IResult> GetVariations(
        int id,
        IFoodCatalogService foodCatalogService,
        ILogger<Program> logger)
    {
        try
        {
            var variations = await foodCatalogService.GetVariationsAsync(id);
            return Results.Ok(variations);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading cookbook variations for food {FoodId}", id);
            return Results.Problem("Failed to load recipe variations");
        }
    }

    /// <summary>
    /// GET /api/v1/cookbook/recipe-index
    /// Wiki recipe lines for every known craftable (incl. non-food intermediates like
    /// "Unbaked Meatpie"), used by the UI to expand recipes recursively.
    /// Tenant-independent: built from the bundled wiki dump.
    /// </summary>
    private static async Task<IResult> GetRecipeIndex(
        IFoodCatalogService foodCatalogService,
        ILogger<Program> logger)
    {
        try
        {
            return Results.Ok(await foodCatalogService.GetRecipeIndexAsync());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading cookbook recipe index");
            return Results.Problem("Failed to load recipe index");
        }
    }

    /// <summary>Runs a panel operation, mapping service exceptions to HTTP results.</summary>
    private static async Task<IResult> RunPanelOpAsync(
        HttpContext context, ILogger<Program> logger, Func<string, Task<IResult>> operation)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        try
        {
            return await operation(userId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Panel not found" });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 403);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Panel operation failed");
            return Results.Problem("Panel operation failed");
        }
    }

    private static Task<IResult> GetPanels(
        HttpContext context, IFoodPanelService panelService, ILogger<Program> logger) =>
        RunPanelOpAsync(context, logger, async userId =>
            Results.Ok(await panelService.GetPanelsAsync(userId)));

    private static Task<IResult> CreatePanel(
        CreateFoodPanelDto dto, HttpContext context, IFoodPanelService panelService, ILogger<Program> logger) =>
        RunPanelOpAsync(context, logger, async userId =>
            Results.Ok(await panelService.CreatePanelAsync(userId, dto.Name)));

    private static Task<IResult> UpdatePanel(
        int id, UpdateFoodPanelDto dto, HttpContext context, IFoodPanelService panelService, ILogger<Program> logger) =>
        RunPanelOpAsync(context, logger, async userId =>
            Results.Ok(await panelService.UpdatePanelAsync(id, userId, dto)));

    private static Task<IResult> DeletePanel(
        int id, HttpContext context, IFoodPanelService panelService, ILogger<Program> logger) =>
        RunPanelOpAsync(context, logger, async userId =>
        {
            await panelService.DeletePanelAsync(id, userId);
            return Results.Ok(new { deleted = true });
        });

    private static Task<IResult> AddPanelItem(
        int id, PanelItemRequestDto dto, HttpContext context, IFoodPanelService panelService, ILogger<Program> logger) =>
        RunPanelOpAsync(context, logger, async userId =>
            Results.Ok(await panelService.AddItemAsync(id, userId, dto.FoodName, dto.IngredientSignature)));

    private static Task<IResult> RemovePanelItem(
        int id, PanelItemRemoveDto dto, HttpContext context, IFoodPanelService panelService, ILogger<Program> logger) =>
        RunPanelOpAsync(context, logger, async userId =>
            Results.Ok(await panelService.RemoveItemAsync(id, userId, dto.ItemId)));

    private static Task<IResult> ReorderPanel(
        int id, PanelOrderDto dto, HttpContext context, IFoodPanelService panelService, ILogger<Program> logger) =>
        RunPanelOpAsync(context, logger, async userId =>
            Results.Ok(await panelService.ReorderAsync(id, userId, dto.ItemIds)));

    private static Task<IResult> ToggleFavorite(
        PanelItemRequestDto dto, HttpContext context, IFoodPanelService panelService, ILogger<Program> logger) =>
        RunPanelOpAsync(context, logger, async userId =>
            Results.Ok(await panelService.ToggleFavoriteAsync(userId, dto.FoodName, dto.IngredientSignature)));

    /// <summary>Route tenant must be the caller's own tenant (SuperAdmin may act on any).</summary>
    private static bool CanManageTenant(ClaimsPrincipal user, string tenantId) =>
        user.IsInRole(AuthorizationConstants.Roles.SuperAdmin)
        || user.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value == tenantId;

    /// <summary>
    /// GET /api/tenants/{tenantId}/cookbook/status
    /// Returns food count and last import/upload time for the tenant.
    /// </summary>
    private static async Task<IResult> GetStatus(
        string tenantId,
        ClaimsPrincipal user,
        IFoodCatalogService foodCatalogService,
        ILogger<Program> logger)
    {
        if (!CanManageTenant(user, tenantId))
        {
            return Results.Forbid();
        }

        try
        {
            var status = await foodCatalogService.GetStatusAsync(tenantId);
            return Results.Ok(status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading cookbook status for tenant {TenantId}", tenantId);
            return Results.Problem("Failed to load cookbook status");
        }
    }

    /// <summary>
    /// DELETE /api/tenants/{tenantId}/cookbook
    /// Removes every food and recipe variation of the tenant's cookbook (the UI asks
    /// for confirmation first). Panels/favorites keep their name-keyed items and gray
    /// out until foods return via import or client uploads.
    /// </summary>
    private static async Task<IResult> ClearCookbook(
        string tenantId,
        IFoodCatalogService foodCatalogService,
        IAuditService auditService,
        ClaimsPrincipal user,
        ILogger<Program> logger)
    {
        if (!CanManageTenant(user, tenantId))
        {
            return Results.Forbid();
        }

        try
        {
            var result = await foodCatalogService.ClearAsync(tenantId);

            if (result.Foods > 0 || result.Variants > 0)
            {
                await auditService.LogAsync(new AuditEntry
                {
                    UserId = user.FindFirstValue(ClaimTypes.NameIdentifier),
                    TenantId = tenantId,
                    Action = "CookbookCleared",
                    EntityType = "FoodCatalog",
                    OldValue = $"{result.Foods} foods, {result.Variants} variants"
                });

                logger.LogInformation(
                    "{Username} cleared the cookbook of tenant {TenantId} ({Foods} foods, {Variants} variants)",
                    user.FindFirstValue(ClaimTypes.Name) ?? "unknown", tenantId, result.Foods, result.Variants);
            }

            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error clearing cookbook for tenant {TenantId}", tenantId);
            return Results.Problem("Failed to clear cookbook data");
        }
    }

    /// <summary>
    /// POST /api/tenants/{tenantId}/cookbook/import
    /// Multipart upload: file "foods" (food-info2.json, required) and file "wiki"
    /// (wiki-food-data.json, optional — the bundled dump is used otherwise).
    /// Replaces the tenant's entire catalog.
    /// </summary>
    private static async Task<IResult> ImportCookbook(
        string tenantId,
        HttpRequest request,
        IFoodCatalogService foodCatalogService,
        ApplicationDbContext db,
        IAuditService auditService,
        ClaimsPrincipal user,
        ILogger<Program> logger)
    {
        try
        {
            if (!CanManageTenant(user, tenantId))
            {
                return Results.Forbid();
            }

            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Content-Type must be multipart/form-data" });
            }

            var form = await request.ReadFormAsync();

            var tenantExists = await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId);
            if (!tenantExists)
            {
                return Results.BadRequest(new { error = $"Unknown tenant '{tenantId}'" });
            }

            var foodsFile = form.Files.GetFile("foods");
            if (foodsFile == null || foodsFile.Length == 0)
            {
                return Results.BadRequest(new { error = "Food data file 'foods' is required" });
            }

            var wikiFile = form.Files.GetFile("wiki");

            await using var foodsStream = foodsFile.OpenReadStream();
            // Wiki data supplies food groups/satiations. An uploaded file wins; the service
            // falls back to the bundled wiki dump when none is provided.
            await using var wikiStream = wikiFile is { Length: > 0 } ? wikiFile.OpenReadStream() : null;

            var result = await foodCatalogService.ImportAsync(foodsStream, wikiStream, tenantId);

            if (result.Imported > 0)
            {
                await auditService.LogAsync(new AuditEntry
                {
                    UserId = user.FindFirstValue(ClaimTypes.NameIdentifier),
                    TenantId = tenantId,
                    Action = "CookbookImported",
                    EntityType = "FoodCatalog",
                    NewValue = $"{result.Imported} foods ({result.WikiMatched} wiki-matched, {result.Fallback} fallback, {result.Skipped} skipped)"
                });

                logger.LogInformation(
                    "{Username} imported cookbook into tenant {TenantId}: {Imported} foods",
                    user.FindFirstValue(ClaimTypes.Name) ?? "unknown", tenantId, result.Imported);

                return Results.Ok(result);
            }

            // Nothing imported: the catalog was left untouched — report why.
            return Results.BadRequest(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error importing cookbook data");
            return Results.Problem("Failed to import cookbook data");
        }
    }
}
