using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace HnHMapperServer.Api.Middleware;

/// <summary>
/// Middleware that extracts the tenant ID from the request and stores it in HttpContext.Items
/// Supports two strategies:
/// 1. Extract from token in URL path (/client/{tenantId}_{secret}/...)
/// 2. Extract from authentication claims (TenantId claim for web UI)
/// </summary>
public class TenantContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantContextMiddleware> _logger;

    public TenantContextMiddleware(
        RequestDelegate next,
        ILogger<TenantContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ApplicationDbContext db)
    {
        string? tenantId = null;

        // Strategy 1: Extract from token in URL (game clients)
        // Path format: /client/{tenantId}_{secret}/...
        if (context.Request.Path.StartsWithSegments("/client"))
        {
            var pathParts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts != null && pathParts.Length >= 2)
            {
                var fullToken = pathParts[1];

                // Extract tenant ID from token (format: tenantId_secret)
                var underscoreIndex = fullToken.IndexOf('_');
                if (underscoreIndex > 0)
                {
                    var extractedTenantId = fullToken.Substring(0, underscoreIndex);
                    var secret = fullToken.Substring(underscoreIndex + 1);

                    // Validate token
                    var (valid, validatedTenantId) = await ValidateTokenAsync(db, extractedTenantId, secret);

                    if (valid)
                    {
                        tenantId = validatedTenantId;
                        _logger.LogDebug("Tenant context resolved from token: {TenantId}", tenantId);
                    }
                    else
                    {
                        _logger.LogWarning("Invalid token for tenant {TenantId}", extractedTenantId);
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync(new { error = "Invalid token" });
                        return;
                    }
                }
                else
                {
                    // Token must be in format {tenantId}_{secret}
                    _logger.LogWarning("Invalid token format. Expected: {{tenantId}}_{{secret}}");
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid token format. Tokens must include tenant prefix." });
                    return;
                }
            }
        }
        // Strategy 2: Extract from claims (web UI)
        else if (context.User.Identity?.IsAuthenticated == true)
        {
            tenantId = context.User.FindFirst("TenantId")?.Value;

            if (!string.IsNullOrEmpty(tenantId))
            {
                _logger.LogDebug("Tenant context resolved from claims: {TenantId}", tenantId);
            }
            else
            {
                // Normal for users who have not created/joined a tenant yet (they are routed to the create-or-join screen)
                _logger.LogDebug("User is authenticated but has no TenantId claim (no active tenant)");
            }
        }
        else
        {
            _logger.LogWarning("User is not authenticated for request: {Path}", context.Request.Path);
        }

        // Store tenant ID in context for downstream use
        if (!string.IsNullOrEmpty(tenantId))
        {
            context.Items["TenantId"] = tenantId;
        }
        else
        {
            _logger.LogDebug("No tenant context available for request: {Path}", context.Request.Path);
        }

        await _next(context);
    }

    /// <summary>
    /// Validates a token by checking if the hash matches and the tenant matches
    /// </summary>
    /// <param name="db">Database context</param>
    /// <param name="tenantId">Extracted tenant ID from token prefix</param>
    /// <param name="secret">Secret portion of the token</param>
    /// <returns>Tuple of (isValid, tenantId)</returns>
    private async Task<(bool isValid, string? tenantId)> ValidateTokenAsync(
        ApplicationDbContext db,
        string tenantId,
        string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Token validation failed: empty secret");
            return (false, null);
        }

        // Hash the secret portion
        var hash = ComputeSha256(secret);

        // Look up token by hash and verify tenant ID matches
        // IMPORTANT: IgnoreQueryFilters() is required because we're establishing tenant context
        // The global query filter would block this query since tenant context doesn't exist yet
        var token = await db.Tokens
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.TenantId == tenantId);

        if (token == null)
        {
            _logger.LogWarning("Token validation failed: no matching token found for tenant {TenantId}", tenantId);
            return (false, null);
        }

        // Check if token is expired
        if (token.ExpiresAt.HasValue && token.ExpiresAt.Value < DateTime.UtcNow)
        {
            _logger.LogWarning("Token validation failed: token expired for tenant {TenantId}", tenantId);
            return (false, null);
        }

        // Check if tenant is active
        // IMPORTANT: IgnoreQueryFilters() not strictly needed for Tenants table, but added for consistency
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null || !tenant.IsActive)
        {
            _logger.LogWarning("Token validation failed: tenant {TenantId} is inactive or does not exist", tenantId);
            return (false, null);
        }

        // Note: LastUsedAt update is handled by existing ClientTokenHelpers.HasUploadAsync
        // This middleware only validates tenant context

        return (true, tenantId);
    }

    /// <summary>
    /// Computes SHA-256 hash of a string
    /// </summary>
    private static string ComputeSha256(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
