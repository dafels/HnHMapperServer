using HnHMapperServer.Web.Models;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Service interface for tenant management operations
/// </summary>
public interface ITenantService
{
    /// <summary>
    /// Gets all tenants for the currently logged-in user
    /// </summary>
    Task<List<TenantDto>> GetUserTenantsAsync();

    /// <summary>
    /// Gets all pending user approvals for the specified tenant
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    Task<List<PendingUserDto>> GetPendingUsersAsync(string tenantId);

    /// <summary>
    /// Approves a pending user with specified permissions
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="userId">The user ID to approve</param>
    /// <param name="permissions">List of permissions to grant</param>
    Task<bool> ApproveUserAsync(string tenantId, string userId, List<string> permissions);

    /// <summary>
    /// Rejects a pending user registration
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="userId">The user ID to reject</param>
    Task<bool> RejectUserAsync(string tenantId, string userId);

    /// <summary>
    /// Gets all users for the specified tenant (for TenantUserManagement)
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    Task<List<TenantUserDto>> GetTenantUsersAsync(string tenantId);

    /// <summary>
    /// Updates a user's permissions in the tenant
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="userId">The user ID</param>
    /// <param name="permissions">New list of permissions</param>
    Task<bool> UpdateUserPermissionsAsync(string tenantId, string userId, List<string> permissions);

    /// <summary>
    /// Removes a user from the tenant
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="userId">The user ID to remove</param>
    Task<bool> RemoveUserAsync(string tenantId, string userId);
}
