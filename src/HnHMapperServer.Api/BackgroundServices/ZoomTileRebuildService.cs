using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that rebuilds incomplete zoom tiles periodically
/// Scans for zoom tiles that were created before their sub-tiles and regenerates them
/// This fixes the issue where tiles don't display at certain zoom levels
/// </summary>
public class ZoomTileRebuildService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ZoomTileRebuildService> _logger;
    private readonly IConfiguration _configuration;

    public ZoomTileRebuildService(
        IServiceScopeFactory scopeFactory,
        ILogger<ZoomTileRebuildService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if the service is enabled
        var enabled = _configuration.GetValue<bool>("ZoomRebuild:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Zoom Tile Rebuild Service is disabled");
            return;
        }

        var intervalMinutes = _configuration.GetValue<int>("ZoomRebuild:IntervalMinutes", 5);
        var maxTilesPerRun = _configuration.GetValue<int>("ZoomRebuild:MaxTilesPerRun", 500);
        var gridStorage = _configuration.GetValue<string>("GridStorage") ?? "map";

        _logger.LogInformation(
            "Zoom Tile Rebuild Service started (Interval: {IntervalMinutes}min, MaxTiles: {MaxTiles})",
            intervalMinutes,
            maxTilesPerRun);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var tileService = scope.ServiceProvider.GetRequiredService<ITileService>();
                var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

                // Get all active tenants
                var tenants = await tenantService.GetAllTenantsAsync();
                var activeTenants = tenants.Where(t => t.IsActive).ToList();

                _logger.LogDebug("Starting zoom rebuild cycle for {TenantCount} active tenants", activeTenants.Count);

                int totalRebuiltCount = 0;

                // Rebuild tiles for each active tenant
                foreach (var tenant in activeTenants)
                {
                    var rebuiltCount = await tileService.RebuildIncompleteZoomTilesAsync(
                        tenant.Id,
                        gridStorage,
                        maxTilesPerRun - totalRebuiltCount);

                    totalRebuiltCount += rebuiltCount;

                    if (rebuiltCount > 0)
                    {
                        _logger.LogInformation("Tenant {TenantId}: Rebuilt {Count} tiles", tenant.Id, rebuiltCount);
                    }

                    // Stop if we've hit the max tiles limit
                    if (totalRebuiltCount >= maxTilesPerRun)
                    {
                        _logger.LogInformation("Reached max tiles per run ({MaxTiles}), stopping for this cycle", maxTilesPerRun);
                        break;
                    }
                }

                if (totalRebuiltCount > 0)
                {
                    _logger.LogInformation("Zoom rebuild cycle completed: {Count} tiles rebuilt across all tenants", totalRebuiltCount);
                }

                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in zoom tile rebuild service");
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
        }

        _logger.LogInformation("Zoom Tile Rebuild Service stopped");
    }
}
