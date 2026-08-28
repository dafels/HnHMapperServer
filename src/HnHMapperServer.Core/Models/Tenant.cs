namespace HnHMapperServer.Core.Models;

/// <summary>
/// Represents a tenant (organization) in the multi-tenant system
/// </summary>
public sealed class TenantEntity
{
    /// <summary>
    /// Tenant identifier (format: icon1-icon2-number, e.g., "warrior-shield-42")
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name (same as Id)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Storage quota in megabytes
    /// </summary>
    public int StorageQuotaMB { get; set; } = 1024;

    /// <summary>
    /// Current storage usage in megabytes
    /// </summary>
    public double CurrentStorageMB { get; set; } = 0;

    /// <summary>
    /// ISO 8601 UTC timestamp when tenant was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Whether the tenant is active (1) or suspended (0)
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Discord webhook URL for sending notifications (nullable)
    /// </summary>
    public string? DiscordWebhookUrl { get; set; }

    /// <summary>
    /// Whether Discord notifications are enabled for this tenant
    /// </summary>
    public bool DiscordNotificationsEnabled { get; set; } = false;

    /// <summary>
    /// Discord webhook URL for cookbook discovery notifications (nullable; falls back to DiscordWebhookUrl when blank)
    /// </summary>
    public string? DiscordCookbookWebhookUrl { get; set; }

    /// <summary>
    /// Whether Discord notifications for cookbook discoveries are enabled for this tenant
    /// </summary>
    public bool DiscordCookbookNotificationsEnabled { get; set; } = false;

    /// <summary>
    /// UTC timestamp of the last tenant activity (grid uploads, position updates)
    /// </summary>
    public DateTime? LastActivityAt { get; set; }
}
