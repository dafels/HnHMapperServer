using System.Diagnostics;
using HnHMapperServer.Api.Endpoints;
using HnHMapperServer.Api.Services;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that pre-generates 400x400 WebP tiles.
///
/// Phase 1 (startup): Full scan of all tenants to catch first-deploy and crash recovery.
/// Phase 2 (ongoing): every 5 minutes, gap-fill the maps with pending dirty marks and run the
/// durable WebP backstop (<see cref="WebpDirtyCellBackstop"/>) over DirtyWebpCells.
/// </summary>
public class LargeTileGenerationService : BackgroundService
{
    private const string LogPrefix = "[LargeTile]";
    private const int DirtyScanIntervalMinutes = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MapRevisionCache _revisionCache;
    private readonly IUpdateNotificationService _updateNotificationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LargeTileGenerationService> _logger;
    private int _cycleCount = 0;
    private bool _legacyMarksSeeded;

    public LargeTileGenerationService(
        IServiceScopeFactory scopeFactory,
        MapRevisionCache revisionCache,
        IUpdateNotificationService updateNotificationService,
        IHttpClientFactory httpClientFactory,
        ILogger<LargeTileGenerationService> logger)
    {
        _scopeFactory = scopeFactory;
        _revisionCache = revisionCache;
        _updateNotificationService = updateNotificationService;
        _httpClientFactory = httpClientFactory;
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

        // Before anything else (the legacy rebuild consumes zoom-1 rows after its 1-hour delay).
        using (var seedScope = _scopeFactory.CreateScope())
        {
            await EnsureLegacyMarksSeededAsync(
                seedScope.ServiceProvider.GetRequiredService<WebpDirtyCellBackstop>(), stoppingToken);
        }

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
    /// Once per process start: carries the WebP work still pending in the legacy zoom-1 dirty rows
    /// over to DirtyWebpCells (nothing is lost at deploy). Idempotent; retried each dirty-scan
    /// cycle until it succeeds once.
    /// </summary>
    private async Task EnsureLegacyMarksSeededAsync(WebpDirtyCellBackstop backstop, CancellationToken ct)
    {
        if (_legacyMarksSeeded)
        {
            return;
        }

        try
        {
            await backstop.SeedFromLegacyDirtyRowsAsync(ct);
            _legacyMarksSeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Prefix} BACKFILL seeding from legacy dirty rows failed; retrying next cycle", LogPrefix);
        }
    }

    /// <summary>
    /// Makes the batch's writes visible beyond this process, as ZoomQ does for its own: the Web
    /// process drops its cached tiles of each map (it holds its own LargeTileService statics, and
    /// the gap-fill rewrites EXISTING parents), then the map revision is bumped so browsers stop
    /// using tiles they hold under the old immutable URL. The batch already evicted this process's
    /// cache per tile written. An unreachable Web process (still starting) is not retried per map.
    /// </summary>
    private async Task PublishWrittenMapsAsync(string tenantId, HashSet<int> writtenMapIds)
    {
        foreach (var mapId in writtenMapIds)
        {
            if (!await SuperadminEndpoints.InvalidateWebMapTileCacheAsync(_httpClientFactory, tenantId, mapId, _logger))
            {
                break;
            }
        }

        foreach (var mapId in writtenMapIds)
        {
            _updateNotificationService.NotifyMapRevision(mapId, _revisionCache.Increment(mapId));
        }
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
                    var writtenMapIds = new HashSet<int>();
                    var generated = await largeTileService.GenerateMissingTilesAsync(tenant.Id, ct, writtenMapIds);
                    totalGenerated += generated;
                    await PublishWrittenMapsAsync(tenant.Id, writtenMapIds);
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
    /// Phase 2, per active tenant: gap-fill the maps with pending marks (legacy DirtyZoomTiles or
    /// DirtyWebpCells), then run the WebP backstop over the tenant's settled DirtyWebpCells marks.
    /// Each step has its own try/catch, and the backstop never sits behind the gap-fill's fast
    /// skip: the legacy rebuild deletes DirtyZoomTiles rows on its own schedule, so their absence
    /// says nothing about pending WebP work.
    /// </summary>
    private async Task RunDirtyScanAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        using var scope = _scopeFactory.CreateScope();
        var largeTileService = scope.ServiceProvider.GetRequiredService<ILargeTileService>();
        var tileService = scope.ServiceProvider.GetRequiredService<ITileService>();
        var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
        var backstop = scope.ServiceProvider.GetRequiredService<WebpDirtyCellBackstop>();

        await EnsureLegacyMarksSeededAsync(backstop, ct);

        var tenants = await tenantService.GetAllTenantsAsync();
        var activeTenants = tenants.Where(t => t.IsActive).ToList();

        var totalGenerated = 0;
        var tenantsScanned = 0;
        var tenantsSkipped = 0;

        foreach (var tenant in activeTenants)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var dirtyMapIds = await tileService.HasDirtyZoomTilesAsync(tenant.Id)
                    ? await tileService.GetDirtyMapIdsAsync(tenant.Id)
                    : new HashSet<int>();
                dirtyMapIds.UnionWith(await backstop.GetMarkedMapIdsAsync(tenant.Id, ct));

                if (dirtyMapIds.Count == 0)
                {
                    tenantsSkipped++;
                }
                else
                {
                    tenantsScanned++;
                    var writtenMapIds = new HashSet<int>();
                    totalGenerated += await largeTileService.GenerateMissingTilesForMapsAsync(
                        tenant.Id, dirtyMapIds, ct, writtenMapIds);
                    await PublishWrittenMapsAsync(tenant.Id, writtenMapIds);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Prefix} DIRTY-SCAN [{Tenant}] gap-fill failed", LogPrefix, tenant.Id);
            }

            // Gap-fill only creates MISSING files; the backstop re-enqueues cells whose existing
            // files predate their marks (merges, imports, wipes, dropped queue work, restarts).
            try
            {
                await backstop.RunAsync(tenant.Id, DateTime.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Prefix} BACKFILL [{Tenant}] failed", LogPrefix, tenant.Id);
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
                "{Prefix} DIRTY-SCAN #{Cycle}: no new tiles ({Scanned} of {Count} tenants had marks) in {Ms}ms",
                LogPrefix, _cycleCount, tenantsScanned, activeTenants.Count, sw.ElapsedMilliseconds);

            if (largeTileService is LargeTileService lts)
            {
                lts.LogStatsSummary();
            }
        }
    }
}
