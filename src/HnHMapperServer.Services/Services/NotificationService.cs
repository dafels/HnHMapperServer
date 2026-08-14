using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Implementation of notification service.
/// Manages creation, retrieval, and lifecycle of user notifications.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<NotificationService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUpdateNotificationService _updateNotificationService;

    public NotificationService(
        ApplicationDbContext db,
        ILogger<NotificationService> logger,
        IServiceScopeFactory scopeFactory,
        IUpdateNotificationService updateNotificationService)
    {
        _db = db;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _updateNotificationService = updateNotificationService;
    }

    /// <summary>
    /// Create a new notification.
    /// </summary>
    public async Task<NotificationDto> CreateAsync(CreateNotificationDto dto)
    {
        var entity = new NotificationEntity
        {
            TenantId = dto.TenantId,
            UserId = dto.UserId,
            Type = dto.Type,
            Title = Truncate(dto.Title, 200),
            Message = Truncate(dto.Message, 1000),
            ActionType = dto.ActionType,
            ActionData = dto.ActionData,
            Priority = dto.Priority,
            IsRead = false,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = dto.ExpiresAt
        };

        _db.Notifications.Add(entity);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Created notification {Id} of type {Type} for tenant {TenantId}",
            entity.Id, entity.Type, entity.TenantId);

        // Live delivery for every notification type; SSE endpoints filter by tenant/user.
        _updateNotificationService.NotifyNotificationCreated(MapToEventDto(entity));

        var notificationDto = MapToDto(entity);

        // Send to Discord if enabled (fire-and-forget, don't block notification creation)
        // Create new scope to avoid DbContext concurrency issues
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
                var discordWebhookService = scope.ServiceProvider.GetRequiredService<IDiscordWebhookService>();

                var tenant = await tenantService.GetTenantAsync(dto.TenantId);
                if (tenant?.DiscordNotificationsEnabled == true &&
                    !string.IsNullOrWhiteSpace(tenant.DiscordWebhookUrl))
                {
                    await discordWebhookService.SendNotificationAsync(notificationDto, tenant.DiscordWebhookUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to send Discord notification for notification {Id}",
                    entity.Id);
            }
        });

        return notificationDto;
    }

    /// <summary>
    /// Get a notification by ID.
    /// </summary>
    public async Task<NotificationDto?> GetByIdAsync(int id)
    {
        var entity = await _db.Notifications.FindAsync(id);
        return entity == null ? null : MapToDto(entity);
    }

    /// <summary>
    /// Get notifications for a specific user.
    /// </summary>
    public async Task<List<NotificationDto>> GetUserNotificationsAsync(
        string userId,
        bool includeRead = true,
        int limit = 50)
    {
        var query = _db.Notifications
            .Where(n => n.UserId == userId || n.UserId == null)
            .Where(n => n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow);

        if (!includeRead)
        {
            query = query.Where(n => !n.IsRead);
        }

        var entities = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return entities.Select(MapToDto).ToList();
    }

    /// <summary>
    /// Query notifications with filtering.
    /// </summary>
    public async Task<List<NotificationDto>> QueryAsync(NotificationQuery query)
    {
        var dbQuery = _db.Notifications.AsQueryable();

        // Apply filters
        if (query.TenantId != null)
            dbQuery = dbQuery.Where(n => n.TenantId == query.TenantId);

        if (query.UserId != null)
            dbQuery = dbQuery.Where(n => n.UserId == query.UserId || n.UserId == null);

        if (query.Type != null)
            dbQuery = dbQuery.Where(n => n.Type == query.Type);

        if (query.IsRead != null)
            dbQuery = dbQuery.Where(n => n.IsRead == query.IsRead.Value);

        if (!query.IncludeExpired)
            dbQuery = dbQuery.Where(n => n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow);

        // Execute query with pagination
        var entities = await dbQuery
            .OrderByDescending(n => n.CreatedAt)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync();

        return entities.Select(MapToDto).ToList();
    }

    /// <summary>
    /// Mark a notification as read.
    /// </summary>
    public async Task<bool> MarkAsReadAsync(int id, string userId)
    {
        var entity = await _db.Notifications.FindAsync(id);
        if (entity == null)
            return false;

        // Authorization check: user can only mark their own notifications as read
        if (entity.UserId != null && entity.UserId != userId)
            return false;

        entity.IsRead = true;
        await _db.SaveChangesAsync();

        _logger.LogDebug(
            "Marked notification {Id} as read for user {UserId}",
            id, userId);

        return true;
    }

    /// <summary>
    /// Mark all notifications as read for a user.
    /// </summary>
    public async Task<int> MarkAllAsReadAsync(string userId)
    {
        var count = await _db.Notifications
            .Where(n => (n.UserId == userId || n.UserId == null) && !n.IsRead)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(n => n.IsRead, true));

        _logger.LogInformation(
            "Marked {Count} notifications as read for user {UserId}",
            count, userId);

        return count;
    }

    /// <summary>
    /// Dismiss (delete) a notification.
    /// </summary>
    public async Task<bool> DismissAsync(int id, string userId)
    {
        var entity = await _db.Notifications.FindAsync(id);
        if (entity == null)
            return false;

        // Authorization check
        if (entity.UserId != null && entity.UserId != userId)
            return false;

        _db.Notifications.Remove(entity);
        await _db.SaveChangesAsync();

        _logger.LogDebug(
            "Dismissed notification {Id} for user {UserId}",
            id, userId);

        return true;
    }

    /// <summary>
    /// Delete all read notifications for a user.
    /// </summary>
    public async Task<int> DeleteAllReadAsync(string userId)
    {
        var count = await _db.Notifications
            .Where(n => (n.UserId == userId || n.UserId == null) && n.IsRead)
            .ExecuteDeleteAsync();

        _logger.LogInformation(
            "Deleted {Count} read notifications for user {UserId}",
            count, userId);

        return count;
    }

    /// <summary>
    /// Delete expired notifications across all tenants (background cleanup).
    /// Runs without an HttpContext, so the tenant query filter (which would resolve to
    /// TenantId == NULL and match nothing) must be bypassed. Returns the deleted ids so the
    /// caller can broadcast dismissals to open clients.
    /// </summary>
    public async Task<List<int>> DeleteExpiredAsync()
    {
        var now = DateTime.UtcNow;
        var expiredIds = await _db.Notifications
            .IgnoreQueryFilters()
            .Where(n => n.ExpiresAt != null && n.ExpiresAt < now)
            .Select(n => n.Id)
            .ToListAsync();

        if (expiredIds.Count == 0)
            return expiredIds;

        await _db.Notifications
            .IgnoreQueryFilters()
            .Where(n => n.ExpiresAt != null && n.ExpiresAt < now)
            .ExecuteDeleteAsync();

        _logger.LogInformation(
            "Deleted {Count} expired notifications",
            expiredIds.Count);

        return expiredIds;
    }

    /// <summary>
    /// Get unread notification count for a user.
    /// </summary>
    public async Task<int> GetUnreadCountAsync(string userId)
    {
        return await _db.Notifications
            .Where(n => (n.UserId == userId || n.UserId == null) && !n.IsRead)
            .Where(n => n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow)
            .CountAsync();
    }

    /// <summary>
    /// SQLite does not enforce the model's HasMaxLength, so cap in code
    /// (Title 200 / Message 1000 per the entity configuration).
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }

    /// <summary>
    /// Map NotificationEntity to NotificationDto.
    /// </summary>
    private static NotificationDto MapToDto(NotificationEntity entity)
    {
        return new NotificationDto
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            UserId = entity.UserId,
            Type = entity.Type,
            Title = entity.Title,
            Message = entity.Message,
            ActionType = entity.ActionType,
            ActionData = entity.ActionData,
            IsRead = entity.IsRead,
            CreatedAt = entity.CreatedAt,
            ExpiresAt = entity.ExpiresAt,
            Priority = entity.Priority
        };
    }

    /// <summary>
    /// Map NotificationEntity to NotificationEventDto (for SSE).
    /// </summary>
    public static NotificationEventDto MapToEventDto(NotificationEntity entity)
    {
        return new NotificationEventDto
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            UserId = entity.UserId,
            Type = entity.Type,
            Title = entity.Title,
            Message = entity.Message,
            ActionType = entity.ActionType,
            ActionData = entity.ActionData,
            Priority = entity.Priority,
            CreatedAt = entity.CreatedAt
        };
    }
}
