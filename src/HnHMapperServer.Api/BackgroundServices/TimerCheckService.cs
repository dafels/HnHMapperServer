using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that checks timers every 30 seconds
/// Sends notifications when timers expire or are nearing expiry
/// Multi-tenancy: Processes timers for all tenants
/// </summary>
public class TimerCheckService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TimerCheckService> _logger;

    // Fixed warning intervals: 1 day, 4 hours, 1 hour, 10 minutes
    private static readonly int[] WARNING_INTERVALS = [1440, 240, 60, 10];
    private const int CHECK_INTERVAL_SECONDS = 30;

    public TimerCheckService(
        IServiceScopeFactory scopeFactory,
        ILogger<TimerCheckService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Timer Check Service started (runs every 30 seconds)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var timerService = scope.ServiceProvider.GetRequiredService<ITimerService>();
                var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
                var updateNotificationService = scope.ServiceProvider.GetRequiredService<IUpdateNotificationService>();
                var timerWarningService = scope.ServiceProvider.GetRequiredService<ITimerWarningService>();

                // Get all active tenants
                var tenants = await db.Tenants
                    .IgnoreQueryFilters()
                    .Where(t => t.IsActive)
                    .Select(t => t.Id)
                    .ToListAsync(stoppingToken);

                int expiredCount = 0;
                int warningCount = 0;

                foreach (var tenantId in tenants)
                {
                    // Get timers that need processing for this tenant
                    var timers = await timerService.GetTimersNeedingProcessingAsync(tenantId);

                    foreach (var timer in timers)
                    {
                        var now = DateTime.UtcNow;
                        var timeUntilReady = (timer.ReadyAt - now).TotalMinutes;

                        // Timer has expired - send notification and complete
                        if (timeUntilReady <= 0 && !timer.NotificationSent)
                        {
                            await ProcessExpiredTimer(timer, notificationService, timerService, updateNotificationService);
                            expiredCount++;
                        }
                        // Check each warning interval (1 day, 4 hours, 1 hour, 10 minutes)
                        else if (timeUntilReady > 0)
                        {
                            foreach (var warningMinutes in WARNING_INTERVALS)
                            {
                                // Check if we're within the warning window for this interval
                                // Use a tolerance based on check interval (30 seconds = 0.5 minutes)
                                var tolerance = CHECK_INTERVAL_SECONDS / 60.0;

                                if (timeUntilReady <= warningMinutes && timeUntilReady > (warningMinutes - tolerance))
                                {
                                    // Check if this specific warning already sent
                                    var warningSent = await timerWarningService.HasWarningSentAsync(timer.Id, warningMinutes);

                                    if (!warningSent)
                                    {
                                        await ProcessPreExpiryWarning(timer, warningMinutes, notificationService, updateNotificationService, timerWarningService);
                                        warningCount++;
                                    }
                                }
                            }
                        }
                    }
                }

                if (expiredCount > 0 || warningCount > 0)
                {
                    _logger.LogInformation(
                        "Processed {ExpiredCount} expired timers and {WarningCount} pre-expiry warnings",
                        expiredCount, warningCount);
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in timer check service");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Timer Check Service stopped");
    }

    /// <summary>
    /// Process an expired timer - send notification and mark as completed
    /// </summary>
    private async Task ProcessExpiredTimer(
        TimerEntity timer,
        INotificationService notificationService,
        ITimerService timerService,
        IUpdateNotificationService updateNotificationService)
    {
        try
        {
            // Determine notification title and message based on timer type
            string title;
            string message;
            string? actionType = null;
            string? actionData = null;

            if (timer.Type == "Marker" || timer.Type == "CustomMarker")
            {
                // Both standard markers and custom markers should navigate to map
                title = $"{timer.Title} is ready!";
                message = timer.Type == "CustomMarker"
                    ? (timer.Description ?? "Custom marker timer has expired")
                    : "Resource is ready to be harvested";
                actionType = "NavigateToMarker";
                actionData = JsonSerializer.Serialize(new
                {
                    markerId = timer.MarkerId,
                    customMarkerId = timer.CustomMarkerId
                });
            }
            else // Standalone timer (no marker association)
            {
                title = timer.Title;
                message = timer.Description ?? "Timer has expired";
                actionType = "NoAction";
            }

            // Create notification
            var notificationDto = await notificationService.CreateAsync(new CreateNotificationDto
            {
                TenantId = timer.TenantId,
                UserId = timer.UserId, // Send to specific user who created the timer
                Type = (timer.Type == "Marker" || timer.Type == "CustomMarker") ? "MarkerTimerExpired" : "StandaloneTimerExpired",
                Title = title,
                Message = message,
                ActionType = actionType,
                ActionData = actionData,
                Priority = "Normal",
                ExpiresAt = DateTime.UtcNow.AddDays(7) // Auto-delete after 7 days
            });

            // Mark timer as notified and completed
            await timerService.MarkNotificationSentAsync(timer.Id);

            // Broadcast SSE event for timer completion
            var timerEvent = TimerService.MapToEventDto(timer);
            updateNotificationService.NotifyTimerCompleted(timerEvent);

            // Broadcast SSE event for new notification
            // Get the full entity to map to event DTO
            var notificationEntity = new NotificationEntity
            {
                Id = notificationDto.Id,
                TenantId = notificationDto.TenantId,
                UserId = notificationDto.UserId,
                Type = notificationDto.Type,
                Title = notificationDto.Title,
                Message = notificationDto.Message,
                ActionType = notificationDto.ActionType,
                ActionData = notificationDto.ActionData,
                Priority = notificationDto.Priority,
                CreatedAt = notificationDto.CreatedAt,
                IsRead = false
            };
            var notificationEvent = NotificationService.MapToEventDto(notificationEntity);
            updateNotificationService.NotifyNotificationCreated(notificationEvent);

            _logger.LogInformation(
                "Timer {TimerId} expired: '{Title}' for tenant {TenantId}",
                timer.Id, timer.Title, timer.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process expired timer {TimerId}",
                timer.Id);
        }
    }

    /// <summary>
    /// Process pre-expiry warning - send notification but don't complete timer
    /// </summary>
    private async Task ProcessPreExpiryWarning(
        TimerEntity timer,
        int warningMinutes,
        INotificationService notificationService,
        IUpdateNotificationService updateNotificationService,
        ITimerWarningService timerWarningService)
    {
        try
        {
            var minutesRemaining = (int)(timer.ReadyAt - DateTime.UtcNow).TotalMinutes;

            // Format time remaining in a human-readable way
            string timeDescription = warningMinutes switch
            {
                1440 => "1 day",
                240 => "4 hours",
                60 => "1 hour",
                10 => "10 minutes",
                _ => $"{minutesRemaining} minutes"
            };

            // Determine priority based on warning level
            string priority = warningMinutes switch
            {
                1440 or 240 => "Normal",
                60 => "High",
                10 => "Urgent",
                _ => "Normal"
            };

            // Create warning notification
            var notificationDto = await notificationService.CreateAsync(new CreateNotificationDto
            {
                TenantId = timer.TenantId,
                UserId = timer.UserId,
                Type = "TimerPreExpiryWarning",
                Title = $"{timer.Title} - {timeDescription} remaining",
                Message = $"Timer will expire in approximately {timeDescription}",
                ActionType = timer.Type == "Marker" ? "NavigateToMarker" : "NoAction",
                ActionData = timer.Type == "Marker"
                    ? JsonSerializer.Serialize(new { markerId = timer.MarkerId, customMarkerId = timer.CustomMarkerId })
                    : null,
                Priority = priority,
                ExpiresAt = DateTime.UtcNow.AddHours(warningMinutes >= 240 ? 24 : 1)
            });

            // Mark this specific warning as sent
            await timerWarningService.MarkWarningSentAsync(timer.Id, warningMinutes);

            // Broadcast SSE event for new notification
            var notificationEntity = new NotificationEntity
            {
                Id = notificationDto.Id,
                TenantId = notificationDto.TenantId,
                UserId = notificationDto.UserId,
                Type = notificationDto.Type,
                Title = notificationDto.Title,
                Message = notificationDto.Message,
                ActionType = notificationDto.ActionType,
                ActionData = notificationDto.ActionData,
                Priority = notificationDto.Priority,
                CreatedAt = notificationDto.CreatedAt,
                IsRead = false
            };
            var notificationEvent = NotificationService.MapToEventDto(notificationEntity);
            updateNotificationService.NotifyNotificationCreated(notificationEvent);

            _logger.LogInformation(
                "Pre-expiry warning sent for timer {TimerId}: '{Title}' ({TimeDesc} remaining, {WarningMinutes}m interval)",
                timer.Id, timer.Title, timeDescription, warningMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send pre-expiry warning for timer {TimerId} at {WarningMinutes}m interval",
                timer.Id, warningMinutes);
        }
    }
}
