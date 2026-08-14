namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// DTO for creating a new notification
/// </summary>
public class CreateNotificationDto
{
    /// <summary>
    /// Tenant ID for multi-tenancy isolation
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// User ID who should receive this notification (NULL = all users in tenant)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Type of notification (MarkerTimerExpired, StandaloneTimerExpired, etc.)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Notification title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Notification message/body
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Action type when notification is clicked
    /// </summary>
    public string? ActionType { get; set; }

    /// <summary>
    /// JSON string with action parameters
    /// </summary>
    public string? ActionData { get; set; }

    /// <summary>
    /// Priority level (Low, Normal, High, Urgent)
    /// </summary>
    public string Priority { get; set; } = "Normal";

    /// <summary>
    /// When the notification expires (NULL = never expires)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// DTO for notification data returned to clients
/// </summary>
public class NotificationDto
{
    /// <summary>
    /// Notification ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Tenant ID
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// User ID (NULL = all users)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Type of notification
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Notification title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Notification message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Action type
    /// </summary>
    public string? ActionType { get; set; }

    /// <summary>
    /// Action data (JSON)
    /// </summary>
    public string? ActionData { get; set; }

    /// <summary>
    /// Whether the notification has been read
    /// </summary>
    public bool IsRead { get; set; }

    /// <summary>
    /// When the notification was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the notification expires
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Priority level
    /// </summary>
    public string Priority { get; set; } = "Normal";
}

/// <summary>
/// DTO for notification SSE events (lightweight payload for real-time updates)
/// </summary>
public class NotificationEventDto
{
    /// <summary>
    /// Notification ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Tenant ID
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// User ID (NULL = all users)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Type of notification
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Notification title
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Notification message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Action type
    /// </summary>
    public string? ActionType { get; set; }

    /// <summary>
    /// Action data (JSON)
    /// </summary>
    public string? ActionData { get; set; }

    /// <summary>
    /// Priority level
    /// </summary>
    public string Priority { get; set; } = "Normal";

    /// <summary>
    /// When the notification was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Structured payload stored in NotificationEntity.ActionData for CookbookFoodAdded
/// digests, serialized camelCase. Carries the deep-link targets and the bell's stat
/// preview; every list is capped so the column stays bounded while TotalCount keeps
/// the real number.
/// </summary>
public class CookbookNotificationActionData
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxFoodIds = 50;
    public const int MaxFoodNames = 20;
    public const int MaxWorlds = 8;
    public const int MaxContributors = 10;
    public const int MaxPreviews = 8;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Total new foods in the digest (uncapped, unlike the lists below).</summary>
    public int TotalCount { get; set; }

    /// <summary>Ids of the new foods, insertion order, first MaxFoodIds.</summary>
    public List<int> FoodIds { get; set; } = new();

    /// <summary>Names aligned with the first entries of FoodIds, first MaxFoodNames.</summary>
    public List<string> FoodNames { get; set; } = new();

    /// <summary>Distinct genus hashes the foods were discovered in (see GameWorlds).</summary>
    public List<string> Worlds { get; set; } = new();

    /// <summary>Usernames of the uploaders, deduped, insertion order.</summary>
    public List<string> ContributorNames { get; set; } = new();

    /// <summary>New recipe variations that accompanied the new foods (not shown in the message).</summary>
    public int VariantCount { get; set; }

    /// <summary>Compact stat previews of the first MaxPreviews foods, for bell rendering.</summary>
    public List<CookbookNotificationFoodPreview> Previews { get; set; } = new();

    /// <summary>UTC time of the last merge into this digest.</summary>
    public DateTime LastUpdatedAt { get; set; }
}

/// <summary>Compact stat preview of one newly discovered food.</summary>
public class CookbookNotificationFoodPreview
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Game resource path, e.g. "gfx/invobjs/autumnsteak" — the icon source.</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Energy restored when eaten (game percent points).</summary>
    public int Energy { get; set; }

    /// <summary>Hunger cost per bite.</summary>
    public decimal Hunger { get; set; }

    public List<FoodFepDto> Feps { get; set; } = new();
}

/// <summary>
/// DTO for querying notifications
/// </summary>
public class NotificationQuery
{
    /// <summary>
    /// Filter by tenant ID
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Filter by user ID
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Filter by notification type
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Filter by read status (NULL = all, true = read only, false = unread only)
    /// </summary>
    public bool? IsRead { get; set; }

    /// <summary>
    /// Include expired notifications
    /// </summary>
    public bool IncludeExpired { get; set; } = false;

    /// <summary>
    /// Maximum number of results
    /// </summary>
    public int Limit { get; set; } = 50;

    /// <summary>
    /// Offset for pagination
    /// </summary>
    public int Offset { get; set; } = 0;
}
