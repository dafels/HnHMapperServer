namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// DTO for tenant information
/// </summary>
public class TenantDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int StorageQuotaMB { get; set; }
    public double CurrentStorageMB { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public int UserCount { get; set; }
    public double StorageUsagePercent => StorageQuotaMB > 0 ? (CurrentStorageMB / StorageQuotaMB) * 100 : 0;
    public string? DiscordWebhookUrl { get; set; }
    public bool DiscordNotificationsEnabled { get; set; }
    public string? DiscordCookbookWebhookUrl { get; set; }
    public bool DiscordCookbookNotificationsEnabled { get; set; }
}

/// <summary>
/// DTO for creating a new tenant
/// </summary>
public class CreateTenantDto
{
    public int StorageQuotaMB { get; set; } = 1024;
}

/// <summary>
/// DTO for updating tenant settings
/// </summary>
public class UpdateTenantDto
{
    public string? Name { get; set; }
    public int? StorageQuotaMB { get; set; }
    public bool? IsActive { get; set; }
}

/// <summary>
/// DTO for updating Discord notification settings
/// </summary>
public class UpdateDiscordSettingsDto
{
    /// <summary>
    /// Whether Discord notifications are enabled
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Discord webhook URL (must start with https://discord.com/api/webhooks/)
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Whether Discord notifications for cookbook discoveries are enabled
    /// </summary>
    public bool CookbookEnabled { get; set; }

    /// <summary>
    /// Discord webhook URL for cookbook discoveries; blank falls back to WebhookUrl
    /// </summary>
    public string? CookbookWebhookUrl { get; set; }
}
