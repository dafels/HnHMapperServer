using System.Diagnostics;
using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that updates marker readiness status every 30 seconds
/// Matches the Go implementation: updateReadinessOnMarkers() goroutine
/// Multi-tenancy: Updates markers for all active tenants
/// </summary>
public class MarkerReadinessService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarkerReadinessService> _logger;

    public MarkerReadinessService(
        IServiceScopeFactory scopeFactory,
        ILogger<MarkerReadinessService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Randomized startup delay to prevent all services starting simultaneously
        var startupDelay = TimeSpan.FromSeconds(Random.Shared.Next(0, 60));
        _logger.LogInformation("Marker Readiness Service starting in {Delay:F1}s", startupDelay.TotalSeconds);
        await Task.Delay(startupDelay, stoppingToken);

        _logger.LogInformation("Marker Readiness Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                _logger.LogDebug("Marker readiness job started");

                using var scope = _scopeFactory.CreateScope();
                var markerService = scope.ServiceProvider.GetRequiredService<IMarkerService>();
                var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

                // Get all active tenants
                var tenants = await tenantService.GetAllTenantsAsync();

                // Update readiness for each tenant's markers
                foreach (var tenant in tenants.Where(t => t.IsActive))
                {
                    await markerService.UpdateReadinessOnMarkersAsync(tenant.Id);
                }

                sw.Stop();
                _logger.LogDebug("Marker readiness job completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Error in marker readiness service after {ElapsedMs}ms", sw.ElapsedMilliseconds);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Marker Readiness Service stopped");
    }
}
