using System.Diagnostics;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that pre-generates 400x400 WebP tiles for all active tenants.
/// Runs every 30 seconds to process missing tiles and reduce on-the-fly generation load.
/// </summary>
public class LargeTileGenerationService : BackgroundService
{
    private const string LogPrefix = "[LargeTile]";
    private const int CycleIntervalSeconds = 30;
    private const int StatsIntervalCycles = 10; // Log stats every 10 cycles (5 minutes)

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LargeTileGenerationService> _logger;
    private int _cycleCount = 0;

    public LargeTileGenerationService(
        IServiceScopeFactory scopeFactory,
        ILogger<LargeTileGenerationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Randomized startup delay to prevent all services starting simultaneously
        var startupDelay = TimeSpan.FromSeconds(Random.Shared.Next(30, 90));
        _logger.LogInformation(
            "{Prefix} SERVICE-INIT starting in {Delay}s (interval={Interval}s)",
            LogPrefix, (int)startupDelay.TotalSeconds, CycleIntervalSeconds);
        await Task.Delay(startupDelay, stoppingToken);

        _logger.LogInformation("{Prefix} SERVICE-START background tile generation active", LogPrefix);

        while (!stoppingToken.IsCancellationRequested)
        {
            _cycleCount++;
            var sw = Stopwatch.StartNew();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var largeTileService = scope.ServiceProvider.GetRequiredService<ILargeTileService>();
                var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

                // Get all active tenants
                var tenants = await tenantService.GetAllTenantsAsync();
                var activeTenants = tenants.Where(t => t.IsActive).ToList();

                var totalGenerated = 0;
                var tenantsWithWork = 0;
                var tenantResults = new List<(string Id, int Generated)>();

                foreach (var tenant in activeTenants)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        var generated = await largeTileService.GenerateMissingTilesAsync(tenant.Id, stoppingToken);
                        if (generated > 0)
                        {
                            totalGenerated += generated;
                            tenantsWithWork++;
                            tenantResults.Add((tenant.Id, generated));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "{Prefix} TENANT-ERROR [{Tenant}] - batch generation failed",
                            LogPrefix, tenant.Id);
                    }
                }

                sw.Stop();

                // Log cycle summary
                if (totalGenerated > 0)
                {
                    var tenantSummary = string.Join(", ", tenantResults.Select(t => $"{t.Id}:{t.Generated}"));
                    _logger.LogInformation(
                        "{Prefix} CYCLE #{Cycle} generated={Total} tenants={TenantsWithWork}/{TotalTenants} time={Ms}ms [{TenantSummary}]",
                        LogPrefix, _cycleCount, totalGenerated, tenantsWithWork, activeTenants.Count, sw.ElapsedMilliseconds, tenantSummary);
                }
                else if (_cycleCount % StatsIntervalCycles == 0)
                {
                    // Periodic heartbeat even when no work done
                    _logger.LogInformation(
                        "{Prefix} CYCLE #{Cycle} no missing tiles (scanned {TenantCount} tenants in {Ms}ms)",
                        LogPrefix, _cycleCount, activeTenants.Count, sw.ElapsedMilliseconds);

                    // Log accumulated stats
                    if (largeTileService is LargeTileService lts)
                    {
                        lts.LogStatsSummary();
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(CycleIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "{Prefix} CYCLE-ERROR #{Cycle} after {Ms}ms",
                    LogPrefix, _cycleCount, sw.ElapsedMilliseconds);
                await Task.Delay(TimeSpan.FromSeconds(CycleIntervalSeconds), stoppingToken);
            }
        }

        _logger.LogInformation("{Prefix} SERVICE-STOP after {Cycles} cycles", LogPrefix, _cycleCount);
    }
}
