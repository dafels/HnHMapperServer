using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// GET /api/superadmin/accounts - the accounts overview (superadmin only). Kept separate from the large
/// SuperadminEndpoints file; same policy. The legacy GET /api/superadmin/users (id/username/email picker) stays.
/// </summary>
public static class SuperadminAccountEndpoints
{
    public static void MapSuperadminAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/superadmin/accounts", GetAccounts)
            .RequireAuthorization(AuthorizationConstants.Policies.SuperAdminOnly);
    }

    private static async Task<IResult> GetAccounts(
        [FromServices] IAccountOverviewService overview,
        int page = 1,
        int pageSize = 25,
        string? search = null,
        string? method = null,
        string? source = null,
        bool noTenant = false,
        string? tenantId = null)
    {
        var result = await overview.GetAccountsAsync(new AccountOverviewQuery
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
            Method = method,
            Source = source,
            NoTenant = noTenant,
            TenantId = tenantId
        });
        return Results.Ok(result);
    }
}
