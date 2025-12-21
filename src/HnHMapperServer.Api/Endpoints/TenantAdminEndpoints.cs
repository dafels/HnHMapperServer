using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Tenant Admin endpoints for managing users within a tenant.
/// Requires TenantAdmin role or SuperAdmin role.
/// </summary>
public static class TenantAdminEndpoints
{
    public static void MapTenantAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenants/{tenantId}")
            .RequireAuthorization("TenantAdmin");

        // IMPORTANT: More specific routes must come BEFORE less specific routes

        // GET /api/tenants/{tenantId}/users/pending - List pending user approvals
        group.MapGet("/users/pending", GetPendingUsers);

        // POST /api/tenants/{tenantId}/users/{userId}/approve - Approve pending user
        group.MapPost("/users/{userId}/approve", ApproveUser);

        // POST /api/tenants/{tenantId}/users/{userId}/password - Reset user password (admin only)
        group.MapPost("/users/{userId}/password", ResetUserPassword);

        // GET /api/tenants/{tenantId}/users - List all users in tenant
        group.MapGet("/users", GetTenantUsers);

        // PUT /api/tenants/{tenantId}/users/{userId}/permissions - Update user permissions
        group.MapPut("/users/{userId}/permissions", UpdateUserPermissions);

        // DELETE /api/tenants/{tenantId}/users/{userId} - Remove user from tenant
        group.MapDelete("/users/{userId}", RemoveUserFromTenant);

        // GET /api/tenants/{tenantId}/settings - Get tenant settings
        group.MapGet("/settings", GetTenantSettings);

        // PUT /api/tenants/{tenantId}/settings - Update tenant settings
        group.MapPut("/settings", UpdateTenantSettings);

        // POST /api/tenants/{tenantId}/config/main-map - Set main map
        group.MapPost("/config/main-map", SetMainMap);

        // PUT /api/tenants/{tenantId}/config/map-upload-settings - Update map upload settings
        group.MapPut("/config/map-upload-settings", UpdateMapUploadSettings);

        // GET /api/tenants/{tenantId}/audit-logs - Get tenant audit logs
        group.MapGet("/audit-logs", GetTenantAuditLogs);

        // GET /api/tenants/{tenantId}/tokens - List all tokens for tenant
        group.MapGet("/tokens", GetTenantTokens);

        // DELETE /api/tenants/{tenantId}/tokens/{token} - Revoke a token
        group.MapDelete("/tokens/{token}", RevokeToken);

        // PUT /api/tenants/{tenantId}/discord-settings - Update Discord webhook settings
        group.MapPut("/discord-settings", UpdateDiscordSettings);

        // POST /api/tenants/{tenantId}/discord-test - Test Discord webhook
        group.MapPost("/discord-test", TestDiscordWebhook);

        // POST /api/tenants/{tenantId}/maps/import - Import .hmap file (large files up to 1GB)
        group.MapPost("/maps/import", ImportHmapFile)
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        // GET /api/tenants/{tenantId}/maps/import/status - Get import status
        group.MapGet("/maps/import/status", GetImportStatus);
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/users
    /// Lists all users in the specified tenant with their roles and permissions.
    /// </summary>
    private static async Task<IResult> GetTenantUsers(
        string tenantId,
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager,
        HttpContext context)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Query tenant users with permissions
        var tenantUsers = await db.TenantUsers
            .Where(tu => tu.TenantId == tenantId)
            .Include(tu => tu.Permissions)
            .ToListAsync();

        var userDtos = new List<TenantUserDto>();

        foreach (var tenantUser in tenantUsers)
        {
            var identityUser = await userManager.FindByIdAsync(tenantUser.UserId);
            if (identityUser == null) continue;

            userDtos.Add(new TenantUserDto
            {
                Id = tenantUser.Id,
                UserId = tenantUser.UserId,
                Username = identityUser.UserName ?? string.Empty,
                TenantId = tenantUser.TenantId,
                Role = tenantUser.Role.ToClaimValue(),
                Permissions = tenantUser.Permissions.Select(p => p.Permission.ToClaimValue()).ToList(),
                JoinedAt = tenantUser.JoinedAt,
                PendingApproval = tenantUser.PendingApproval
            });
        }

        return Results.Ok(userDtos);
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/users/pending
    /// Lists all users pending approval in the specified tenant.
    /// </summary>
    private static async Task<IResult> GetPendingUsers(
        string tenantId,
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager,
        HttpContext context,
        ILogger<Program> logger)
    {
        logger.LogInformation("GetPendingUsers called for tenant: {TenantId}", tenantId);

        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                logger.LogWarning("Access denied: User tenant {CurrentTenant} doesn't match requested {TenantId}", currentTenantId, tenantId);
                return Results.Forbid();
            }
        }

        // Query pending users (JoinedAt == default means pending)
        var pendingTenantUsers = await db.TenantUsers
            .Where(tu => tu.TenantId == tenantId && tu.JoinedAt == default)
            .ToListAsync();

        var pendingUserDtos = new List<object>();

        foreach (var tenantUser in pendingTenantUsers)
        {
            var identityUser = await userManager.FindByIdAsync(tenantUser.UserId);
            if (identityUser == null) continue;

            // Find the invitation used for registration
            var invitation = await db.TenantInvitations
                .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.UsedBy == tenantUser.UserId);

            // Use invitation UsedAt as RequestedAt if available, otherwise use minimum value
            var requestedAt = invitation?.UsedAt ?? DateTime.MinValue;

            pendingUserDtos.Add(new
            {
                UserId = tenantUser.UserId,
                Username = identityUser.UserName ?? string.Empty,
                RequestedAt = requestedAt,
                InvitationCode = invitation?.InviteCode
            });
        }

        return Results.Ok(pendingUserDtos);
    }

    /// <summary>
    /// POST /api/tenants/{tenantId}/users/{userId}/approve
    /// Approves a pending user and grants specified permissions.
    /// </summary>
    private static async Task<IResult> ApproveUser(
        string tenantId,
        string userId,
        ApproveTenantUserDto dto,
        ApplicationDbContext db,
        HttpContext context,
        IAuditService auditService,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Validate permissions (case-insensitive)
        var validPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Permission.Map.ToClaimValue(),
            Permission.Markers.ToClaimValue(),
            Permission.Pointer.ToClaimValue(),
            Permission.Upload.ToClaimValue(),
            Permission.Writer.ToClaimValue()
        };
        foreach (var permission in dto.Permissions)
        {
            if (!validPermissions.Contains(permission))
            {
                return Results.BadRequest(new { error = $"Invalid permission: {permission}" });
            }
        }

        // Find pending user
        var tenantUser = await db.TenantUsers
            .Include(tu => tu.Permissions)
            .FirstOrDefaultAsync(tu => tu.UserId == userId && tu.TenantId == tenantId && tu.JoinedAt == default);

        if (tenantUser == null)
        {
            return Results.NotFound(new { error = "Pending user not found" });
        }

        // Approve user by setting JoinedAt and clearing PendingApproval
        tenantUser.JoinedAt = DateTime.UtcNow;
        tenantUser.PendingApproval = false;

        // Also update the invitation's PendingApproval flag to prevent cleanup service from deleting this user
        var invitation = await db.TenantInvitations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.UsedBy == userId && i.TenantId == tenantId && i.PendingApproval);
        if (invitation != null)
        {
            invitation.PendingApproval = false;
        }

        // Add permissions
        foreach (var permission in dto.Permissions)
        {
            db.TenantPermissions.Add(new HnHMapperServer.Core.Models.TenantPermissionEntity
            {
                TenantUserId = tenantUser.Id,
                Permission = permission.ToPermission()
            });
        }

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Approved user {UserId} in tenant {TenantId} with permissions: {Permissions}",
            userId, tenantId, string.Join(", ", dto.Permissions));

        // Audit log
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "UserApproved",
            EntityType = "User",
            EntityId = userId,
            NewValue = string.Join(", ", dto.Permissions)
        });

        return Results.Ok(new { message = "User approved successfully" });
    }

    /// <summary>
    /// POST /api/tenants/{tenantId}/users/{userId}/password
    /// Resets a user's password (admin only).
    /// </summary>
    private static async Task<IResult> ResetUserPassword(
        string tenantId,
        string userId,
        ResetUserPasswordDto dto,
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager,
        HttpContext context,
        IAuditService auditService,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Validate new password
        if (string.IsNullOrWhiteSpace(dto.NewPassword))
            return Results.BadRequest(new { error = "New password is required" });

        if (dto.NewPassword.Length < 6)
            return Results.BadRequest(new { error = "Password must be at least 6 characters long" });

        // Verify user exists in this tenant
        var tenantUser = await db.TenantUsers
            .FirstOrDefaultAsync(tu => tu.UserId == userId && tu.TenantId == tenantId);

        if (tenantUser == null)
        {
            return Results.NotFound(new { error = "User not found in this tenant" });
        }

        // Get the identity user
        var identityUser = await userManager.FindByIdAsync(userId);
        if (identityUser == null)
        {
            return Results.NotFound(new { error = "User not found" });
        }

        // Generate password reset token and reset password
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(identityUser);
        var result = await userManager.ResetPasswordAsync(identityUser, resetToken, dto.NewPassword);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            logger.LogWarning("Password reset failed for user {UserId} in tenant {TenantId}: {Errors}", userId, tenantId, errors);
            return Results.BadRequest(new { error = errors });
        }

        logger.LogInformation("Password reset successfully for user {UserId} in tenant {TenantId} by admin", userId, tenantId);

        // Audit log
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "PasswordReset",
            EntityType = "User",
            EntityId = userId,
            NewValue = "Password reset by admin"
        });

        return Results.Ok(new { message = "Password reset successfully" });
    }

    /// <summary>
    /// PUT /api/tenants/{tenantId}/users/{userId}/permissions
    /// Updates the permissions for a user within the tenant.
    /// Replaces all existing permissions with the new set.
    /// </summary>
    private static async Task<IResult> UpdateUserPermissions(
        string tenantId,
        string userId,
        UpdateTenantUserPermissionsDto dto,
        ApplicationDbContext db,
        HttpContext context,
        IAuditService auditService,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Validate permissions (case-insensitive)
        var validPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Permission.Map.ToClaimValue(),
            Permission.Markers.ToClaimValue(),
            Permission.Pointer.ToClaimValue(),
            Permission.Upload.ToClaimValue(),
            Permission.Writer.ToClaimValue()
        };
        foreach (var permission in dto.Permissions)
        {
            if (!validPermissions.Contains(permission))
            {
                return Results.BadRequest(new { error = $"Invalid permission: {permission}" });
            }
        }

        // Find tenant user
        var tenantUser = await db.TenantUsers
            .Include(tu => tu.Permissions)
            .FirstOrDefaultAsync(tu => tu.UserId == userId && tu.TenantId == tenantId);

        if (tenantUser == null)
        {
            return Results.NotFound(new { error = "User not found in this tenant" });
        }

        // Capture old permissions for audit log
        var oldPermissions = string.Join(", ", tenantUser.Permissions.Select(p => p.Permission.ToClaimValue()));

        // Replace permissions (delete all, add new)
        db.TenantPermissions.RemoveRange(tenantUser.Permissions);

        foreach (var permission in dto.Permissions)
        {
            db.TenantPermissions.Add(new HnHMapperServer.Core.Models.TenantPermissionEntity
            {
                TenantUserId = tenantUser.Id,
                Permission = permission.ToPermission()
            });
        }

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Updated permissions for user {UserId} in tenant {TenantId}. New permissions: {Permissions}",
            userId, tenantId, string.Join(", ", dto.Permissions));

        // Audit log
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "PermissionsUpdated",
            EntityType = "User",
            EntityId = userId,
            OldValue = oldPermissions,
            NewValue = string.Join(", ", dto.Permissions)
        });

        return Results.Ok(new { message = "Permissions updated successfully" });
    }

    /// <summary>
    /// DELETE /api/tenants/{tenantId}/users/{userId}
    /// Removes a user from the tenant.
    /// Deletes TenantUser entry, associated permissions, and revokes tokens.
    /// </summary>
    private static async Task<IResult> RemoveUserFromTenant(
        string tenantId,
        string userId,
        ApplicationDbContext db,
        HttpContext context,
        IAuditService auditService,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }

            // Prevent self-removal
            var currentUserId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (currentUserId == userId)
            {
                return Results.BadRequest(new { error = "Cannot remove yourself from the tenant" });
            }
        }

        // Find tenant user
        var tenantUser = await db.TenantUsers
            .Include(tu => tu.Permissions)
            .FirstOrDefaultAsync(tu => tu.UserId == userId && tu.TenantId == tenantId);

        if (tenantUser == null)
        {
            return Results.NotFound(new { error = "User not found in this tenant" });
        }

        // Prevent removal of last admin (unless SuperAdmin is doing it)
        if (tenantUser.Role == TenantRole.TenantAdmin && !context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var adminCount = await db.TenantUsers
                .Where(tu => tu.TenantId == tenantId && tu.Role == TenantRole.TenantAdmin)
                .CountAsync();

            if (adminCount <= 1)
            {
                return Results.BadRequest(new { error = "Cannot remove the last admin from the tenant" });
            }
        }

        using (var transaction = await db.Database.BeginTransactionAsync())
        {
            try
            {
                // Delete permissions (cascade)
                db.TenantPermissions.RemoveRange(tenantUser.Permissions);

                // Delete tenant user
                db.TenantUsers.Remove(tenantUser);

                // Revoke tokens for this user in this tenant
                var userTokens = await db.Tokens
                    .Where(t => t.TenantId == tenantId && t.UserId == userId)
                    .ToListAsync();

                db.Tokens.RemoveRange(userTokens);

                await db.SaveChangesAsync();

                // Audit log (inside transaction)
                await auditService.LogAsync(new AuditEntry
                {
                    TenantId = tenantId,
                    Action = "UserRemoved",
                    EntityType = "User",
                    EntityId = userId,
                    OldValue = $"Role: {tenantUser.Role.ToClaimValue()}, {userTokens.Count} tokens revoked"
                });

                await transaction.CommitAsync();

                logger.LogInformation(
                    "Removed user {UserId} from tenant {TenantId}. Revoked {TokenCount} tokens.",
                    userId, tenantId, userTokens.Count);

                return Results.Ok(new { message = "User removed from tenant successfully" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                logger.LogError(ex, "Failed to remove user {UserId} from tenant {TenantId}", userId, tenantId);
                return Results.Problem("Failed to remove user from tenant");
            }
        }
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/settings
    /// Gets tenant settings (name, storage quota, etc.)
    /// </summary>
    private static async Task<IResult> GetTenantSettings(
        string tenantId,
        ApplicationDbContext db,
        HttpContext context,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            return Results.NotFound(new { error = "Tenant not found" });
        }

        // Count active users
        var userCount = await db.TenantUsers
            .Where(tu => tu.TenantId == tenantId && tu.JoinedAt != default)
            .CountAsync();

        var tenantDto = new TenantDto
        {
            Id = tenant.Id,
            Name = tenant.Name,
            StorageQuotaMB = tenant.StorageQuotaMB,
            CurrentStorageMB = tenant.CurrentStorageMB,
            CreatedAt = tenant.CreatedAt,
            IsActive = tenant.IsActive,
            UserCount = userCount,
            DiscordWebhookUrl = tenant.DiscordWebhookUrl,
            DiscordNotificationsEnabled = tenant.DiscordNotificationsEnabled
        };

        return Results.Ok(tenantDto);
    }

    /// <summary>
    /// PUT /api/tenants/{tenantId}/settings
    /// Updates tenant settings (currently only name is editable by tenant admin)
    /// </summary>
    private static async Task<IResult> UpdateTenantSettings(
        string tenantId,
        UpdateTenantSettingsDto dto,
        ApplicationDbContext db,
        HttpContext context,
        IAuditService auditService,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            return Results.NotFound(new { error = "Tenant not found" });
        }

        // Only allow updating name (quota is managed by SuperAdmin)
        if (!string.IsNullOrWhiteSpace(dto.Name))
        {
            var oldName = tenant.Name;
            tenant.Name = dto.Name;
            await db.SaveChangesAsync();
            logger.LogInformation("Tenant {TenantId} name updated to: {Name}", tenantId, dto.Name);

            // Audit log
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantId,
                Action = "TenantSettingsUpdated",
                EntityType = "Tenant",
                EntityId = tenantId,
                OldValue = $"Name: {oldName}",
                NewValue = $"Name: {dto.Name}"
            });
        }

        return Results.Ok(new { message = "Settings updated successfully" });
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/audit-logs
    /// Gets audit logs for this tenant only
    /// </summary>
    private static async Task<IResult> GetTenantAuditLogs(
        string tenantId,
        IAuditService auditService,
        UserManager<IdentityUser> userManager,
        HttpContext context,
        ILogger<Program> logger,
        string? userId = null,
        string? action = null,
        string? entityType = null,
        DateTime? since = null,
        DateTime? until = null,
        int limit = 500)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Query audit logs for this tenant using IAuditService
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

            // Tenant name not needed for tenant-scoped view
            dto.TenantName = null;

            enrichedLogs.Add(dto);
        }

        logger.LogInformation("Loaded {Count} audit logs for tenant {TenantId}", enrichedLogs.Count, tenantId);
        return Results.Ok(enrichedLogs);
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/tokens
    /// Lists all authentication tokens for this tenant
    /// </summary>
    private static async Task<IResult> GetTenantTokens(
        string tenantId,
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager,
        HttpContext context,
        IConfigRepository configRepository,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Get the prefix configuration for URL construction (GLOBAL setting)
        var prefix = await configRepository.GetGlobalValueAsync("prefix") ?? string.Empty;

        var tokens = await db.Tokens
            .Where(t => t.TenantId == tenantId)
            .ToListAsync();

        var tokenDtos = new List<object>();

        foreach (var token in tokens)
        {
            var user = await userManager.FindByIdAsync(token.UserId);
            if (user == null) continue;

            // Get user's permissions in this tenant
            var tenantUser = await db.TenantUsers
                .Include(tu => tu.Permissions)
                .FirstOrDefaultAsync(tu => tu.TenantId == tenantId && tu.UserId == token.UserId);

            var permissions = tenantUser?.Permissions.Select(p => p.Permission.ToClaimValue()).ToList() ?? new List<string>();

            // Construct the full URL
            var url = string.IsNullOrEmpty(prefix)
                ? $"/client/{token.DisplayToken}"
                : $"{prefix}/client/{token.DisplayToken}";

            tokenDtos.Add(new
            {
                Token = token.DisplayToken,
                Username = user.UserName ?? string.Empty,
                Permissions = permissions,
                Url = url
            });
        }

        logger.LogInformation("Loaded {Count} tokens for tenant {TenantId}", tokenDtos.Count, tenantId);
        return Results.Ok(tokenDtos);
    }

    /// <summary>
    /// DELETE /api/tenants/{tenantId}/tokens/{token}
    /// Revokes an authentication token
    /// </summary>
    private static async Task<IResult> RevokeToken(
        string tenantId,
        string token,
        ApplicationDbContext db,
        HttpContext context,
        IAuditService auditService,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        var tokenEntity = await db.Tokens
            .FirstOrDefaultAsync(t => t.DisplayToken == token && t.TenantId == tenantId);

        if (tokenEntity == null)
        {
            return Results.NotFound(new { error = "Token not found" });
        }

        var revokedUserId = tokenEntity.UserId;

        db.Tokens.Remove(tokenEntity);
        await db.SaveChangesAsync();

        logger.LogInformation("Revoked token for user {UserId} in tenant {TenantId}", revokedUserId, tenantId);

        // Audit log
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "TokenRevoked",
            EntityType = "Token",
            EntityId = token,
            OldValue = $"User: {revokedUserId}"
        });

        return Results.Ok(new { message = "Token revoked successfully" });
    }

    /// <summary>
    /// POST /api/tenants/{tenantId}/config/main-map
    /// Sets the main/default map for the tenant
    /// </summary>
    private static async Task<IResult> SetMainMap(
        string tenantId,
        SetMainMapDto dto,
        ApplicationDbContext db,
        Core.Interfaces.IConfigRepository configRepository,
        HttpContext context,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // If mapId is provided, validate that the map exists and belongs to this tenant
        if (dto.MapId.HasValue)
        {
            var map = await db.Maps
                .FirstOrDefaultAsync(m => m.Id == dto.MapId.Value && m.TenantId == tenantId);

            if (map == null)
            {
                return Results.NotFound(new { error = "Map not found or does not belong to this tenant" });
            }
        }

        // Get current config
        var config = await configRepository.GetConfigAsync();

        // Update main map ID
        config.MainMapId = dto.MapId;

        // Save config
        await configRepository.SaveConfigAsync(config);

        logger.LogInformation("Tenant {TenantId} main map set to: {MapId}", tenantId, dto.MapId?.ToString() ?? "none");
        return Results.Ok(new { message = "Main map updated successfully", mainMapId = dto.MapId });
    }

    /// <summary>
    /// PUT /api/tenants/{tenantId}/config/map-upload-settings
    /// Updates map upload settings (allowGridUpdates, allowNewMaps) for the tenant.
    /// </summary>
    private static async Task<IResult> UpdateMapUploadSettings(
        string tenantId,
        UpdateMapUploadSettingsDto dto,
        Core.Interfaces.IConfigRepository configRepository,
        HttpContext context,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Get current config
        var config = await configRepository.GetConfigAsync();

        // Update settings
        config.AllowGridUpdates = dto.AllowGridUpdates;
        config.AllowNewMaps = dto.AllowNewMaps;

        // Save config
        await configRepository.SaveConfigAsync(config);

        logger.LogInformation("Tenant {TenantId} map upload settings updated: AllowGridUpdates={AllowGridUpdates}, AllowNewMaps={AllowNewMaps}",
            tenantId, dto.AllowGridUpdates, dto.AllowNewMaps);
        return Results.Ok(new { message = "Map upload settings updated successfully" });
    }

    /// <summary>
    /// PUT /api/tenants/{tenantId}/discord-settings
    /// Updates Discord webhook settings for the tenant.
    /// </summary>
    private static async Task<IResult> UpdateDiscordSettings(
        string tenantId,
        UpdateDiscordSettingsDto dto,
        ApplicationDbContext db,
        HttpContext context,
        IAuditService auditService,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Validate webhook URL format if provided
        if (!string.IsNullOrWhiteSpace(dto.WebhookUrl))
        {
            if (!dto.WebhookUrl.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) &&
                !dto.WebhookUrl.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Invalid Discord webhook URL. Must start with https://discord.com/api/webhooks/ or https://discordapp.com/api/webhooks/" });
            }
        }

        // Find tenant
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            return Results.NotFound(new { error = "Tenant not found" });
        }

        var oldEnabled = tenant.DiscordNotificationsEnabled;
        var oldWebhookUrl = tenant.DiscordWebhookUrl;

        // Update settings
        tenant.DiscordNotificationsEnabled = dto.Enabled;
        tenant.DiscordWebhookUrl = dto.WebhookUrl;

        await db.SaveChangesAsync();

        // Audit log
        await auditService.LogAsync(new AuditEntry
        {
            TenantId = tenantId,
            Action = "DiscordSettingsUpdated",
            EntityType = "TenantSettings",
            EntityId = tenantId,
            OldValue = $"Enabled: {oldEnabled}, WebhookUrl: {(string.IsNullOrEmpty(oldWebhookUrl) ? "none" : "***")}",
            NewValue = $"Enabled: {dto.Enabled}, WebhookUrl: {(string.IsNullOrEmpty(dto.WebhookUrl) ? "none" : "***")}"
        });

        logger.LogInformation("Discord settings updated for tenant {TenantId}: Enabled={Enabled}", tenantId, dto.Enabled);

        return Results.Ok(new { message = "Discord settings updated successfully" });
    }

    /// <summary>
    /// POST /api/tenants/{tenantId}/discord-test
    /// Sends a test notification to the Discord webhook.
    /// </summary>
    private static async Task<IResult> TestDiscordWebhook(
        string tenantId,
        ApplicationDbContext db,
        IDiscordWebhookService discordService,
        HttpContext context)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        // Find tenant
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            return Results.NotFound(new { error = "Tenant not found" });
        }

        if (string.IsNullOrWhiteSpace(tenant.DiscordWebhookUrl))
        {
            return Results.BadRequest(new { error = "Discord webhook URL is not configured" });
        }

        // Test the webhook
        var success = await discordService.TestWebhookAsync(tenant.DiscordWebhookUrl);

        if (success)
        {
            return Results.Ok(new { message = "Test notification sent successfully! Check your Discord channel." });
        }
        else
        {
            return Results.BadRequest(new { error = "Failed to send test notification. Please check your webhook URL and try again." });
        }
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/maps/import/status
    /// Gets the current import status for the tenant.
    /// </summary>
    private static IResult GetImportStatus(
        string tenantId,
        ImportLockService lockService,
        HttpContext context)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                return Results.Forbid();
            }
        }

        var status = lockService.GetStatus(tenantId);
        return Results.Ok(new
        {
            isImporting = status.IsImporting,
            canImport = status.CanImport,
            cooldownSeconds = status.CooldownRemaining?.TotalSeconds ?? 0
        });
    }

    /// <summary>
    /// POST /api/tenants/{tenantId}/maps/import
    /// Imports an .hmap file exported from the Haven &amp; Hearth game client.
    /// Streams SSE progress events during import.
    /// The import continues in the background even if the client disconnects after file upload.
    /// </summary>
    private static async Task ImportHmapFile(
        string tenantId,
        IFormFile file,
        [FromForm] string mode,
        IHmapImportService importService,
        ImportLockService lockService,
        IConfiguration configuration,
        HttpContext context,
        IAuditService auditService,
        ILogger<Program> logger)
    {
        // Verify user has access to this tenant (unless SuperAdmin)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var currentTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
            if (currentTenantId != tenantId)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Forbidden" });
                return;
            }
        }

        // Check import lock and cooldown
        var (lockAcquired, lockReason, waitTime) = lockService.TryAcquireLock(tenantId);
        if (!lockAcquired)
        {
            context.Response.StatusCode = 429; // Too Many Requests
            await context.Response.WriteAsJsonAsync(new
            {
                error = lockReason,
                cooldownSeconds = waitTime?.TotalSeconds ?? 0
            });
            return;
        }

        // Validate file
        if (file == null || file.Length == 0)
        {
            lockService.ReleaseLock(tenantId, success: false);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "No file provided" });
            return;
        }

        if (!file.FileName.EndsWith(".hmap", StringComparison.OrdinalIgnoreCase))
        {
            lockService.ReleaseLock(tenantId, success: false);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "File must be a .hmap file" });
            return;
        }

        // Parse import mode
        if (!Enum.TryParse<HmapImportMode>(mode, ignoreCase: true, out var importMode))
        {
            lockService.ReleaseLock(tenantId, success: false);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid mode. Must be 'Merge' or 'CreateNew'" });
            return;
        }

        var gridStorage = configuration["GridStorage"] ?? "map";

        // Save uploaded file to temp location so import can continue even if client disconnects
        var tempDir = Path.Combine(gridStorage, "hmap-temp");
        Directory.CreateDirectory(tempDir);
        var tempFilePath = Path.Combine(tempDir, $"{tenantId}_{Guid.NewGuid():N}.hmap");

        try
        {
            using (var tempFileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
            {
                await file.CopyToAsync(tempFileStream);
            }
        }
        catch (Exception ex)
        {
            lockService.ReleaseLock(tenantId, success: false);
            logger.LogError(ex, "Failed to save uploaded .hmap file for tenant {TenantId}", tenantId);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "Failed to save uploaded file" });
            return;
        }

        logger.LogInformation(
            "Starting .hmap import for tenant {TenantId}, file: {FileName}, size: {Size} bytes, mode: {Mode}",
            tenantId, file.FileName, file.Length, importMode);

        // Set up SSE response
        context.Response.Headers["Content-Type"] = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";

        var writer = context.Response.BodyWriter;
        var clientDisconnected = false;

        // Helper to send SSE events (ignores errors if client disconnected)
        async Task SendProgressEvent(HmapImportProgress p)
        {
            if (clientDisconnected) return;
            try
            {
                var data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    phase = p.Phase,
                    current = p.CurrentItem,
                    total = p.TotalItems,
                    itemName = p.CurrentItemName ?? "",
                    phaseNumber = p.PhaseNumber,
                    totalPhases = p.TotalPhases,
                    overallPercent = p.OverallPercent,
                    elapsedSeconds = p.ElapsedSeconds,
                    itemsPerSecond = p.ItemsPerSecond
                });
                var bytes = System.Text.Encoding.UTF8.GetBytes($"event: progress\ndata: {data}\n\n");
                await writer.WriteAsync(bytes);
                await writer.FlushAsync();
            }
            catch
            {
                clientDisconnected = true;
            }
        }

        async Task SendCompleteEvent(object eventData)
        {
            if (clientDisconnected) return;
            try
            {
                var data = System.Text.Json.JsonSerializer.Serialize(eventData);
                var bytes = System.Text.Encoding.UTF8.GetBytes($"event: complete\ndata: {data}\n\n");
                await writer.WriteAsync(bytes);
                await writer.FlushAsync();
            }
            catch
            {
                clientDisconnected = true;
            }
        }

        HmapImportResult? importResult = null;

        try
        {
            // Create progress reporter that sends SSE events (continues even if client gone)
            var progress = new Progress<HmapImportProgress>(async p =>
            {
                await SendProgressEvent(p);
            });

            // Open file from disk - import continues regardless of client connection
            using var stream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read);

            // Use CancellationToken.None so import continues even if client disconnects
            importResult = await importService.ImportAsync(stream, tenantId, importMode, gridStorage, progress, CancellationToken.None);

            if (!importResult.Success)
            {
                logger.LogWarning("Import failed for tenant {TenantId}: {Error}", tenantId, importResult.ErrorMessage);

                // Clean up any partially created data
                if (importResult.CreatedMapIds.Count > 0 || importResult.CreatedGridIds.Count > 0)
                {
                    await SendProgressEvent(new HmapImportProgress
                    {
                        Phase = "Cleaning up",
                        CurrentItem = 0,
                        TotalItems = 1,
                        CurrentItemName = "Rolling back changes...",
                        PhaseNumber = 6,
                        TotalPhases = 6,
                        OverallPercent = 100
                    });
                    await importService.CleanupFailedImportAsync(
                        importResult.CreatedMapIds,
                        importResult.CreatedGridIds,
                        tenantId,
                        gridStorage);
                }

                lockService.ReleaseLock(tenantId, success: false);

                await SendCompleteEvent(new
                {
                    success = false,
                    error = importResult.ErrorMessage ?? "Import failed",
                    cleanedUp = importResult.CreatedMapIds.Count > 0 || importResult.CreatedGridIds.Count > 0
                });
                return;
            }

            // Success - release lock
            lockService.ReleaseLock(tenantId, success: true);

            // Audit log
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantId,
                Action = "HmapImport",
                EntityType = "Map",
                NewValue = $"Mode: {importMode}, Maps: {importResult.MapsCreated}, Grids: {importResult.GridsImported}, Skipped: {importResult.GridsSkipped}, Duration: {importResult.Duration.TotalSeconds:F1}s"
            });

            logger.LogInformation(
                "Import completed for tenant {TenantId}: {MapsCreated} maps, {GridsImported} grids imported, {GridsSkipped} skipped",
                tenantId, importResult.MapsCreated, importResult.GridsImported, importResult.GridsSkipped);

            // Send completion event (may fail if client disconnected, that's OK)
            await SendCompleteEvent(new
            {
                success = true,
                mapsCreated = importResult.MapsCreated,
                gridsImported = importResult.GridsImported,
                gridsSkipped = importResult.GridsSkipped,
                tilesRendered = importResult.TilesRendered,
                affectedMapIds = importResult.AffectedMapIds.Distinct().ToList(),
                duration = importResult.Duration.TotalSeconds
            });
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Invalid .hmap file format for tenant {TenantId}", tenantId);
            lockService.ReleaseLock(tenantId, success: false);
            await SendCompleteEvent(new
            {
                success = false,
                error = $"Invalid .hmap file: {ex.Message}",
                cleanedUp = false
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during .hmap import for tenant {TenantId}", tenantId);

            // Clean up any partially created data
            if (importResult != null && (importResult.CreatedMapIds.Count > 0 || importResult.CreatedGridIds.Count > 0))
            {
                try
                {
                    await importService.CleanupFailedImportAsync(
                        importResult.CreatedMapIds,
                        importResult.CreatedGridIds,
                        tenantId,
                        gridStorage);
                }
                catch (Exception cleanupEx)
                {
                    logger.LogError(cleanupEx, "Failed to cleanup after import error for tenant {TenantId}", tenantId);
                }
            }

            lockService.ReleaseLock(tenantId, success: false);
            await SendCompleteEvent(new
            {
                success = false,
                error = "An unexpected error occurred during import",
                cleanedUp = importResult?.CreatedMapIds.Count > 0 || importResult?.CreatedGridIds.Count > 0
            });
        }
        finally
        {
            // Only delete temp file on successful import
            // Keep failed imports for 7 days for debugging (cleaned up by HmapTempCleanupService)
            if (importResult?.Success == true)
            {
                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete temp file {TempFile}", tempFilePath);
                }
            }
            else
            {
                logger.LogInformation(
                    "Keeping temp file for debugging: {TempFile} (will be cleaned up after 7 days)",
                    tempFilePath);
            }
        }
    }

    /// <summary>
    /// DTO for approving a pending user
    /// </summary>
    public sealed class ApproveTenantUserDto
    {
        public List<string> Permissions { get; set; } = new();
    }

    /// <summary>
    /// DTO for updating tenant settings
    /// </summary>
    public sealed class UpdateTenantSettingsDto
    {
        public string? Name { get; set; }
    }

    /// <summary>
    /// DTO for setting main map
    /// </summary>
    public sealed class SetMainMapDto
    {
        public int? MapId { get; set; }
    }

    /// <summary>
    /// DTO for updating map upload settings
    /// </summary>
    public sealed class UpdateMapUploadSettingsDto
    {
        public bool AllowGridUpdates { get; set; } = true;
        public bool AllowNewMaps { get; set; } = true;
    }

    /// <summary>
    /// DTO for resetting user password
    /// </summary>
    public sealed class ResetUserPasswordDto
    {
        public string NewPassword { get; set; } = string.Empty;
    }
}
