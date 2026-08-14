using System.Diagnostics;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that deletes expired notifications every 30 minutes and
/// broadcasts their dismissal so open notification bells drop them live.
/// Also purges legacy CookbookFoodAdded rows created before expiry existed
/// (ExpiresAt == null), which used to pile up forever.
/// Multi-tenancy: Cleans notifications for all tenants.
/// </summary>
public class NotificationCleanupService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationCleanupService> _logger;

    public NotificationCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Randomized startup delay to prevent all services starting simultaneously
        var startupDelay = TimeSpan.FromSeconds(Random.Shared.Next(0, 60));
        _logger.LogInformation("Notification Cleanup Service starting in {Delay:F1}s", startupDelay.TotalSeconds);
        await Task.Delay(startupDelay, stoppingToken);

        _logger.LogInformation("Notification Cleanup Service started (runs every 30 minutes)");

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
                var updateNotificationService = scope.ServiceProvider.GetRequiredService<IUpdateNotificationService>();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Expired notifications across all tenants (bypasses the tenant filter)
                var deletedIds = await notificationService.DeleteExpiredAsync();

                // Legacy cookbook digests from before ExpiresAt was set on them
                var legacyCutoff = DateTime.UtcNow - CookbookNotificationService.Expiry;
                var legacyIds = await db.Notifications
                    .IgnoreQueryFilters()
                    .Where(n => n.Type == CookbookNotificationService.NotificationType
                        && n.ExpiresAt == null
                        && n.CreatedAt < legacyCutoff)
                    .Select(n => n.Id)
                    .ToListAsync(stoppingToken);

                if (legacyIds.Count > 0)
                {
                    await db.Notifications
                        .IgnoreQueryFilters()
                        .Where(n => legacyIds.Contains(n.Id))
                        .ExecuteDeleteAsync(stoppingToken);
                }

                // Dismissal events are id-only and unfiltered by design (ids are non-sensitive)
                foreach (var id in deletedIds.Concat(legacyIds))
                {
                    updateNotificationService.NotifyNotificationDismissed(id);
                }

                sw.Stop();
                var total = deletedIds.Count + legacyIds.Count;
                if (total > 0)
                {
                    _logger.LogInformation(
                        "Cleaned up {Expired} expired and {Legacy} legacy notifications in {ElapsedMs}ms",
                        deletedIds.Count, legacyIds.Count, sw.ElapsedMilliseconds);
                }

                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Error in notification cleanup service after {ElapsedMs}ms", sw.ElapsedMilliseconds);
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }

        _logger.LogInformation("Notification Cleanup Service stopped");
    }
}
