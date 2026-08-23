using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Self-service tenant creation: any signed-in player can create a map (tenant) and becomes its TenantAdmin.
/// Quota, per-user cap and the kill switch live in configuration (TenantSelfService:*); the body carries only
/// an optional display name. IP rate-limited (3/hour) on top of the cap.
/// </summary>
public static class TenantSelfServiceEndpoints
{
    public static void MapTenantSelfServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tenants/self", CreateOwnTenant)
            .RequireAuthorization()
            .RequireRateLimiting("TenantCreate")
            .DisableAntiforgery();

        app.MapGet("/api/tenants/self/options", GetOptions)
            .RequireAuthorization();
    }

    public sealed class CreateOwnTenantRequest
    {
        public string? Name { get; set; }
    }

    private static async Task<IResult> CreateOwnTenant(
        [FromBody] CreateOwnTenantRequest? request,
        HttpContext context,
        [FromServices] UserManager<ApplicationUser> userManager,
        [FromServices] ITenantProvisioningService provisioning,
        ILogger<Program> logger)
    {
        var user = await userManager.GetUserAsync(context.User);
        if (user == null)
            return Results.Unauthorized();

        var result = await provisioning.CreateOwnedTenantAsync(user.Id, request?.Name);

        return result.Outcome switch
        {
            TenantProvisionOutcome.Created => Results.Created($"/api/tenants/{result.TenantId}", new
            {
                tenantId = result.TenantId,
                tenantName = result.TenantName
            }),
            TenantProvisionOutcome.Disabled => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden),
            TenantProvisionOutcome.NotEligible => Results.Json(new { error = result.Error, notEligible = true }, statusCode: StatusCodes.Status403Forbidden),
            TenantProvisionOutcome.CapReached => Results.Conflict(new { error = result.Error }),
            TenantProvisionOutcome.InvalidName => Results.BadRequest(new { error = result.Error }),
            _ => Results.BadRequest(new { error = result.Error ?? "Could not create the map." })
        };
    }

    /// <summary>Lets the "create a map" card show the right state: switched off, not eligible (password-only account), or ready.</summary>
    private static async Task<IResult> GetOptions(
        HttpContext context,
        [FromServices] UserManager<ApplicationUser> userManager,
        [FromServices] ITenantProvisioningService provisioning)
    {
        var user = await userManager.GetUserAsync(context.User);
        if (user == null)
            return Results.Unauthorized();

        var options = await provisioning.GetOptionsAsync(user.Id);
        return Results.Ok(new { enabled = options.Enabled, eligible = options.Eligible, reason = options.Reason });
    }
}
