using HnHMapperServer.Core.DTOs;
using System.Net.Http.Json;
using System.Text.Json;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Service for invitation management operations via API calls
/// </summary>
public class InvitationService : IInvitationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InvitationService> _logger;

    public InvitationService(
        IHttpClientFactory httpClientFactory,
        ILogger<InvitationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<InvitationDto?> CreateInvitationAsync(string tenantId, int? expiresInDays = null, int? maxUses = null, string? preset = null)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var body = new CreateInvitationRequestDto { ExpiresInDays = expiresInDays, MaxUses = maxUses, Preset = preset };
            var response = await client.PostAsJsonAsync($"/api/tenants/{tenantId}/invitations", body);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<InvitationDto>(JsonOptions);
            }

            _logger.LogWarning("Failed to create invitation for tenant {TenantId}: {StatusCode}", tenantId, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating invitation for tenant {TenantId}", tenantId);
            return null;
        }
    }

    public async Task<List<InvitationDto>> GetInvitationsAsync(string tenantId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.GetAsync($"/api/tenants/{tenantId}/invitations");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<InvitationDto>>(JsonOptions) ?? new List<InvitationDto>();
            }

            _logger.LogWarning("Failed to get invitations for tenant {TenantId}: {StatusCode}", tenantId, response.StatusCode);
            return new List<InvitationDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting invitations for tenant {TenantId}", tenantId);
            return new List<InvitationDto>();
        }
    }

    public async Task<ValidateInvitationDto> ValidateInvitationAsync(string code)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.GetAsync($"/api/invitations/validate/{Uri.EscapeDataString(code)}");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<ValidateInvitationDto>(JsonOptions)
                       ?? new ValidateInvitationDto { IsValid = false, ErrorMessage = "Invitation is invalid or expired" };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return new ValidateInvitationDto { IsValid = false, ErrorMessage = "Too many attempts. Please wait a moment and try again." };

            _logger.LogWarning("Failed to validate invitation: {StatusCode}", response.StatusCode);
            return new ValidateInvitationDto { IsValid = false, ErrorMessage = "Could not check the invitation right now. Please try again." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating invitation");
            return new ValidateInvitationDto { IsValid = false, ErrorMessage = "Could not check the invitation right now. Please try again." };
        }
    }

    public async Task<(RedeemInvitationResultDto? Result, string? Error)> RedeemInvitationAsync(string code)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.PostAsync($"/api/invitations/{Uri.EscapeDataString(code)}/redeem", null);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<RedeemInvitationResultDto>(JsonOptions);
                return result != null ? (result, null) : (null, "Unexpected response from the server.");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return (null, "Too many attempts. Please wait a moment and try again.");

            string? error = null;
            try
            {
                var payload = await response.Content.ReadFromJsonAsync<ErrorPayload>(JsonOptions);
                error = payload?.Error;
            }
            catch { /* non-JSON error body */ }

            _logger.LogWarning("Invite redemption failed: {StatusCode} {Error}", response.StatusCode, error);
            return (null, error ?? "Invitation could not be redeemed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error redeeming invitation");
            return (null, "Could not reach the server. Please try again.");
        }
    }

    public async Task<bool> RevokeInvitationAsync(string tenantId, int invitationId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("API");
            var response = await client.DeleteAsync($"/api/tenants/{tenantId}/invitations/{invitationId}");

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning("Failed to revoke invitation {InvitationId}: {StatusCode}", invitationId, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking invitation {InvitationId}", invitationId);
            return false;
        }
    }

    private sealed class ErrorPayload
    {
        public string? Error { get; set; }
    }
}
