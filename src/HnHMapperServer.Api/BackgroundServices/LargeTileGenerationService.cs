using System.Diagnostics;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that pre-generates 400x400 WebP tiles.
///
/// Phase 1 (startup): Full scan of all tenants to catch first-deploy and crash recovery.
/// Phase 2 (ongoing): Dirty-driven scan every 5 minutes — only checks maps with DirtyZoomTile entries.
/// </summary>
public class LargeTileGenerationService : BackgroundService
{
    private const string LogPrefix = "[LargeTile]";
    private const int DirtyScanIntervalMinutes = 5;

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
        // Brief startup delay to let other services initialize
        var startupDelay = TimeSpan.FromSeconds(Random.Shared.Next(10, 30));
        _logger.LogInformation(
            "{Prefix} SERVICE-INIT starting in {Delay}s",
            LogPrefix, (int)startupDelay.TotalSeconds);
        await Task.Delay(startupDelay, stoppingToken);

        // === Phase 1: Startup full scan (runs once) ===
        await RunStartupScanAsync(stoppingToken);

        // === Phase 2: Dirty-driven scan (every 5 minutes) ===
        _logger.LogInformation(
            "{Prefix} Phase 2: dirty-driven scan every {Interval}min",
            LogPrefix, DirtyScanIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(DirtyScanIntervalMinutes), stoppingToken);

            _cycleCount++;
            try
            {
                await RunDirtyScanAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Prefix} DIRTY-SCAN-ERROR cycle #{Cycle}", LogPrefix, _cycleCount);
            }
        }

        _logger.LogInformation("{Prefix} SERVICE-STOP after {Cycles} dirty-scan cycles", LogPrefix, _cycleCount);
    }

    /// <summary>
    /// Phase 1: Full scan of all tenants. Catches first deploy, crash recovery, queue overflow from previous run.
    /// </summary>
    private async Task RunStartupScanAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Prefix} Phase 1: startup full scan", LogPrefix);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var largeTileService = scope.ServiceProvider.GetRequiredService<ILargeTileService>();
            var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

            var tenants = await tenantService.GetAllTenantsAsync();
            var activeTenants = tenants.Where(t => t.IsActive).ToList();

            var totalGenerated = 0;

            foreach (var tenant in activeTenants)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var generated = await largeTileService.GenerateMissingTilesAsync(tenant.Id, ct);
                    totalGenerated += generated;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Prefix} STARTUP-ERROR [{Tenant}]", LogPrefix, tenant.Id);
                }
            }

            sw.Stop();
            _logger.LogInformation(
                "{Prefix} Phase 1 complete: generated {Total} tiles across {Count} tenants in {Ms}ms",
                LogPrefix, totalGenerated, activeTenants.Count, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Shutting down during startup scan
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} Phase 1 failed after {Ms}ms", LogPrefix, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Phase 2: Only scan maps that have DirtyZoomTile entries (recent uploads).
    /// O(dirty maps) instead of O(all maps with all tiles).
    /// </summary>
    private async Task RunDirtyScanAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        using var scope = _scopeFactory.CreateScope();
        var largeTileService = scope.ServiceProvider.GetRequiredService<ILargeTileService>();
        var tileService = scope.ServiceProvider.GetRequiredService<ITileService>();
        var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

        var tenants = await tenantService.GetAllTenantsAsync();
        var activeTenants = tenants.Where(t => t.IsActive).ToList();

        var totalGenerated = 0;
        var tenantsScanned = 0;
        var tenantsSkipped = 0;

        foreach (var tenant in activeTenants)
        {
            if (ct.IsCancellationRequested) break;

            // Fast skip: no dirty tiles means no recent uploads to catch up on
            if (!await tileService.HasDirtyZoomTilesAsync(tenant.Id))
            {
                tenantsSkipped++;
                continue;
            }

            try
            {
                var dirtyMapIds = await tileService.GetDirtyMapIdsAsync(tenant.Id);
                if (dirtyMapIds.Count == 0)
                {
                    tenantsSkipped++;
                    continue;
                }

                tenantsScanned++;
                var generated = await largeTileService.GenerateMissingTilesForMapsAsync(
                    tenant.Id, dirtyMapIds, ct);
                totalGenerated += generated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Prefix} DIRTY-SCAN [{Tenant}] failed", LogPrefix, tenant.Id);
            }
        }

        sw.Stop();

        if (totalGenerated > 0)
        {
            _logger.LogInformation(
                "{Prefix} DIRTY-SCAN #{Cycle}: generated {Total} tiles, scanned {Scanned} tenants, skipped {Skipped} in {Ms}ms",
                LogPrefix, _cycleCount, totalGenerated, tenantsScanned, tenantsSkipped, sw.ElapsedMilliseconds);
        }
        else if (_cycleCount % 12 == 0) // Log heartbeat every hour (12 x 5min)
        {
            _logger.LogInformation(
                "{Prefix} DIRTY-SCAN #{Cycle}: no work (all {Count} tenants clean) in {Ms}ms",
                LogPrefix, _cycleCount, activeTenants.Count, sw.ElapsedMilliseconds);

            if (largeTileService is LargeTileService lts)
            {
                lts.LogStatsSummary();
            }
        }
    }
}
