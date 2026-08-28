using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

public class TenantService : ITenantService
{
    private readonly ITenantRepository _tenantRepository;
    private readonly TenantNameService _tenantNameService;
    private readonly ILogger<TenantService> _logger;

    public TenantService(
        ITenantRepository tenantRepository,
        TenantNameService tenantNameService,
        ILogger<TenantService> logger)
    {
        _tenantRepository = tenantRepository;
        _tenantNameService = tenantNameService;
        _logger = logger;
    }

    public async Task<TenantDto?> GetTenantAsync(string tenantId)
    {
        var tenant = await _tenantRepository.GetByIdAsync(tenantId);
        return tenant == null ? null : MapToDto(tenant);
    }

    public async Task<List<TenantDto>> GetAllTenantsAsync()
    {
        var tenants = await _tenantRepository.GetAllAsync();
        return tenants.Select(MapToDto).ToList();
    }

    public async Task<TenantDto> CreateTenantAsync(CreateTenantDto dto)
    {
        // Generate unique tenant ID
        var tenantId = await _tenantNameService.GenerateUniqueIdentifierAsync();

        var tenant = new TenantEntity
        {
            Id = tenantId,
            Name = tenantId, // Initially same as ID
            StorageQuotaMB = dto.StorageQuotaMB,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        var created = await _tenantRepository.CreateAsync(tenant);

        _logger.LogInformation("Created tenant {TenantId} with quota {QuotaMB}MB",
            tenantId, dto.StorageQuotaMB);

        return MapToDto(created);
    }

    public async Task<TenantDto> UpdateTenantAsync(string tenantId, UpdateTenantDto dto)
    {
        var tenant = await _tenantRepository.GetByIdAsync(tenantId);
        if (tenant == null)
        {
            throw new ArgumentException($"Tenant {tenantId} not found");
        }

        // Update properties if provided
        if (dto.Name != null)
        {
            tenant.Name = dto.Name;
        }

        if (dto.StorageQuotaMB.HasValue)
        {
            tenant.StorageQuotaMB = dto.StorageQuotaMB.Value;
            _logger.LogInformation("Updated storage quota for tenant {TenantId} to {QuotaMB}MB",
                tenantId, dto.StorageQuotaMB.Value);
        }

        if (dto.IsActive.HasValue)
        {
            tenant.IsActive = dto.IsActive.Value;
            _logger.LogInformation("Updated tenant {TenantId} active status to {IsActive}",
                tenantId, dto.IsActive.Value);
        }

        await _tenantRepository.UpdateAsync(tenant);

        return MapToDto(tenant);
    }

    public async Task DeleteTenantAsync(string tenantId)
    {
        var tenant = await _tenantRepository.GetByIdAsync(tenantId);
        if (tenant == null)
        {
            throw new ArgumentException($"Tenant {tenantId} not found");
        }

        // Prevent deletion of default tenant
        if (tenantId == "default-tenant-1")
        {
            throw new InvalidOperationException("Cannot delete the default tenant");
        }

        await _tenantRepository.DeleteAsync(tenantId);

        _logger.LogInformation("Deleted tenant {TenantId}", tenantId);
    }

    public async Task<bool> TenantExistsAsync(string tenantId)
    {
        return await _tenantRepository.ExistsAsync(tenantId);
    }

    private static TenantDto MapToDto(TenantEntity entity)
    {
        return new TenantDto
        {
            Id = entity.Id,
            Name = entity.Name,
            StorageQuotaMB = entity.StorageQuotaMB,
            CurrentStorageMB = entity.CurrentStorageMB,
            CreatedAt = entity.CreatedAt,
            IsActive = entity.IsActive,
            DiscordWebhookUrl = entity.DiscordWebhookUrl,
            DiscordNotificationsEnabled = entity.DiscordNotificationsEnabled,
            DiscordCookbookWebhookUrl = entity.DiscordCookbookWebhookUrl,
            DiscordCookbookNotificationsEnabled = entity.DiscordCookbookNotificationsEnabled
        };
    }
}
