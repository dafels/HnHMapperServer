using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service for managing user notifications.
/// Notifications appear in the notification center and can have actions.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Create a new notification.
    /// </summary>
    /// <param name="dto">Notification data</param>
    /// <returns>Created notification</returns>
    Task<NotificationDto> CreateAsync(CreateNotificationDto dto);

    /// <summary>
    /// Get a notification by ID.
    /// </summary>
    /// <param name="id">Notification ID</param>
    /// <returns>Notification or null if not found</returns>
    Task<NotificationDto?> GetByIdAsync(int id);

    /// <summary>
    /// Get notifications for a specific user.
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="includeRead">Whether to include read notifications</param>
    /// <param name="limit">Maximum number of results</param>
    /// <returns>List of notifications</returns>
    Task<List<NotificationDto>> GetUserNotificationsAsync(string userId, bool includeRead = true, int limit = 50);

    /// <summary>
    /// Query notifications with filtering.
    /// </summary>
    /// <param name="query">Query parameters</param>
    /// <returns>List of notifications matching the query</returns>
    Task<List<NotificationDto>> QueryAsync(NotificationQuery query);

    /// <summary>
    /// Mark a notification as read.
    /// </summary>
    /// <param name="id">Notification ID</param>
    /// <param name="userId">User ID (for authorization check)</param>
    /// <returns>True if successful, false if not found or unauthorized</returns>
    Task<bool> MarkAsReadAsync(int id, string userId);

    /// <summary>
    /// Mark all notifications as read for a user.
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <returns>Number of notifications marked as read</returns>
    Task<int> MarkAllAsReadAsync(string userId);

    /// <summary>
    /// Dismiss (soft delete) a notification.
    /// </summary>
    /// <param name="id">Notification ID</param>
    /// <param name="userId">User ID (for authorization check)</param>
    /// <returns>True if successful, false if not found or unauthorized</returns>
    Task<bool> DismissAsync(int id, string userId);

    /// <summary>
    /// Delete all read notifications for a user.
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <returns>Number of notifications deleted</returns>
    Task<int> DeleteAllReadAsync(string userId);

    /// <summary>
    /// Delete expired notifications across all tenants (background cleanup; bypasses the
    /// tenant query filter, which cannot resolve outside an HTTP request).
    /// </summary>
    /// <returns>Ids of the deleted notifications, for broadcasting dismissals</returns>
    Task<List<int>> DeleteExpiredAsync();

    /// <summary>
    /// Get unread notification count for a user.
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <returns>Number of unread notifications</returns>
    Task<int> GetUnreadCountAsync(string userId);
}
