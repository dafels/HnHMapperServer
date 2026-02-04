using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Superadmin endpoints for managing all tenants globally.
/// Requires Superadmin role.
/// </summary>
public static class SuperadminEndpoints
{
    public static void MapSuperadminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/superadmin")
            .RequireAuthorization("SuperadminOnly");

        // === SuperAdmin Role Management ===

        // GET /api/superadmin/superadmins - List all users with SuperAdmin role
        group.MapGet("/superadmins", GetAllSuperAdmins);

        // POST /api/superadmin/users/{userId}/grant-superadmin - Grant SuperAdmin role
        group.MapPost("/users/{userId}/grant-superadmin", GrantSuperAdmin);

        // POST /api/superadmin/users/{userId}/revoke-superadmin - Revoke SuperAdmin role
        group.MapPost("/users/{userId}/revoke-superadmin", RevokeSuperAdmin);

        // GET /api/superadmin/users - List all users (for granting SuperAdmin)
        group.MapGet("/users", GetAllUsers);

        // === Tenant Management ===

        // GET /api/superadmin/tenants - List all tenants
        group.MapGet("/tenants", GetAllTenants);

        // POST /api/superadmin/tenants - Create new tenant
        group.MapPost("/tenants", CreateTenant);

        // GET /api/superadmin/tenants/{tenantId} - View tenant details
        group.MapGet("/tenants/{tenantId}", GetTenantDetails);

        // PUT /api/superadmin/tenants/{tenantId} - Update tenant (name, quota, status)
        group.MapPut("/tenants/{tenantId}", UpdateTenant);

        // PUT /api/superadmin/tenants/{tenantId}/quota - Update storage quota
        group.MapPut("/tenants/{tenantId}/quota", UpdateStorageQuota);

        // PATCH /api/superadmin/tenants/{tenantId}/status - Suspend/activate tenant
        group.MapPatch("/tenants/{tenantId}/status", UpdateTenantStatus);

        // POST /api/superadmin/tenants/{tenantId}/suspend - Suspend tenant
        group.MapPost("/tenants/{tenantId}/suspend", SuspendTenant);

        // POST /api/superadmin/tenants/{tenantId}/activate - Activate tenant
        group.MapPost("/tenants/{tenantId}/activate", ActivateTenant);

        // NOTE: Audit logs endpoint is registered in AuditEndpoints.cs
        // GET /api/superadmin/audit-logs is handled by AuditEndpoints.MapAuditEndpoints()

        // GET /api/superadmin/config - Get all system configuration values
        group.MapGet("/config", GetAllConfig);

        // PUT /api/superadmin/config/{key} - Update a single configuration value
        group.MapPut("/config/{key}", UpdateConfig);

        // PUT /api/superadmin/tenants/{tenantId}/users/{userId}/permissions - Update user permissions
        group.MapPut("/tenants/{tenantId}/users/{userId}/permissions", UpdateUserPermissions);

        // PUT /api/superadmin/tenants/{tenantId}/users/{userId}/role - Change user role
        group.MapPut("/tenants/{tenantId}/users/{userId}/role", UpdateUserRole);

        // POST /api/superadmin/tenants/{tenantId}/users/{userId}/password - Reset user password
        group.MapPost("/tenants/{tenantId}/users/{userId}/password", ResetUserPassword);

        // DELETE /api/superadmin/tenants/{tenantId}/users/{userId} - Remove user from tenant
        group.MapDelete("/tenants/{tenantId}/users/{userId}", RemoveUserFromTenant);

        // GET /api/superadmin/users/unassigned - List users not assigned to any tenant
        group.MapGet("/users/unassigned", GetUnassignedUsers);

        // POST /api/superadmin/users/{userId}/assign-tenant - Assign user to tenant
        group.MapPost("/users/{userId}/assign-tenant", AssignUserToTenant);

        // === Map & Marker Monitoring ===

        // GET /api/superadmin/maps - List all maps across all tenants
        group.MapGet("/maps", GetAllMaps);

        // GET /api/superadmin/tenants/{tenantId}/maps - List maps for specific tenant
        group.MapGet("/tenants/{tenantId}/maps", GetTenantMaps);

        // GET /api/superadmin/markers - List all markers across tenants (paginated)
        group.MapGet("/markers", GetAllMarkers);

        // GET /api/superadmin/tenants/{tenantId}/markers - List markers for specific tenant
        group.MapGet("/tenants/{tenantId}/markers", GetTenantMarkers);

        // GET /api/superadmin/custom-markers - List all custom markers across tenants (paginated)
        group.MapGet("/custom-markers", GetAllCustomMarkers);

        // GET /api/superadmin/tenants/{tenantId}/custom-markers - Custom markers for specific tenant
        group.MapGet("/tenants/{tenantId}/custom-markers", GetTenantCustomMarkers);

        // GET /api/superadmin/tenants/{tenantId}/statistics - Enhanced tenant statistics
        group.MapGet("/tenants/{tenantId}/statistics", GetTenantStatistics);

        // DELETE /api/superadmin/tenants/{tenantId}/maps/{mapId} - Delete map for tenant
        group.MapDelete("/tenants/{tenantId}/maps/{mapId}", DeleteTenantMap);

        // === Cross-Tenant Map Viewing ===

        // GET /api/superadmin/tenants/{tenantId}/map-view-data - Get all map view data for tenant
        group.MapGet("/tenants/{tenantId}/map-view-data", GetTenantMapViewData);

        // GET /api/superadmin/tenants/{tenantId}/custom-markers-data - Get custom markers for map viewing
        group.MapGet("/tenants/{tenantId}/custom-markers-data", GetTenantCustomMarkersData);

        // === Public Maps Management ===

        // GET /api/superadmin/public-maps - List all public maps
        group.MapGet("/public-maps", GetAllPublicMaps);

        // POST /api/superadmin/public-maps - Create a new public map
        group.MapPost("/public-maps", CreatePublicMap);

        // GET /api/superadmin/public-maps/{id} - Get public map with sources
        group.MapGet("/public-maps/{id}", GetPublicMap);

        // PUT /api/superadmin/public-maps/{id} - Update public map
        group.MapPut("/public-maps/{id}", UpdatePublicMap);

        // DELETE /api/superadmin/public-maps/{id} - Delete public map
        group.MapDelete("/public-maps/{id}", DeletePublicMap);

        // POST /api/superadmin/public-maps/{id}/sources - Add source map
        group.MapPost("/public-maps/{id}/sources", AddPublicMapSource);

        // DELETE /api/superadmin/public-maps/{id}/sources/{sourceId} - Remove source
        group.MapDelete("/public-maps/{id}/sources/{sourceId}", RemovePublicMapSource);

        // POST /api/superadmin/public-maps/{id}/regenerate - Trigger regeneration
        group.MapPost("/public-maps/{id}/regenerate", TriggerPublicMapRegeneration);

        // GET /api/superadmin/public-maps/{id}/status - Get generation status
        group.MapGet("/public-maps/{id}/status", GetPublicMapGenerationStatus);

        // GET /api/superadmin/available-tenant-maps - Get available tenant maps for source selection
        group.MapGet("/available-tenant-maps", GetAvailableTenantMaps);

        // === HMap Source Library Management ===

        // GET /api/superadmin/hmap-sources - List all HMap sources
        group.MapGet("/hmap-sources", GetAllHmapSources);

        // POST /api/superadmin/hmap-sources - Upload new HMap source
        group.MapPost("/hmap-sources", UploadHmapSource).DisableAntiforgery();

        // GET /api/superadmin/hmap-sources/{id} - Get HMap source details
        group.MapGet("/hmap-sources/{id:int}", GetHmapSource);

        // DELETE /api/superadmin/hmap-sources/{id} - Delete HMap source
        group.MapDelete("/hmap-sources/{id:int}", DeleteHmapSource);

        // POST /api/superadmin/hmap-sources/{id}/analyze - Re-analyze HMap source
        group.MapPost("/hmap-sources/{id:int}/analyze", AnalyzeHmapSource);

        // === Public Map HMap Source Selection ===

        // GET /api/superadmin/public-maps/{id}/hmap-sources - List selected HMap sources
        group.MapGet("/public-maps/{id}/hmap-sources", GetPublicMapHmapSources);

        // POST /api/superadmin/public-maps/{id}/hmap-sources - Add HMap source to public map
        group.MapPost("/public-maps/{id}/hmap-sources", AddPublicMapHmapSource);

        // DELETE /api/superadmin/public-maps/{id}/hmap-sources/{sourceId} - Remove HMap source
        group.MapDelete("/public-maps/{id}/hmap-sources/{sourceId:int}", RemovePublicMapHmapSource);

        // GET /api/superadmin/public-maps/{id}/hmap-sources/analysis - Contribution analysis
        group.MapGet("/public-maps/{id}/hmap-sources/analysis", AnalyzePublicMapHmapSourceContributions);

        // POST /api/superadmin/public-maps/{id}/generate-from-hmap - Generate from HMap sources
        group.MapPost("/public-maps/{id}/generate-from-hmap", GenerateFromHmapSources);
    }

    /// <summary>
    /// GET /api/superadmin/tenants
    /// Lists all tenants with statistics (user count, storage usage, etc.)
    /// </summary>
    private static async Task<IResult> GetAllTenants(
        ApplicationDbContext db,
        ITenantActivityService activityService)
    {
        var tenants = await db.Tenants
            .IgnoreQueryFilters() // Superadmin sees all tenants
            .ToListAsync();

        // Get merged activity times (in-memory cache + database)
        var activities = await activityService.GetAllLastActivitiesAsync();

        var tenantDtos = new List<TenantListDto>();

        foreach (var tenant in tenants)
        {
            var userCount = await db.TenantUsers
                .IgnoreQueryFilters()
                .Where(tu => tu.TenantId == tenant.Id)
                .CountAsync();

            var tokenCount = await db.Tokens
                .IgnoreQueryFilters()
                .Where(t => t.TenantId == tenant.Id)
                .CountAsync();

            tenantDtos.Add(new TenantListDto
            {
                Id = tenant.Id,
                Name = tenant.Name,
                StorageQuotaMB = tenant.StorageQuotaMB,
                CurrentStorageMB = tenant.CurrentStorageMB,
                CreatedAt = tenant.CreatedAt,
                IsActive = tenant.IsActive,
                UserCount = userCount,
                TokenCount = tokenCount,
                LastActivityAt = activities.TryGetValue(tenant.Id, out var activity) ? activity : null
            });
        }

        return Results.Ok(tenantDtos);
    }

    /// <summary>
    /// POST /api/superadmin/tenants
    /// Creates a new tenant with auto-generated ID
    /// </summary>
    private static async Task<IResult> CreateTenant(
        CreateTenantDto dto,
        ITenantService tenantService,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            var newTenant = await tenantService.CreateTenantAsync(dto);

            // Log audit entry
            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = newTenant.Id,
                UserId = username,
                Action = "TenantCreated",
                EntityType = "Tenant",
                EntityId = newTenant.Id,
                NewValue = $"Created tenant {newTenant.Id} (Name: {newTenant.Name}) with quota {dto.StorageQuotaMB}MB"
            });

            logger.LogInformation("SuperAdmin {Username} created tenant {TenantId}", username, newTenant.Id);
            return Results.Created($"/api/superadmin/tenants/{newTenant.Id}", newTenant);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating tenant");
            return Results.Problem("Failed to create tenant");
        }
    }

    /// <summary>
    /// GET /api/superadmin/tenants/{tenantId}
    /// Gets detailed information about a specific tenant including users
    /// </summary>
    private static async Task<IResult> GetTenantDetails(
        string tenantId,
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager)
    {
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null)
        {
            return Results.NotFound(new { error = "Tenant not found" });
        }

        var userCount = await db.TenantUsers
            .IgnoreQueryFilters()
            .Where(tu => tu.TenantId == tenantId)
            .CountAsync();

        var tokenCount = await db.Tokens
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .CountAsync();

        // Get tenant users with permissions (use AsNoTracking to ensure fresh data)
        var tenantUsers = await db.TenantUsers
            .AsNoTracking()
            .IgnoreQueryFilters()
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

        var details = new TenantDetailsDto
        {
            Id = tenant.Id,
            Name = tenant.Name,
            StorageQuotaMB = tenant.StorageQuotaMB,
            CurrentStorageMB = tenant.CurrentStorageMB,
            CreatedAt = tenant.CreatedAt,
            IsActive = tenant.IsActive,
            UserCount = userCount,
            TokenCount = tokenCount,
            Users = userDtos
        };

        return Results.Ok(details);
    }

    /// <summary>
    /// PUT /api/superadmin/tenants/{tenantId}
    /// Updates tenant information (name, quota, status)
    /// </summary>
    private static async Task<IResult> UpdateTenant(
        string tenantId,
        UpdateTenantDto dto,
        ITenantService tenantService,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            var updatedTenant = await tenantService.UpdateTenantAsync(tenantId, dto);

            // Log audit entry
            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantId,
                UserId = username,
                Action = "TenantUpdated",
                EntityType = "Tenant",
                EntityId = tenantId,
                OldValue = null, // TenantService handles the delta tracking
                NewValue = $"Name: {dto.Name}, Quota: {dto.StorageQuotaMB}MB, Active: {dto.IsActive}"
            });

            return Results.Ok(updatedTenant);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Failed to update tenant {TenantId}", tenantId);
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating tenant {TenantId}", tenantId);
            return Results.Problem("Failed to update tenant");
        }
    }

    /// <summary>
    /// PUT /api/superadmin/tenants/{tenantId}/quota
    /// Updates the storage quota for a tenant
    /// </summary>
    private static async Task<IResult> UpdateStorageQuota(
        string tenantId,
        UpdateStorageQuotaDto dto,
        ApplicationDbContext db,
        ILogger<Program> logger,
        IAuditService auditService,
        HttpContext context)
    {
        if (dto.StorageQuotaMB < 0)
        {
            return Results.BadRequest(new { error = "Storage quota must be non-negative" });
        }

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null)
        {
            return Results.NotFound(new { error = "Tenant not found" });
        }

        var oldQuota = tenant.StorageQuotaMB;
        tenant.StorageQuotaMB = dto.StorageQuotaMB;
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Superadmin updated storage quota for tenant {TenantId}: {OldQuota}MB -> {NewQuota}MB",
            tenantId, oldQuota, dto.StorageQuotaMB);

        // Audit log
        await auditService.LogAsync(new AuditEntry
        {
            UserId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            TenantId = tenantId,
            Action = "UpdateStorageQuota",
            EntityType = "Tenant",
            EntityId = tenantId,
            OldValue = JsonSerializer.Serialize(new { storageQuotaMB = oldQuota }),
            NewValue = JsonSerializer.Serialize(new { storageQuotaMB = dto.StorageQuotaMB })
        });

        return Results.Ok(new
        {
            message = "Storage quota updated successfully",
            oldQuota = oldQuota,
            newQuota = dto.StorageQuotaMB
        });
    }

    /// <summary>
    /// PATCH /api/superadmin/tenants/{tenantId}/status
    /// Suspends or activates a tenant
    /// Suspended tenants cannot authenticate
    /// </summary>
    private static async Task<IResult> UpdateTenantStatus(
        string tenantId,
        UpdateTenantStatusDto dto,
        ApplicationDbContext db,
        ILogger<Program> logger,
        IAuditService auditService,
        HttpContext context)
    {
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        if (tenant == null)
        {
            return Results.NotFound(new { error = "Tenant not found" });
        }

        var oldStatus = tenant.IsActive;
        tenant.IsActive = dto.IsActive;
        await db.SaveChangesAsync();

        var action = dto.IsActive ? "activated" : "suspended";
        logger.LogWarning(
            "Superadmin {Action} tenant {TenantId}",
            action, tenantId);

        // Audit log
        await auditService.LogAsync(new AuditEntry
        {
            UserId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            TenantId = tenantId,
            Action = dto.IsActive ? "ActivateTenant" : "SuspendTenant",
            EntityType = "Tenant",
            EntityId = tenantId,
            OldValue = JsonSerializer.Serialize(new { isActive = oldStatus }),
            NewValue = JsonSerializer.Serialize(new { isActive = dto.IsActive })
        });

        return Results.Ok(new
        {
            message = $"Tenant {action} successfully",
            tenantId = tenantId,
            isActive = dto.IsActive
        });
    }

    /// <summary>
    /// POST /api/superadmin/tenants/{tenantId}/suspend
    /// Suspends a tenant (shorthand for PATCH with IsActive=false)
    /// </summary>
    private static async Task<IResult> SuspendTenant(
        string tenantId,
        ApplicationDbContext db,
        ILogger<Program> logger,
        IAuditService auditService,
        HttpContext context)
    {
        return await UpdateTenantStatus(
            tenantId,
            new UpdateTenantStatusDto { IsActive = false },
            db,
            logger,
            auditService,
            context);
    }

    /// <summary>
    /// POST /api/superadmin/tenants/{tenantId}/activate
    /// Activates a tenant (shorthand for PATCH with IsActive=true)
    /// </summary>
    private static async Task<IResult> ActivateTenant(
        string tenantId,
        ApplicationDbContext db,
        ILogger<Program> logger,
        IAuditService auditService,
        HttpContext context)
    {
        return await UpdateTenantStatus(
            tenantId,
            new UpdateTenantStatusDto { IsActive = true },
            db,
            logger,
            auditService,
            context);
    }

    /// <summary>
    /// GET /api/superadmin/config
    /// Gets all global system configuration values
    /// </summary>
    private static async Task<IResult> GetAllConfig(
        IConfigRepository configRepository,
        ILogger<Program> logger)
    {
        try
        {
            // Get global config values (TenantId = "__global__")
            // Currently we only manage these as global: prefix
            // Other settings like title, defaultHide, mainMap are tenant-scoped
            var configValues = new Dictionary<string, string>();

            var prefix = await configRepository.GetGlobalValueAsync("prefix");
            if (!string.IsNullOrEmpty(prefix))
                configValues["prefix"] = prefix;

            // For backwards compatibility, also include other config keys
            // (These will eventually be moved to tenant-specific management)
            var title = await configRepository.GetValueAsync("title");
            if (!string.IsNullOrEmpty(title))
                configValues["title"] = title;

            var defaultHide = await configRepository.GetValueAsync("defaultHide");
            if (!string.IsNullOrEmpty(defaultHide))
                configValues["defaultHide"] = defaultHide;

            logger.LogInformation("SuperAdmin: Loaded {Count} config values", configValues.Count);
            return Results.Ok(configValues);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading config values");
            return Results.Problem("Failed to load config values");
        }
    }

    /// <summary>
    /// PUT /api/superadmin/config/{key}
    /// Updates a single configuration value
    /// </summary>
    private static async Task<IResult> UpdateConfig(
        string key,
        UpdateConfigDto dto,
        IConfigRepository configRepository,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            // Determine if this is a global or tenant-scoped config key
            var isGlobalKey = key == "prefix"; // Only prefix is global for now

            string? oldValue;

            if (isGlobalKey)
            {
                // Get old value and save as global
                oldValue = await configRepository.GetGlobalValueAsync(key);
                await configRepository.SetGlobalValueAsync(key, dto.Value);
            }
            else
            {
                // Get old value and save as tenant-scoped
                oldValue = await configRepository.GetValueAsync(key);
                await configRepository.SetValueAsync(key, dto.Value);
            }

            // Log audit entry
            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = null, // Global config
                UserId = username,
                Action = "ConfigUpdated",
                EntityType = "Config",
                EntityId = key,
                OldValue = oldValue,
                NewValue = dto.Value
            });

            logger.LogInformation("SuperAdmin: Updated {Scope} config {Key} = {Value}",
                isGlobalKey ? "GLOBAL" : "tenant",
                key,
                dto.Value);
            return Results.Ok(new { key, value = dto.Value });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating config {Key}", key);
            return Results.Problem("Failed to update config");
        }
    }

    /// <summary>
    /// PUT /api/superadmin/tenants/{tenantId}/users/{userId}/permissions
    /// Updates permissions for a user within a tenant
    /// </summary>
    private static async Task<IResult> UpdateUserPermissions(
        string tenantId,
        string userId,
        UpdateUserPermissionsDto dto,
        ApplicationDbContext db,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            // Find tenant user
            var tenantUser = await db.TenantUsers
                .IgnoreQueryFilters()
                .Include(tu => tu.Permissions)
                .FirstOrDefaultAsync(tu => tu.TenantId == tenantId && tu.UserId == userId);

            if (tenantUser == null)
            {
                logger.LogWarning("TenantUser not found: TenantId={TenantId}, UserId={UserId}", tenantId, userId);
                return Results.NotFound(new { error = "User not found in this tenant" });
            }

            // Get old permissions for audit log
            var oldPermissions = tenantUser.Permissions.Select(p => p.Permission).ToList();

            // Remove existing permissions
            db.TenantPermissions.RemoveRange(tenantUser.Permissions);

            // Add new permissions
            foreach (var permission in dto.Permissions)
            {
                db.TenantPermissions.Add(new HnHMapperServer.Core.Models.TenantPermissionEntity
                {
                    TenantUserId = tenantUser.Id,
                    Permission = permission.ToPermission()
                });
            }

            await db.SaveChangesAsync();

            // Log audit entry
            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantId,
                UserId = username,
                Action = "SuperAdminUpdatedUserPermissions",
                EntityType = "TenantUser",
                EntityId = userId,
                OldValue = JsonSerializer.Serialize(oldPermissions),
                NewValue = JsonSerializer.Serialize(dto.Permissions)
            });

            logger.LogInformation("SuperAdmin updated permissions for user {UserId} in tenant {TenantId}", userId, tenantId);
            return Results.Ok(new { message = "Permissions updated successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating user permissions");
            return Results.Problem("Failed to update user permissions");
        }
    }

    /// <summary>
    /// PUT /api/superadmin/tenants/{tenantId}/users/{userId}/role
    /// Changes the role of a user within a tenant (TenantAdmin <-> TenantUser)
    /// </summary>
    private static async Task<IResult> UpdateUserRole(
        string tenantId,
        string userId,
        UpdateUserRoleDto dto,
        ApplicationDbContext db,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            // Validate role
            if (dto.Role != TenantRole.TenantAdmin.ToClaimValue() && dto.Role != TenantRole.TenantUser.ToClaimValue())
            {
                return Results.BadRequest(new { error = "Role must be either 'TenantAdmin' or 'TenantUser'" });
            }

            // Find tenant user
            var tenantUser = await db.TenantUsers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(tu => tu.TenantId == tenantId && tu.UserId == userId);

            if (tenantUser == null)
            {
                logger.LogWarning("TenantUser not found: TenantId={TenantId}, UserId={UserId}", tenantId, userId);
                return Results.NotFound(new { error = "User not found in this tenant" });
            }

            var oldRole = tenantUser.Role;

            // Check if role is already the same
            if (oldRole.ToClaimValue() == dto.Role)
            {
                return Results.Ok(new { message = "User already has this role" });
            }

            // Update role
            tenantUser.Role = dto.Role.ToTenantRole();
            await db.SaveChangesAsync();

            // Log audit entry
            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantId,
                UserId = username,
                Action = "SuperAdminChangedUserRole",
                EntityType = "TenantUser",
                EntityId = userId,
                OldValue = oldRole.ToClaimValue(),
                NewValue = dto.Role
            });

            logger.LogInformation("SuperAdmin changed role for user {UserId} in tenant {TenantId}: {OldRole} -> {NewRole}",
                userId, tenantId, oldRole, dto.Role);
            return Results.Ok(new { message = $"User role changed to {dto.Role}" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating user role");
            return Results.Problem("Failed to update user role");
        }
    }

    /// <summary>
    /// POST /api/superadmin/tenants/{tenantId}/users/{userId}/password
    /// Resets a user's password (SuperAdmin privilege)
    /// </summary>
    private static async Task<IResult> ResetUserPassword(
        string tenantId,
        string userId,
        ResetPasswordDto dto,
        UserManager<IdentityUser> userManager,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            // Validate password
            if (string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 6)
            {
                return Results.BadRequest(new { error = "Password must be at least 6 characters long" });
            }

            // Find user
            var user = await userManager.FindByIdAsync(userId);
            if (user == null)
            {
                logger.LogWarning("User not found: UserId={UserId}", userId);
                return Results.NotFound(new { error = "User not found" });
            }

            // Remove old password and set new one
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var result = await userManager.ResetPasswordAsync(user, token, dto.Password);

            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                logger.LogWarning("Failed to reset password for user {UserId}: {Errors}", userId, errors);
                return Results.BadRequest(new { error = "Failed to reset password", details = errors });
            }

            // Log audit entry
            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantId,
                UserId = username,
                Action = "SuperAdminResetPassword",
                EntityType = "User",
                EntityId = userId,
                OldValue = null,
                NewValue = $"Password reset for user {user.UserName}"
            });

            logger.LogInformation("SuperAdmin reset password for user {UserId} ({Username}) in tenant {TenantId}",
                userId, user.UserName, tenantId);
            return Results.Ok(new { message = "Password reset successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resetting user password");
            return Results.Problem("Failed to reset user password");
        }
    }

    /// <summary>
    /// DELETE /api/superadmin/tenants/{tenantId}/users/{userId}
    /// Removes a user from a tenant (revokes all access and permissions)
    /// </summary>
    private static async Task<IResult> RemoveUserFromTenant(
        string tenantId,
        string userId,
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            // Find tenant user
            var tenantUser = await db.TenantUsers
                .IgnoreQueryFilters()
                .Include(tu => tu.Permissions)
                .FirstOrDefaultAsync(tu => tu.TenantId == tenantId && tu.UserId == userId);

            if (tenantUser == null)
            {
                logger.LogWarning("TenantUser not found: TenantId={TenantId}, UserId={UserId}", tenantId, userId);
                return Results.NotFound(new { error = "User not found in this tenant" });
            }

            // Get user for audit log
            var user = await userManager.FindByIdAsync(userId);
            var username = user?.UserName ?? userId;

            // Remove permissions
            db.TenantPermissions.RemoveRange(tenantUser.Permissions);

            // Remove tenant user
            db.TenantUsers.Remove(tenantUser);

            // Remove tenant-specific tokens (if they exist)
            var tenantTokens = await db.Tokens
                .IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && t.UserId == userId)
                .ToListAsync();

            if (tenantTokens.Any())
            {
                db.Tokens.RemoveRange(tenantTokens);
            }

            await db.SaveChangesAsync();

            // Log audit entry
            var adminUsername = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantId,
                UserId = adminUsername,
                Action = "SuperAdminRemovedUser",
                EntityType = "TenantUser",
                EntityId = userId,
                OldValue = $"User {username} (Role: {tenantUser.Role})",
                NewValue = "User removed from tenant"
            });

            logger.LogInformation("SuperAdmin removed user {UserId} ({Username}) from tenant {TenantId}",
                userId, username, tenantId);
            return Results.Ok(new { message = "User removed from tenant successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing user from tenant");
            return Results.Problem("Failed to remove user from tenant");
        }
    }

    /// <summary>
    /// GET /api/superadmin/users/unassigned
    /// Lists all users who are not assigned to any tenant
    /// </summary>
    private static async Task<IResult> GetUnassignedUsers(
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager)
    {
        try
        {
            // Get all users
            var allUsers = await userManager.Users.ToListAsync();

            // Get all user IDs that have tenant assignments
            var assignedUserIds = await db.TenantUsers
                .IgnoreQueryFilters()
                .Select(tu => tu.UserId)
                .Distinct()
                .ToListAsync();

            // Filter to users without any tenant
            var unassignedUsers = allUsers
                .Where(u => !assignedUserIds.Contains(u.Id))
                .Select(u => new
                {
                    userId = u.Id,
                    username = u.UserName,
                    registeredAt = u.LockoutEnd // Using as proxy for registration date if available
                })
                .ToList();

            return Results.Ok(unassignedUsers);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to get unassigned users: {ex.Message}");
        }
    }

    // ===========================
    // SuperAdmin Role Management Endpoints
    // ===========================

    /// <summary>
    /// GET /api/superadmin/superadmins
    /// Lists all users with the SuperAdmin role
    /// </summary>
    private static async Task<IResult> GetAllSuperAdmins(
        UserManager<IdentityUser> userManager,
        ILogger<Program> logger)
    {
        try
        {
            var superAdmins = await userManager.GetUsersInRoleAsync("SuperAdmin");

            var result = superAdmins.Select(u => new SuperAdminDto(
                u.Id,
                u.UserName ?? string.Empty,
                u.Email
            )).ToList();

            logger.LogInformation("SuperAdmin: Loaded {Count} superadmins", result.Count);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading superadmins");
            return Results.Problem("Failed to load superadmins");
        }
    }

    /// <summary>
    /// POST /api/superadmin/users/{userId}/grant-superadmin
    /// Grants the SuperAdmin role to a user
    /// </summary>
    private static async Task<IResult> GrantSuperAdmin(
        string userId,
        UserManager<IdentityUser> userManager,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            // Find the user
            var user = await userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return Results.NotFound(new { error = "User not found" });
            }

            // Check if already SuperAdmin
            if (await userManager.IsInRoleAsync(user, "SuperAdmin"))
            {
                return Results.BadRequest(new { error = "User is already a SuperAdmin" });
            }

            // Grant SuperAdmin role
            var result = await userManager.AddToRoleAsync(user, "SuperAdmin");
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                logger.LogWarning("Failed to grant SuperAdmin to user {UserId}: {Errors}", userId, errors);
                return Results.BadRequest(new { error = "Failed to grant SuperAdmin role", details = errors });
            }

            // Log audit entry
            var adminUsername = context.User.Identity?.Name ?? "Unknown";
            var adminUserId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = null, // Global action
                UserId = adminUserId,
                Action = "SuperAdminGranted",
                EntityType = "User",
                EntityId = userId,
                OldValue = null,
                NewValue = $"SuperAdmin role granted to {user.UserName} by {adminUsername}"
            });

            logger.LogWarning("SuperAdmin {AdminUsername} granted SuperAdmin role to user {UserId} ({Username})",
                adminUsername, userId, user.UserName);

            return Results.Ok(new { message = $"SuperAdmin role granted to {user.UserName}" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error granting SuperAdmin role to user {UserId}", userId);
            return Results.Problem("Failed to grant SuperAdmin role");
        }
    }

    /// <summary>
    /// POST /api/superadmin/users/{userId}/revoke-superadmin
    /// Revokes the SuperAdmin role from a user
    /// </summary>
    private static async Task<IResult> RevokeSuperAdmin(
        string userId,
        UserManager<IdentityUser> userManager,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            // Find the user
            var user = await userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return Results.NotFound(new { error = "User not found" });
            }

            // Check if user is SuperAdmin
            if (!await userManager.IsInRoleAsync(user, "SuperAdmin"))
            {
                return Results.BadRequest(new { error = "User is not a SuperAdmin" });
            }

            // Safety: Cannot revoke own SuperAdmin role
            var currentUserId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == currentUserId)
            {
                return Results.BadRequest(new { error = "Cannot revoke your own SuperAdmin role" });
            }

            // Safety: Must leave at least one SuperAdmin
            var allSuperAdmins = await userManager.GetUsersInRoleAsync("SuperAdmin");
            if (allSuperAdmins.Count <= 1)
            {
                return Results.BadRequest(new { error = "Cannot revoke the last SuperAdmin. At least one SuperAdmin must remain." });
            }

            // Revoke SuperAdmin role
            var result = await userManager.RemoveFromRoleAsync(user, "SuperAdmin");
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                logger.LogWarning("Failed to revoke SuperAdmin from user {UserId}: {Errors}", userId, errors);
                return Results.BadRequest(new { error = "Failed to revoke SuperAdmin role", details = errors });
            }

            // Log audit entry
            var adminUsername = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = null, // Global action
                UserId = currentUserId,
                Action = "SuperAdminRevoked",
                EntityType = "User",
                EntityId = userId,
                OldValue = $"User {user.UserName} was SuperAdmin",
                NewValue = $"SuperAdmin role revoked by {adminUsername}"
            });

            logger.LogWarning("SuperAdmin {AdminUsername} revoked SuperAdmin role from user {UserId} ({Username})",
                adminUsername, userId, user.UserName);

            return Results.Ok(new { message = $"SuperAdmin role revoked from {user.UserName}" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error revoking SuperAdmin role from user {UserId}", userId);
            return Results.Problem("Failed to revoke SuperAdmin role");
        }
    }

    /// <summary>
    /// GET /api/superadmin/users
    /// Lists all users in the system (for SuperAdmin role management)
    /// </summary>
    private static async Task<IResult> GetAllUsers(
        UserManager<IdentityUser> userManager,
        ILogger<Program> logger)
    {
        try
        {
            var users = await userManager.Users.ToListAsync();

            var result = users.Select(u => new UserDto(
                u.Id,
                u.UserName ?? string.Empty,
                u.Email
            )).OrderBy(u => u.Username).ToList();

            logger.LogInformation("SuperAdmin: Loaded {Count} users", result.Count);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading users");
            return Results.Problem("Failed to load users");
        }
    }

    // ===========================
    // Map & Marker Monitoring Endpoints
    // ===========================

    /// <summary>
    /// GET /api/superadmin/maps
    /// Lists all maps across all tenants with statistics (paginated)
    /// </summary>
    private static async Task<IResult> GetAllMaps(
        ApplicationDbContext db,
        ILogger<Program> logger,
        int page = 1,
        int pageSize = 25,
        string? search = null)
    {
        try
        {
            if (pageSize > 100) pageSize = 100;
            if (pageSize < 1) pageSize = 25;
            if (page < 1) page = 1;

            // Build base query
            var query = db.Maps
                .IgnoreQueryFilters()
                .AsNoTracking();

            // Apply search filter
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(m => m.Name.Contains(search));

            // Get total count
            var totalCount = await query.CountAsync();

            // Load tenants dictionary for lookup
            var tenants = await db.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(t => t.Id, t => t.Name);

            // Get paginated maps
            var maps = await query
                .OrderByDescending(m => m.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Build DTOs with counts (now only for the page, not all maps)
            var mapDtos = new List<GlobalMapDto>();
            foreach (var map in maps)
            {
                var tileCount = await db.Tiles
                    .IgnoreQueryFilters()
                    .CountAsync(t => t.MapId == map.Id);

                var customMarkerCount = await db.CustomMarkers
                    .IgnoreQueryFilters()
                    .CountAsync(cm => cm.MapId == map.Id);

                mapDtos.Add(new GlobalMapDto
                {
                    Id = map.Id,
                    Name = map.Name,
                    TenantId = map.TenantId,
                    TenantName = tenants.GetValueOrDefault(map.TenantId, "Unknown"),
                    Hidden = map.Hidden,
                    Priority = map.Priority,
                    CreatedAt = map.CreatedAt,
                    TileCount = tileCount,
                    MarkerCount = 0, // Skip expensive marker count
                    CustomMarkerCount = customMarkerCount
                });
            }

            logger.LogInformation("SuperAdmin: Loaded {Count}/{Total} maps (page {Page})", mapDtos.Count, totalCount, page);

            return Results.Ok(new
            {
                page,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                maps = mapDtos
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading all maps");
            return Results.Problem("Failed to load maps");
        }
    }

    /// <summary>
    /// GET /api/superadmin/tenants/{tenantId}/maps
    /// Lists all maps for a specific tenant
    /// </summary>
    private static async Task<IResult> GetTenantMaps(
        string tenantId,
        ApplicationDbContext db,
        ILogger<Program> logger)
    {
        try
        {
            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId);

            if (tenant == null)
                return Results.NotFound(new { error = "Tenant not found" });

            var maps = await db.Maps
                .IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();

            var mapDtos = new List<GlobalMapDto>();

            foreach (var map in maps)
            {
                var tileCount = await db.Tiles
                    .IgnoreQueryFilters()
                    .CountAsync(t => t.MapId == map.Id);

                var markerCount = await db.Markers
                    .IgnoreQueryFilters()
                    .CountAsync(m => db.Grids
                        .IgnoreQueryFilters()
                        .Any(g => g.Id == m.GridId && g.Map == map.Id));

                var customMarkerCount = await db.CustomMarkers
                    .IgnoreQueryFilters()
                    .CountAsync(cm => cm.MapId == map.Id);

                mapDtos.Add(new GlobalMapDto
                {
                    Id = map.Id,
                    Name = map.Name,
                    TenantId = map.TenantId,
                    TenantName = tenant.Name,
                    Hidden = map.Hidden,
                    Priority = map.Priority,
                    CreatedAt = map.CreatedAt,
                    TileCount = tileCount,
                    MarkerCount = markerCount,
                    CustomMarkerCount = customMarkerCount
                });
            }

            logger.LogInformation("SuperAdmin: Loaded {Count} maps for tenant {TenantId}", mapDtos.Count, tenantId);
            return Results.Ok(mapDtos);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading maps for tenant {TenantId}", tenantId);
            return Results.Problem("Failed to load tenant maps");
        }
    }

    /// <summary>
    /// GET /api/superadmin/markers?page=1&pageSize=100&tenantId=...
    /// Lists all game markers across all tenants (paginated)
    /// </summary>
    private static async Task<IResult> GetAllMarkers(
        ApplicationDbContext db,
        ILogger<Program> logger,
        int page = 1,
        int pageSize = 100,
        string? tenantId = null)
    {
        try
        {
            if (pageSize > 500) pageSize = 500; // Max page size
            if (page < 1) page = 1;

            var query = db.Markers
                .IgnoreQueryFilters()
                .AsQueryable();

            if (!string.IsNullOrEmpty(tenantId))
                query = query.Where(m => m.TenantId == tenantId);

            var totalCount = await query.CountAsync();
            var markers = await query
                .OrderByDescending(m => m.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var markerDtos = new List<GlobalMarkerDto>();

            foreach (var marker in markers)
            {
                var tenant = await db.Tenants
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.Id == marker.TenantId);

                // Get grid to find map
                var grid = await db.Grids
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(g => g.Id == marker.GridId && g.TenantId == marker.TenantId);

                var map = grid != null ? await db.Maps
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(m => m.Id == grid.Map)
                    : null;

                markerDtos.Add(new GlobalMarkerDto
                {
                    Id = marker.Id,
                    Name = marker.Name,
                    Key = marker.Key,
                    TenantId = marker.TenantId,
                    TenantName = tenant?.Name ?? "Unknown",
                    GridId = marker.GridId,
                    MapId = grid?.Map ?? 0,
                    MapName = map?.Name ?? "Unknown",
                    PositionX = marker.PositionX,
                    PositionY = marker.PositionY,
                    Image = marker.Image,
                    Hidden = marker.Hidden,
                    Ready = marker.Ready,
                    MaxReady = marker.MaxReady,
                    MinReady = marker.MinReady
                });
            }

            logger.LogInformation("SuperAdmin: Loaded {Count}/{Total} markers (page {Page})",
                markerDtos.Count, totalCount, page);

            return Results.Ok(new
            {
                page,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                markers = markerDtos
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading markers");
            return Results.Problem("Failed to load markers");
        }
    }

    /// <summary>
    /// GET /api/superadmin/tenants/{tenantId}/markers?page=1&pageSize=100
    /// Lists all game markers for a specific tenant (paginated)
    /// </summary>
    private static async Task<IResult> GetTenantMarkers(
        string tenantId,
        ApplicationDbContext db,
        ILogger<Program> logger,
        int page = 1,
        int pageSize = 100)
    {
        return await GetAllMarkers(db, logger, page, pageSize, tenantId);
    }

    /// <summary>
    /// GET /api/superadmin/custom-markers?page=1&pageSize=100&tenantId=...
    /// Lists all custom markers across all tenants (paginated)
    /// </summary>
    private static async Task<IResult> GetAllCustomMarkers(
        ApplicationDbContext db,
        ILogger<Program> logger,
        int page = 1,
        int pageSize = 100,
        string? tenantId = null)
    {
        try
        {
            if (pageSize > 500) pageSize = 500; // Max page size
            if (page < 1) page = 1;

            var query = db.CustomMarkers
                .IgnoreQueryFilters()
                .AsQueryable();

            if (!string.IsNullOrEmpty(tenantId))
                query = query.Where(cm => cm.TenantId == tenantId);

            var totalCount = await query.CountAsync();
            var customMarkers = await query
                .OrderByDescending(cm => cm.PlacedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Load all referenced tenants and maps
            var tenantIds = customMarkers.Select(cm => cm.TenantId).Distinct().ToList();
            var mapIds = customMarkers.Select(cm => cm.MapId).Distinct().ToList();

            var tenants = await db.Tenants
                .IgnoreQueryFilters()
                .Where(t => tenantIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name);

            var maps = await db.Maps
                .IgnoreQueryFilters()
                .Where(m => mapIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => m.Name);

            var customMarkerDtos = new List<GlobalCustomMarkerDto>();

            foreach (var cm in customMarkers)
            {
                customMarkerDtos.Add(new GlobalCustomMarkerDto
                {
                    Id = cm.Id,
                    Title = cm.Title,
                    Description = cm.Description,
                    TenantId = cm.TenantId,
                    TenantName = tenants.GetValueOrDefault(cm.TenantId, "Unknown"),
                    MapId = cm.MapId,
                    MapName = maps.GetValueOrDefault(cm.MapId, "Unknown"),
                    GridId = cm.GridId,
                    CoordX = cm.CoordX,
                    CoordY = cm.CoordY,
                    X = cm.X,
                    Y = cm.Y,
                    Icon = cm.Icon,
                    CreatedBy = cm.CreatedBy,
                    PlacedAt = cm.PlacedAt,
                    UpdatedAt = cm.UpdatedAt,
                    Hidden = cm.Hidden
                });
            }

            logger.LogInformation("SuperAdmin: Loaded {Count}/{Total} custom markers (page {Page})",
                customMarkerDtos.Count, totalCount, page);

            return Results.Ok(new
            {
                page,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                customMarkers = customMarkerDtos
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading custom markers");
            return Results.Problem("Failed to load custom markers");
        }
    }

    /// <summary>
    /// GET /api/superadmin/tenants/{tenantId}/custom-markers?page=1&pageSize=100
    /// Lists all custom markers for a specific tenant (paginated)
    /// </summary>
    private static async Task<IResult> GetTenantCustomMarkers(
        string tenantId,
        ApplicationDbContext db,
        ILogger<Program> logger,
        int page = 1,
        int pageSize = 100)
    {
        return await GetAllCustomMarkers(db, logger, page, pageSize, tenantId);
    }

    /// <summary>
    /// GET /api/superadmin/tenants/{tenantId}/statistics
    /// Gets comprehensive statistics for a tenant (maps, markers, grids, tiles, storage)
    /// </summary>
    private static async Task<IResult> GetTenantStatistics(
        string tenantId,
        ApplicationDbContext db,
        ILogger<Program> logger)
    {
        try
        {
            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId);

            if (tenant == null)
                return Results.NotFound(new { error = "Tenant not found" });

            var mapCount = await db.Maps
                .IgnoreQueryFilters()
                .CountAsync(m => m.TenantId == tenantId);

            var gridCount = await db.Grids
                .IgnoreQueryFilters()
                .CountAsync(g => g.TenantId == tenantId);

            var tileCount = await db.Tiles
                .IgnoreQueryFilters()
                .CountAsync(t => t.TenantId == tenantId);

            var markerCount = await db.Markers
                .IgnoreQueryFilters()
                .CountAsync(m => m.TenantId == tenantId);

            var customMarkerCount = await db.CustomMarkers
                .IgnoreQueryFilters()
                .CountAsync(cm => cm.TenantId == tenantId);

            var userCount = await db.TenantUsers
                .IgnoreQueryFilters()
                .CountAsync(tu => tu.TenantId == tenantId);

            var tokenCount = await db.Tokens
                .IgnoreQueryFilters()
                .CountAsync(t => t.TenantId == tenantId);

            var stats = new TenantStatisticsDto
            {
                TenantId = tenant.Id,
                TenantName = tenant.Name,
                MapCount = mapCount,
                GridCount = gridCount,
                TileCount = tileCount,
                MarkerCount = markerCount,
                CustomMarkerCount = customMarkerCount,
                UserCount = userCount,
                TokenCount = tokenCount,
                StorageUsageMB = tenant.CurrentStorageMB,
                StorageQuotaMB = tenant.StorageQuotaMB
            };

            logger.LogInformation("SuperAdmin: Loaded statistics for tenant {TenantId}", tenantId);
            return Results.Ok(stats);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading tenant statistics for {TenantId}", tenantId);
            return Results.Problem("Failed to load tenant statistics");
        }
    }

    /// <summary>
    /// DELETE /api/superadmin/tenants/{tenantId}/maps/{mapId}
    /// Deletes a map and all associated data (tiles, markers, custom markers) for a tenant
    /// </summary>
    private static async Task<IResult> DeleteTenantMap(
        string tenantId,
        int mapId,
        ApplicationDbContext db,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            var map = await db.Maps
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.Id == mapId && m.TenantId == tenantId);

            if (map == null)
                return Results.NotFound(new { error = "Map not found" });

            // Count related entities before deletion
            var tileCount = await db.Tiles
                .IgnoreQueryFilters()
                .CountAsync(t => t.MapId == mapId && t.TenantId == tenantId);

            var customMarkerCount = await db.CustomMarkers
                .IgnoreQueryFilters()
                .CountAsync(cm => cm.MapId == mapId && cm.TenantId == tenantId);

            // Delete map (cascade will handle tiles, markers, custom markers)
            db.Maps.Remove(map);
            await db.SaveChangesAsync();

            // Log audit entry
            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = tenantId,
                UserId = username,
                Action = "SuperAdminDeletedMap",
                EntityType = "Map",
                EntityId = mapId.ToString(),
                OldValue = $"Map '{map.Name}' with {tileCount} tiles and {customMarkerCount} custom markers",
                NewValue = "Map deleted"
            });

            logger.LogWarning("SuperAdmin {Username} deleted map {MapId} ({MapName}) for tenant {TenantId}",
                username, mapId, map.Name, tenantId);

            return Results.Ok(new
            {
                message = "Map deleted successfully",
                mapId,
                mapName = map.Name,
                tilesDeleted = tileCount,
                customMarkersDeleted = customMarkerCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting map {MapId} for tenant {TenantId}", mapId, tenantId);
            return Results.Problem("Failed to delete map");
        }
    }

    /// <summary>
    /// POST /api/superadmin/users/{userId}/assign-tenant
    /// Assigns an unassigned user to a tenant with specified role and permissions
    /// </summary>
    private static async Task<IResult> AssignUserToTenant(
        string userId,
        AssignUserToTenantDto dto,
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager,
        ILogger<Program> logger,
        ClaimsPrincipal adminUser)
    {
        try
        {
            var adminUsername = adminUser.Identity?.Name ?? "unknown";

            // Validate user exists
            var user = await userManager.FindByIdAsync(userId);
            if (user == null)
                return Results.NotFound(new { error = "User not found" });

            // Validate tenant exists
            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == dto.TenantId);

            if (tenant == null)
                return Results.NotFound(new { error = "Tenant not found" });

            // Check if user is already in a tenant
            var existingAssignment = await db.TenantUsers
                .IgnoreQueryFilters()
                .AnyAsync(tu => tu.UserId == userId);

            if (existingAssignment)
                return Results.BadRequest(new { error = "User is already assigned to a tenant" });

            // Validate role
            if (dto.Role != TenantRole.TenantAdmin.ToClaimValue() && dto.Role != TenantRole.TenantUser.ToClaimValue())
                return Results.BadRequest(new { error = $"Role must be '{TenantRole.TenantAdmin.ToClaimValue()}' or '{TenantRole.TenantUser.ToClaimValue()}'" });

            // Validate permissions
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
                    return Results.BadRequest(new { error = $"Invalid permission: {permission}" });
            }

            // Create TenantUser with immediate approval (JoinedAt = now)
            var tenantUser = new HnHMapperServer.Core.Models.TenantUserEntity
            {
                TenantId = dto.TenantId,
                UserId = userId,
                Role = dto.Role.ToTenantRole(),
                JoinedAt = DateTime.UtcNow // Immediate approval for SuperAdmin assignment
            };

            db.TenantUsers.Add(tenantUser);
            await db.SaveChangesAsync();

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

            // Log audit
            db.AuditLogs.Add(new HnHMapperServer.Core.Models.AuditLogEntity
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                UserId = adminUsername,
                TenantId = dto.TenantId,
                Action = "SuperAdminAssignedUser",
                EntityType = "TenantUser",
                EntityId = userId,
                NewValue = $"User {user.UserName} assigned to tenant {tenant.Name} as {dto.Role} with permissions: {string.Join(", ", dto.Permissions)}"
            });
            await db.SaveChangesAsync();

            logger.LogInformation("SuperAdmin {AdminUsername} assigned user {UserId} ({Username}) to tenant {TenantId} as {Role}",
                adminUsername, userId, user.UserName, dto.TenantId, dto.Role);

            return Results.Ok(new
            {
                message = "User assigned to tenant successfully",
                tenantId = dto.TenantId,
                tenantName = tenant.Name,
                role = dto.Role,
                permissions = dto.Permissions
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error assigning user to tenant");
            return Results.Problem("Failed to assign user to tenant");
        }
    }

    // ===========================
    // Cross-Tenant Map Viewing Endpoints
    // ===========================

    /// <summary>
    /// GET /api/superadmin/tenants/{tenantId}/map-view-data
    /// Returns all data needed for map viewing: markers, maps, and config
    /// </summary>
    private static async Task<IResult> GetTenantMapViewData(
        string tenantId,
        ApplicationDbContext db,
        HnHMapperServer.Api.Services.MapRevisionCache revisionCache,
        ILogger<Program> logger)
    {
        try
        {
            // Verify tenant exists
            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId);

            if (tenant == null)
                return Results.NotFound(new { error = "Tenant not found" });

            // Get markers (bypassing tenant filter)
            var markers = await db.Markers
                .IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId)
                .ToListAsync();

            var markerModels = markers.Select(m => {
                // Get grid to find map
                var grid = db.Grids
                    .IgnoreQueryFilters()
                    .FirstOrDefault(g => g.Id == m.GridId && g.TenantId == tenantId);

                return new
                {
                    Id = m.Id,
                    Name = m.Name,
                    Map = grid?.Map ?? 0,
                    Position = new { X = m.PositionX, Y = m.PositionY },
                    Image = m.Image,
                    Hidden = m.Hidden,
                    MinReady = m.MinReady,
                    MaxReady = m.MaxReady,
                    Ready = m.Ready
                };
            }).ToList();

            // Get maps
            var config = await db.Config
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId)
                .ToListAsync();

            var mainMapIdStr = config.FirstOrDefault(c => c.Key == "mainMapId")?.Value;
            int? mainMapId = null;
            if (!string.IsNullOrEmpty(mainMapIdStr) && int.TryParse(mainMapIdStr, out var parsedMainMapId))
                mainMapId = parsedMainMapId;

            var maps = await db.Maps
                .IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && !m.Hidden)
                .OrderByDescending(m => m.Priority)
                .ThenBy(m => m.Name)
                .ToListAsync();

            var mapModels = maps.Select(m => new
            {
                ID = m.Id,
                MapInfo = new
                {
                    Name = m.Name,
                    Hidden = m.Hidden,
                    Priority = m.Priority,
                    Revision = revisionCache.Get(m.Id),
                    IsMainMap = mainMapId.HasValue && mainMapId.Value == m.Id
                },
                Size = 0
            }).ToList();

            // Get config
            var title = config.FirstOrDefault(c => c.Key == "title")?.Value ?? "HnH Mapper";

            // Superadmin gets all permissions for viewing
            var permissions = new[] { "Map", "Markers", "Pointer", "Upload", "Writer" };

            var response = new
            {
                Markers = markerModels,
                Maps = mapModels,
                Config = new
                {
                    Title = title,
                    Permissions = permissions,
                    MainMapId = mainMapId
                }
            };

            logger.LogInformation("SuperAdmin: Loaded map view data for tenant {TenantId} ({MapCount} maps, {MarkerCount} markers)",
                tenantId, mapModels.Count, markerModels.Count);

            return Results.Json(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading map view data for tenant {TenantId}", tenantId);
            return Results.Problem("Failed to load map view data");
        }
    }

    /// <summary>
    /// GET /api/superadmin/tenants/{tenantId}/custom-markers-data?mapId={mapId}
    /// Returns custom markers for a tenant, optionally filtered by map ID
    /// </summary>
    private static async Task<IResult> GetTenantCustomMarkersData(
        string tenantId,
        ApplicationDbContext db,
        ILogger<Program> logger,
        int? mapId = null)
    {
        try
        {
            // Verify tenant exists
            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId);

            if (tenant == null)
                return Results.NotFound(new { error = "Tenant not found" });

            var query = db.CustomMarkers
                .IgnoreQueryFilters()
                .Where(cm => cm.TenantId == tenantId);

            if (mapId.HasValue)
                query = query.Where(cm => cm.MapId == mapId.Value);

            var customMarkers = await query.ToListAsync();

            var response = customMarkers.Select(cm => new
            {
                Id = cm.Id,
                MapId = cm.MapId,
                GridId = cm.GridId,
                CoordX = cm.CoordX,
                CoordY = cm.CoordY,
                X = cm.X,
                Y = cm.Y,
                Title = cm.Title,
                Description = cm.Description,
                Icon = cm.Icon,
                CreatedBy = cm.CreatedBy,
                PlacedAt = cm.PlacedAt,
                UpdatedAt = cm.UpdatedAt,
                Hidden = cm.Hidden
            }).ToList();

            logger.LogInformation("SuperAdmin: Loaded {Count} custom markers for tenant {TenantId} (mapId: {MapId})",
                response.Count, tenantId, mapId);

            return Results.Json(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading custom markers for tenant {TenantId}", tenantId);
            return Results.Problem("Failed to load custom markers");
        }
    }

    // ===========================
    // Public Maps Management Endpoints
    // ===========================

    /// <summary>
    /// GET /api/superadmin/public-maps
    /// Lists all public maps with generation status
    /// </summary>
    private static async Task<IResult> GetAllPublicMaps(
        IPublicMapService publicMapService,
        ILogger<Program> logger)
    {
        try
        {
            var publicMaps = await publicMapService.GetAllPublicMapsAsync();
            logger.LogInformation("SuperAdmin: Loaded {Count} public maps", publicMaps.Count);
            return Results.Ok(publicMaps);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading public maps");
            return Results.Problem("Failed to load public maps");
        }
    }

    /// <summary>
    /// POST /api/superadmin/public-maps
    /// Creates a new public map
    /// </summary>
    private static async Task<IResult> CreatePublicMap(
        CreatePublicMapDto dto,
        IPublicMapService publicMapService,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            var username = context.User.Identity?.Name ?? "Unknown";
            var publicMap = await publicMapService.CreatePublicMapAsync(dto, username);

            await auditService.LogAsync(new AuditEntry
            {
                TenantId = null, // Global
                UserId = username,
                Action = "PublicMapCreated",
                EntityType = "PublicMap",
                EntityId = publicMap.Id,
                NewValue = $"Created public map '{publicMap.Name}' (slug: {publicMap.Id})"
            });

            logger.LogInformation("SuperAdmin {Username} created public map {PublicMapId}", username, publicMap.Id);
            return Results.Created($"/api/superadmin/public-maps/{publicMap.Id}", publicMap);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating public map");
            return Results.Problem("Failed to create public map");
        }
    }

    /// <summary>
    /// GET /api/superadmin/public-maps/{id}
    /// Gets a public map with its sources
    /// </summary>
    private static async Task<IResult> GetPublicMap(
        string id,
        IPublicMapService publicMapService,
        ILogger<Program> logger)
    {
        try
        {
            var publicMap = await publicMapService.GetPublicMapAsync(id);
            if (publicMap == null)
            {
                return Results.NotFound(new { error = "Public map not found" });
            }

            return Results.Ok(publicMap);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading public map {PublicMapId}", id);
            return Results.Problem("Failed to load public map");
        }
    }

    /// <summary>
    /// PUT /api/superadmin/public-maps/{id}
    /// Updates a public map
    /// </summary>
    private static async Task<IResult> UpdatePublicMap(
        string id,
        UpdatePublicMapDto dto,
        IPublicMapService publicMapService,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            var publicMap = await publicMapService.UpdatePublicMapAsync(id, dto);

            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = null,
                UserId = username,
                Action = "PublicMapUpdated",
                EntityType = "PublicMap",
                EntityId = id,
                NewValue = $"Updated public map: Name={dto.Name}, IsActive={dto.IsActive}, AutoRegenerate={dto.AutoRegenerate}"
            });

            logger.LogInformation("SuperAdmin {Username} updated public map {PublicMapId}", username, id);
            return Results.Ok(publicMap);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating public map {PublicMapId}", id);
            return Results.Problem("Failed to update public map");
        }
    }

    /// <summary>
    /// DELETE /api/superadmin/public-maps/{id}
    /// Deletes a public map and its generated tiles
    /// </summary>
    private static async Task<IResult> DeletePublicMap(
        string id,
        IPublicMapService publicMapService,
        IHttpClientFactory httpClientFactory,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            await publicMapService.DeletePublicMapAsync(id);

            // Invalidate Web service cache (in-memory + OutputCache)
            try
            {
                var webClient = httpClientFactory.CreateClient("Web");
                await webClient.PostAsync($"/internal/public-cache/invalidate/{id}", null);
                logger.LogInformation("Invalidated Web cache for deleted public map {PublicMapId}", id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to invalidate Web cache for {PublicMapId}", id);
            }

            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = null,
                UserId = username,
                Action = "PublicMapDeleted",
                EntityType = "PublicMap",
                EntityId = id,
                NewValue = "Public map and tiles deleted"
            });

            logger.LogWarning("SuperAdmin {Username} deleted public map {PublicMapId}", username, id);
            return Results.Ok(new { message = "Public map deleted successfully" });
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting public map {PublicMapId}", id);
            return Results.Problem("Failed to delete public map");
        }
    }

    /// <summary>
    /// POST /api/superadmin/public-maps/{id}/sources
    /// Adds a source map to a public map
    /// </summary>
    private static async Task<IResult> AddPublicMapSource(
        string id,
        AddPublicMapSourceDto dto,
        IPublicMapService publicMapService,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            var username = context.User.Identity?.Name ?? "Unknown";
            var source = await publicMapService.AddSourceAsync(id, dto, username);

            await auditService.LogAsync(new AuditEntry
            {
                TenantId = dto.TenantId,
                UserId = username,
                Action = "PublicMapSourceAdded",
                EntityType = "PublicMapSource",
                EntityId = source.Id.ToString(),
                NewValue = $"Added source map {dto.TenantId}/{dto.MapId} to public map {id}"
            });

            logger.LogInformation("SuperAdmin {Username} added source {TenantId}/{MapId} to public map {PublicMapId}",
                username, dto.TenantId, dto.MapId, id);
            return Results.Created($"/api/superadmin/public-maps/{id}/sources/{source.Id}", source);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding source to public map {PublicMapId}", id);
            return Results.Problem("Failed to add source to public map");
        }
    }

    /// <summary>
    /// DELETE /api/superadmin/public-maps/{id}/sources/{sourceId}
    /// Removes a source from a public map
    /// </summary>
    private static async Task<IResult> RemovePublicMapSource(
        string id,
        int sourceId,
        IPublicMapService publicMapService,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            await publicMapService.RemoveSourceAsync(id, sourceId);

            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = null,
                UserId = username,
                Action = "PublicMapSourceRemoved",
                EntityType = "PublicMapSource",
                EntityId = sourceId.ToString(),
                NewValue = $"Removed source {sourceId} from public map {id}"
            });

            logger.LogInformation("SuperAdmin {Username} removed source {SourceId} from public map {PublicMapId}",
                username, sourceId, id);
            return Results.Ok(new { message = "Source removed successfully" });
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing source {SourceId} from public map {PublicMapId}", sourceId, id);
            return Results.Problem("Failed to remove source from public map");
        }
    }

    /// <summary>
    /// POST /api/superadmin/public-maps/{id}/regenerate
    /// Triggers regeneration of a public map
    /// </summary>
    private static async Task<IResult> TriggerPublicMapRegeneration(
        string id,
        IPublicMapService publicMapService,
        IPublicMapGenerationService generationService,
        IAuditService auditService,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            // Verify public map exists
            var publicMap = await publicMapService.GetPublicMapAsync(id);
            if (publicMap == null)
            {
                return Results.NotFound(new { error = "Public map not found" });
            }

            // Check if already running
            if (await generationService.IsGenerationRunningAsync(id))
            {
                return Results.Conflict(new { error = "Generation already in progress for this public map" });
            }

            // Queue the generation
            generationService.QueueGeneration(id);

            var username = context.User.Identity?.Name ?? "Unknown";
            await auditService.LogAsync(new AuditEntry
            {
                TenantId = null,
                UserId = username,
                Action = "PublicMapRegenerationTriggered",
                EntityType = "PublicMap",
                EntityId = id,
                NewValue = "Manual regeneration triggered"
            });

            logger.LogInformation("SuperAdmin {Username} triggered regeneration for public map {PublicMapId}", username, id);
            return Results.Accepted(value: new { message = "Regeneration queued successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error triggering regeneration for public map {PublicMapId}", id);
            return Results.Problem("Failed to trigger regeneration");
        }
    }

    /// <summary>
    /// GET /api/superadmin/public-maps/{id}/status
    /// Gets generation status for a public map
    /// </summary>
    private static async Task<IResult> GetPublicMapGenerationStatus(
        string id,
        IPublicMapService publicMapService,
        IPublicMapGenerationService generationService,
        ILogger<Program> logger)
    {
        try
        {
            var status = await publicMapService.GetGenerationStatusAsync(id);
            if (status == null)
            {
                return Results.NotFound(new { error = "Public map not found" });
            }

            // Add real-time running status
            var isRunning = await generationService.IsGenerationRunningAsync(id);

            return Results.Ok(new
            {
                status.PublicMapId,
                status.Status,
                status.Progress,
                status.TileCount,
                status.LastGeneratedAt,
                status.LastGenerationDurationSeconds,
                status.Error,
                IsCurrentlyRunning = isRunning
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting generation status for public map {PublicMapId}", id);
            return Results.Problem("Failed to get generation status");
        }
    }

    /// <summary>
    /// GET /api/superadmin/available-tenant-maps
    /// Gets available tenant maps for public map source selection
    /// </summary>
    private static async Task<IResult> GetAvailableTenantMaps(
        IPublicMapService publicMapService,
        ILogger<Program> logger)
    {
        try
        {
            var availableMaps = await publicMapService.GetAvailableTenantMapsAsync();
            logger.LogInformation("SuperAdmin: Loaded {Count} available tenant maps", availableMaps.Count);
            return Results.Ok(availableMaps);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading available tenant maps");
            return Results.Problem("Failed to load available tenant maps");
        }
    }

    // ========================================
    // HMap Source Library Endpoints
    // ========================================

    /// <summary>
    /// GET /api/superadmin/hmap-sources
    /// Lists all HMap sources in the library
    /// </summary>
    private static async Task<IResult> GetAllHmapSources(
        IHmapSourceService hmapSourceService,
        ILogger<Program> logger)
    {
        try
        {
            var sources = await hmapSourceService.GetAllAsync();
            return Results.Ok(sources);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading HMap sources");
            return Results.Problem("Failed to load HMap sources");
        }
    }

    /// <summary>
    /// POST /api/superadmin/hmap-sources
    /// Uploads a new HMap source file
    /// </summary>
    private static async Task<IResult> UploadHmapSource(
        HttpRequest request,
        IHmapSourceService hmapSourceService,
        ClaimsPrincipal user,
        ILogger<Program> logger)
    {
        try
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Content-Type must be multipart/form-data" });
            }

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var name = form["name"].ToString();

            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "No file uploaded" });
            }

            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";

            await using var stream = file.OpenReadStream();
            var source = await hmapSourceService.UploadAsync(
                stream,
                file.FileName,
                string.IsNullOrWhiteSpace(name) ? null : name,
                username
            );

            logger.LogInformation("SuperAdmin {Username} uploaded HMap source {SourceId} ({FileName})",
                username, source.Id, file.FileName);

            return Results.Created($"/api/superadmin/hmap-sources/{source.Id}", source);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading HMap source");
            return Results.Problem("Failed to upload HMap source");
        }
    }

    /// <summary>
    /// GET /api/superadmin/hmap-sources/{id}
    /// Gets details of a specific HMap source
    /// </summary>
    private static async Task<IResult> GetHmapSource(
        int id,
        IHmapSourceService hmapSourceService,
        ILogger<Program> logger)
    {
        try
        {
            var source = await hmapSourceService.GetAsync(id);
            if (source == null)
            {
                return Results.NotFound(new { error = "HMap source not found" });
            }
            return Results.Ok(source);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting HMap source {SourceId}", id);
            return Results.Problem("Failed to get HMap source");
        }
    }

    /// <summary>
    /// DELETE /api/superadmin/hmap-sources/{id}
    /// Deletes an HMap source from the library
    /// </summary>
    private static async Task<IResult> DeleteHmapSource(
        int id,
        IHmapSourceService hmapSourceService,
        ClaimsPrincipal user,
        ILogger<Program> logger)
    {
        try
        {
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";

            // Check if source is in use
            var publicMapsUsing = await hmapSourceService.GetPublicMapsUsingSourceAsync(id);
            if (publicMapsUsing.Count > 0)
            {
                return Results.BadRequest(new
                {
                    error = "Cannot delete HMap source that is in use",
                    publicMapsUsing
                });
            }

            await hmapSourceService.DeleteAsync(id);

            logger.LogInformation("SuperAdmin {Username} deleted HMap source {SourceId}", username, id);
            return Results.NoContent();
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting HMap source {SourceId}", id);
            return Results.Problem("Failed to delete HMap source");
        }
    }

    /// <summary>
    /// POST /api/superadmin/hmap-sources/{id}/analyze
    /// Re-analyzes an HMap source file
    /// </summary>
    private static async Task<IResult> AnalyzeHmapSource(
        int id,
        IHmapSourceService hmapSourceService,
        ILogger<Program> logger)
    {
        try
        {
            var analysis = await hmapSourceService.AnalyzeAsync(id);
            return Results.Ok(analysis);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error analyzing HMap source {SourceId}", id);
            return Results.Problem("Failed to analyze HMap source");
        }
    }

    // ========================================
    // Public Map HMap Source Selection Endpoints
    // ========================================

    /// <summary>
    /// GET /api/superadmin/public-maps/{id}/hmap-sources
    /// Lists HMap sources selected for a public map
    /// </summary>
    private static async Task<IResult> GetPublicMapHmapSources(
        string id,
        IPublicMapService publicMapService,
        ILogger<Program> logger)
    {
        try
        {
            var exists = await publicMapService.PublicMapExistsAsync(id);
            if (!exists)
            {
                return Results.NotFound(new { error = "Public map not found" });
            }

            var sources = await publicMapService.GetHmapSourcesAsync(id);
            return Results.Ok(sources);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting HMap sources for public map {PublicMapId}", id);
            return Results.Problem("Failed to get HMap sources");
        }
    }

    /// <summary>
    /// POST /api/superadmin/public-maps/{id}/hmap-sources
    /// Adds an HMap source to a public map
    /// </summary>
    private static async Task<IResult> AddPublicMapHmapSource(
        string id,
        AddPublicMapHmapSourceDto dto,
        IPublicMapService publicMapService,
        ClaimsPrincipal user,
        ILogger<Program> logger)
    {
        try
        {
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            var source = await publicMapService.AddHmapSourceAsync(id, dto);

            logger.LogInformation("SuperAdmin {Username} added HMap source {SourceId} to public map {PublicMapId}",
                username, dto.HmapSourceId, id);

            return Results.Created($"/api/superadmin/public-maps/{id}/hmap-sources/{source.Id}", source);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding HMap source to public map {PublicMapId}", id);
            return Results.Problem("Failed to add HMap source");
        }
    }

    /// <summary>
    /// DELETE /api/superadmin/public-maps/{id}/hmap-sources/{sourceId}
    /// Removes an HMap source from a public map
    /// </summary>
    private static async Task<IResult> RemovePublicMapHmapSource(
        string id,
        int sourceId,
        IPublicMapService publicMapService,
        ClaimsPrincipal user,
        ILogger<Program> logger)
    {
        try
        {
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            await publicMapService.RemoveHmapSourceAsync(id, sourceId);

            logger.LogInformation("SuperAdmin {Username} removed HMap source {SourceId} from public map {PublicMapId}",
                username, sourceId, id);

            return Results.NoContent();
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing HMap source from public map {PublicMapId}", id);
            return Results.Problem("Failed to remove HMap source");
        }
    }

    /// <summary>
    /// GET /api/superadmin/public-maps/{id}/hmap-sources/analysis
    /// Analyzes source contributions for a public map
    /// </summary>
    private static async Task<IResult> AnalyzePublicMapHmapSourceContributions(
        string id,
        IPublicMapService publicMapService,
        ILogger<Program> logger)
    {
        try
        {
            var exists = await publicMapService.PublicMapExistsAsync(id);
            if (!exists)
            {
                return Results.NotFound(new { error = "Public map not found" });
            }

            var analysis = await publicMapService.AnalyzeSourceContributionsAsync(id);
            return Results.Ok(analysis);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error analyzing HMap source contributions for public map {PublicMapId}", id);
            return Results.Problem("Failed to analyze source contributions");
        }
    }

    /// <summary>
    /// POST /api/superadmin/public-maps/{id}/generate-from-hmap
    /// Generates a public map from its HMap sources
    /// </summary>
    private static async Task<IResult> GenerateFromHmapSources(
        string id,
        IPublicMapService publicMapService,
        IPublicMapGenerationService generationService,
        ClaimsPrincipal user,
        ILogger<Program> logger)
    {
        try
        {
            var exists = await publicMapService.PublicMapExistsAsync(id);
            if (!exists)
            {
                return Results.NotFound(new { error = "Public map not found" });
            }

            var isRunning = await generationService.IsGenerationRunningAsync(id);
            if (isRunning)
            {
                return Results.Conflict(new { error = "Generation is already in progress for this map" });
            }

            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            logger.LogInformation("SuperAdmin {Username} triggered HMap source generation for public map {PublicMapId}",
                username, id);

            // Start generation in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await generationService.StartGenerationFromHmapSourcesAsync(id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background HMap generation failed for public map {PublicMapId}", id);
                }
            });

            return Results.Json(new
            {
                message = "Generation from HMap sources started",
                publicMapId = id,
                statusUrl = $"/api/superadmin/public-maps/{id}/status"
            }, statusCode: StatusCodes.Status202Accepted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting HMap generation for public map {PublicMapId}", id);
            return Results.Problem("Failed to start generation");
        }
    }
}

/// <summary>
/// DTO for SuperAdmin user information
/// </summary>
public record SuperAdminDto(string UserId, string Username, string? Email);

/// <summary>
/// DTO for basic user information
/// </summary>
public record UserDto(string UserId, string Username, string? Email);

/// <summary>
/// DTO for updating a configuration value
/// </summary>
public record UpdateConfigDto(string Value);

/// <summary>
/// DTO for updating user permissions
/// </summary>
public record UpdateUserPermissionsDto(List<string> Permissions);

/// <summary>
/// DTO for updating user role
/// </summary>
public record UpdateUserRoleDto(string Role);

/// <summary>
/// DTO for resetting user password
/// </summary>
public record ResetPasswordDto(string Password);

/// <summary>
/// DTO for assigning a user to a tenant
/// </summary>
public record AssignUserToTenantDto(
    string TenantId,
    string Role,
    List<string> Permissions
);
