using HnHMapperServer.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Service for tenant management operations via API calls
/// </summary>
public class TenantService : ITenantService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TenantService> _logger;

    public TenantService(
        IHttpClientFactory httpClientFactory,
        ILogger<TenantService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<TenantDto>> GetUserTenantsAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.GetAsync("/api/auth/tenants");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<TenantDto>>() ?? new List<TenantDto>();
            }
            else
            {
                _logger.LogWarning("Failed to get user tenants: {StatusCode}", response.StatusCode);
                return new List<TenantDto>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user tenants");
            return new List<TenantDto>();
        }
    }

    public async Task<List<PendingUserDto>> GetPendingUsersAsync(string tenantId)
    {
        try
        {
            _logger.LogInformation("GetPendingUsersAsync: Calling GET /api/tenants/{TenantId}/users/pending", tenantId);
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.GetAsync($"/api/tenants/{tenantId}/users/pending");

            _logger.LogInformation("GetPendingUsersAsync: Response Status={StatusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var users = await response.Content.ReadFromJsonAsync<List<PendingUserDto>>() ?? new List<PendingUserDto>();
                _logger.LogInformation("GetPendingUsersAsync: Received {Count} pending users", users.Count);
                return users;
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to get pending users for tenant {TenantId}: {StatusCode}, Body={Body}", tenantId, response.StatusCode, errorBody);
                return new List<PendingUserDto>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pending users for tenant {TenantId}", tenantId);
            return new List<PendingUserDto>();
        }
    }

    public async Task<bool> ApproveUserAsync(string tenantId, string userId, List<string> permissions)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var payload = new { Permissions = permissions };
            var response = await client.PostAsJsonAsync($"/api/tenants/{tenantId}/users/{userId}/approve", payload);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to approve user {UserId} for tenant {TenantId}: {StatusCode}", userId, tenantId, response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving user {UserId} for tenant {TenantId}", userId, tenantId);
            return false;
        }
    }

    public async Task<bool> RejectUserAsync(string tenantId, string userId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.DeleteAsync($"/api/tenants/{tenantId}/users/{userId}");

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to reject user {UserId} for tenant {TenantId}: {StatusCode}", userId, tenantId, response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting user {UserId} for tenant {TenantId}", userId, tenantId);
            return false;
        }
    }

    public async Task<List<TenantUserDto>> GetTenantUsersAsync(string tenantId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.GetAsync($"/api/tenants/{tenantId}/users");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<TenantUserDto>>() ?? new List<TenantUserDto>();
            }
            else
            {
                _logger.LogWarning("Failed to get tenant users for {TenantId}: {StatusCode}", tenantId, response.StatusCode);
                return new List<TenantUserDto>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenant users for {TenantId}", tenantId);
            return new List<TenantUserDto>();
        }
    }

    public async Task<bool> UpdateUserPermissionsAsync(string tenantId, string userId, List<string> permissions)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var payload = new { Permissions = permissions };
            var response = await client.PutAsJsonAsync($"/api/tenants/{tenantId}/users/{userId}/permissions", payload);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to update permissions for user {UserId} in tenant {TenantId}: {StatusCode}", userId, tenantId, response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating permissions for user {UserId} in tenant {TenantId}", userId, tenantId);
            return false;
        }
    }

    public async Task<bool> RemoveUserAsync(string tenantId, string userId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.DeleteAsync($"/api/tenants/{tenantId}/users/{userId}");

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to remove user {UserId} from tenant {TenantId}: {StatusCode}", userId, tenantId, response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing user {UserId} from tenant {TenantId}", userId, tenantId);
            return false;
        }
    }
}
