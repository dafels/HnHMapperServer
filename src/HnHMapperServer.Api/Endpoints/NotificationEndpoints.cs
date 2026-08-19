using System.Security.Claims;
using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Notification API endpoints
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications")
            .RequireAuthorization(); // Require authentication for all notification APIs

        // Get user's notifications
        group.MapGet("", GetNotifications);

        // Live notification stream (SSE) — works on every page, unlike /map/updates
        // which requires Map permission and carries the full map traffic
        group.MapGet("stream", StreamNotifications);

        // Get notification by ID
        group.MapGet("{id:int}", GetNotificationById);

        // Get unread count
        group.MapGet("unread/count", GetUnreadCount);

        // Mark notification as read
        group.MapPut("{id:int}/read", MarkAsRead);

        // Mark all notifications as read
        group.MapPut("read-all", MarkAllAsRead);

        // Dismiss notification
        group.MapDelete("{id:int}", DismissNotification);

        // Delete all read notifications
        group.MapDelete("read", DeleteAllRead);
    }

    // CamelCase to match the /map/updates SSE contract and client expectations
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Lightweight SSE stream carrying only notification events, for the notification bell
    /// on every authenticated page. Does no DB work — it just drains the in-memory channels.
    /// </summary>
    private static async Task StreamNotifications(
        HttpContext context,
        IUpdateNotificationService updateNotificationService,
        ILogger<Program> logger)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenantId = context.Items["TenantId"] as string;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(tenantId))
        {
            context.Response.StatusCode = 401;
            return;
        }

        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Append("Connection", "keep-alive");
        context.Response.Headers.Append("Pragma", "no-cache");
        context.Response.Headers.Append("Expires", "0");

        // Kestrel would abort the connection below ~240 bytes/second; the stream is mostly idle
        var minResponseDataRateFeature = context.Features.Get<IHttpMinResponseDataRateFeature>();
        if (minResponseDataRateFeature != null)
        {
            minResponseDataRateFeature.MinDataRate = null;
        }

        // `using var` is load-bearing: disposal is the only unsubscribe — an
        // undisposed subscription buffers events forever (see /map/updates).
        using var createdSub = updateNotificationService.SubscribeToNotificationCreated();
        using var updatedSub = updateNotificationService.SubscribeToNotificationUpdated();
        using var readSub = updateNotificationService.SubscribeToNotificationRead();
        using var dismissedSub = updateNotificationService.SubscribeToNotificationDismissed();

        var created = createdSub.Reader;
        var updated = updatedSub.Reader;
        var read = readSub.Reader;
        var dismissed = dismissedSub.Reader;

        try
        {
            // Immediate first byte so the browser fires onopen right away
            await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            var idleTicks = 0;

            while (await timer.WaitForNextTickAsync(context.RequestAborted))
            {
                var sentData = false;

                // SECURITY: created/updated carry content — only this tenant's, and only
                // per-user rows addressed to this user (UserId == null is a tenant broadcast)
                while (created.TryRead(out var notification))
                {
                    if (notification.TenantId == tenantId &&
                        (notification.UserId == null || notification.UserId == userId))
                    {
                        var json = JsonSerializer.Serialize(notification, SseJsonOptions);
                        await context.Response.WriteAsync($"event: notificationCreated\ndata: {json}\n\n");
                        sentData = true;
                    }
                }

                while (updated.TryRead(out var notification))
                {
                    if (notification.TenantId == tenantId &&
                        (notification.UserId == null || notification.UserId == userId))
                    {
                        var json = JsonSerializer.Serialize(notification, SseJsonOptions);
                        await context.Response.WriteAsync($"event: notificationUpdated\ndata: {json}\n\n");
                        sentData = true;
                    }
                }

                // Read/dismiss events are id-only (same contract as /map/updates)
                while (read.TryRead(out var readId))
                {
                    var json = JsonSerializer.Serialize(new { Id = readId }, SseJsonOptions);
                    await context.Response.WriteAsync($"event: notificationRead\ndata: {json}\n\n");
                    sentData = true;
                }

                while (dismissed.TryRead(out var dismissedId))
                {
                    var json = JsonSerializer.Serialize(new { Id = dismissedId }, SseJsonOptions);
                    await context.Response.WriteAsync($"event: notificationDismissed\ndata: {json}\n\n");
                    sentData = true;
                }

                if (sentData)
                {
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    idleTicks = 0;
                }
                else
                {
                    // Keep-alive comment every ~5s of idle (VPNs/proxies drop idle connections)
                    idleTicks++;
                    if (idleTicks % 10 == 0)
                    {
                        await context.Response.WriteAsync(": keep-alive\n\n");
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Notification SSE stream closed for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in notification SSE stream");
        }
    }

    /// <summary>
    /// Get notifications for the current user
    /// </summary>
    private static async Task<IResult> GetNotifications(
        HttpContext context,
        [FromQuery] bool includeRead,
        [FromQuery] int limit,
        INotificationService notificationService)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        try
        {
            var notifications = await notificationService.GetUserNotificationsAsync(
                userId,
                includeRead,
                limit > 0 ? limit : 50);

            return Results.Json(notifications);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    /// <summary>
    /// Get a single notification by ID
    /// </summary>
    private static async Task<IResult> GetNotificationById(
        HttpContext context,
        int id,
        INotificationService notificationService)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        try
        {
            var notification = await notificationService.GetByIdAsync(id);
            if (notification == null)
            {
                return Results.NotFound(new { error = $"Notification {id} not found" });
            }

            // Check authorization - user can only access their own notifications
            if (notification.UserId != null && notification.UserId != userId)
            {
                return Results.Forbid();
            }

            return Results.Json(notification);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    /// <summary>
    /// Get unread notification count
    /// </summary>
    private static async Task<IResult> GetUnreadCount(
        HttpContext context,
        INotificationService notificationService)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        try
        {
            var count = await notificationService.GetUnreadCountAsync(userId);
            return Results.Json(new { count });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    /// <summary>
    /// Mark a notification as read
    /// </summary>
    private static async Task<IResult> MarkAsRead(
        HttpContext context,
        int id,
        INotificationService notificationService,
        IUpdateNotificationService updateNotificationService)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        try
        {
            var success = await notificationService.MarkAsReadAsync(id, userId);
            if (!success)
            {
                return Results.NotFound(new { error = $"Notification {id} not found or access denied" });
            }

            // Broadcast SSE event
            updateNotificationService.NotifyNotificationRead(id);

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    /// <summary>
    /// Mark all notifications as read for the current user
    /// </summary>
    private static async Task<IResult> MarkAllAsRead(
        HttpContext context,
        INotificationService notificationService)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        try
        {
            var count = await notificationService.MarkAllAsReadAsync(userId);
            return Results.Ok(new { count });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    /// <summary>
    /// Dismiss (delete) a notification
    /// </summary>
    private static async Task<IResult> DismissNotification(
        HttpContext context,
        int id,
        INotificationService notificationService,
        IUpdateNotificationService updateNotificationService)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        try
        {
            var success = await notificationService.DismissAsync(id, userId);
            if (!success)
            {
                return Results.NotFound(new { error = $"Notification {id} not found or access denied" });
            }

            // Broadcast SSE event
            updateNotificationService.NotifyNotificationDismissed(id);

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    /// <summary>
    /// Delete all read notifications for the current user
    /// </summary>
    private static async Task<IResult> DeleteAllRead(
        HttpContext context,
        INotificationService notificationService)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        try
        {
            var count = await notificationService.DeleteAllReadAsync(userId);
            return Results.Ok(new { count });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }
}
