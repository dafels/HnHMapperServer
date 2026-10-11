using System.Diagnostics;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that removes expired pings every 15 seconds
/// Publishes SSE events for deleted pings
/// Multi-tenancy: Cleans expired pings for all tenants
/// </summary>
public class PingCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PingCleanupService> _logger;

    public PingCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<PingCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Randomized startup delay to prevent all services starting simultaneously
        var startupDelay = TimeSpan.FromSeconds(Random.Shared.Next(0, 60));
        _logger.LogInformation("Ping Cleanup Service starting in {Delay:F1}s", startupDelay.TotalSeconds);
        await Task.Delay(startupDelay, stoppingToken);

        _logger.LogInformation("Ping Cleanup Service started (runs every 15 seconds)");

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                _logger.LogDebug("Ping cleanup job started");

                using var scope = _scopeFactory.CreateScope();
                var pingService = scope.ServiceProvider.GetRequiredService<IPingService>();
                var updateNotificationService = scope.ServiceProvider.GetRequiredService<IUpdateNotificationService>();

                // Delete expired pings across all tenants
                var expiredPings = await pingService.DeleteExpiredAsync();

                // Publish SSE events for each deleted ping (with correct tenant ID)
                foreach (var (id, tenantId) in expiredPings)
                {
                    var deleteEvent = new PingDeleteEventDto
                    {
                        Id = id,
                        TenantId = tenantId
                    };
                    updateNotificationService.NotifyPingDeleted(deleteEvent);
                }

                sw.Stop();
                if (expiredPings.Any())
                {
                    _logger.LogInformation("Cleaned up {Count} expired pings across all tenants in {ElapsedMs}ms", expiredPings.Count, sw.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogDebug("Ping cleanup job completed in {ElapsedMs}ms (no expired pings)", sw.ElapsedMilliseconds);
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Error in ping cleanup service after {ElapsedMs}ms", sw.ElapsedMilliseconds);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }

        _logger.LogInformation("Ping Cleanup Service stopped");
    }
}
