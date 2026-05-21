using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Audit log endpoints for viewing sensitive operation history.
/// Provides both superadmin (global) and tenant admin (scoped) views.
/// </summary>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        // Superadmin endpoint - view all audit logs across all tenants
        group.MapGet("/superadmin/audit-logs", GetAllAuditLogs)
            .RequireAuthorization("SuperadminOnly");

        // Tenant admin endpoint - view audit logs for a specific tenant
        group.MapGet("/tenants/{tenantId}/audit-logs", GetTenantAuditLogs)
            .RequireAuthorization("TenantAdmin");
    }

    /// <summary>
    /// GET /api/superadmin/audit-logs
    /// Retrieves audit logs across all tenants with filtering.
    /// Only accessible to superadmins.
    /// </summary>
    private static async Task<IResult> GetAllAuditLogs(
        IAuditService auditService,
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        string? tenantId = null,
        string? userId = null,
        string? action = null,
        string? entityType = null,
        DateTime? since = null,
        DateTime? until = null,
        int limit = 100)
    {
        // Query audit logs with filters
        var logs = await auditService.GetLogsAsync(new AuditQuery
        {
            TenantId = tenantId,
            UserId = userId,
            Action = action,
            EntityType = entityType,
            Since = since,
            Until = until,
            Limit = limit
        });

        // Enrich with username and tenant name
        var enrichedLogs = new List<AuditLogDto>();

        foreach (var log in logs)
        {
            var dto = new AuditLogDto
            {
                Timestamp = log.Timestamp,
                UserId = log.UserId,
                TenantId = log.TenantId,
                Action = log.Action,
                EntityType = log.EntityType,
                EntityId = log.EntityId,
                OldValue = log.OldValue,
                NewValue = log.NewValue,
                IpAddress = log.IpAddress,
                UserAgent = log.UserAgent
            };

            // Resolve username (may be null if user deleted)
            if (log.UserId != null)
            {
                var user = await userManager.FindByIdAsync(log.UserId);
                dto.Username = user?.UserName;
            }

            // Resolve tenant name (may be null if tenant deleted)
            if (log.TenantId != null)
            {
                var tenant = await db.Tenants
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.Id == log.TenantId);
                dto.TenantName = tenant?.Name;
            }

            enrichedLogs.Add(dto);
        }

        return Results.Ok(enrichedLogs);
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/audit-logs
    /// Retrieves audit logs for a specific tenant.
    /// Tenant admins can only view their own tenant's logs.
    /// Superadmins can view any tenant's logs.
    /// </summary>
    private static async Task<IResult> GetTenantAuditLogs(
        string tenantId,
        IAuditService auditService,
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        HttpContext context,
        string? userId = null,
        string? action = null,
        string? entityType = null,
        DateTime? since = null,
        DateTime? until = null,
        int limit = 100)
    {
        // Verify user has access to this tenant (unless Superadmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Query audit logs for this tenant only
        var logs = await auditService.GetLogsAsync(new AuditQuery
        {
            TenantId = tenantId,
            UserId = userId,
            Action = action,
            EntityType = entityType,
            Since = since,
            Until = until,
            Limit = limit
        });

        // Enrich with username
        var enrichedLogs = new List<AuditLogDto>();

        foreach (var log in logs)
        {
            var dto = new AuditLogDto
            {
                Timestamp = log.Timestamp,
                UserId = log.UserId,
                TenantId = log.TenantId,
                Action = log.Action,
                EntityType = log.EntityType,
                EntityId = log.EntityId,
                OldValue = log.OldValue,
                NewValue = log.NewValue,
                IpAddress = log.IpAddress,
                UserAgent = log.UserAgent
            };

            // Resolve username (may be null if user deleted)
            if (log.UserId != null)
            {
                var user = await userManager.FindByIdAsync(log.UserId);
                dto.Username = user?.UserName;
            }

            // Tenant name not needed (already known from URL parameter)
            dto.TenantName = tenantId;

            enrichedLogs.Add(dto);
        }

        return Results.Ok(enrichedLogs);
    }
}
