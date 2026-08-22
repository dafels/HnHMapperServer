using System.Diagnostics;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// Per-cycle ceiling on the zoom queue depth this scan may fill it to. Leaves headroom in the
    /// bounded (4096, DropOldest) channel so a big backlog can never flush out upload-driven
    /// requests; whatever doesn't fit is retried next cycle (the dirty rows persist).
    /// </summary>
    private const int MaxQueueDepthForBackfill = 3000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ZoomTileQueueService _zoomTileQueue;
    private readonly ILogger<LargeTileGenerationService> _logger;
    private int _cycleCount = 0;

    public LargeTileGenerationService(
        IServiceScopeFactory scopeFactory,
        ZoomTileQueueService zoomTileQueue,
        ILogger<LargeTileGenerationService> logger)
    {
        _scopeFactory = scopeFactory;
        _zoomTileQueue = zoomTileQueue;
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

                // Gap-fill above only creates MISSING files. Cells whose files exist but predate
                // their dirty marks (merges, imports, dropped queue work, restarts) stay stale
                // forever without this: re-enqueue them for force-regeneration.
                await EnqueueStaleWebpCellsAsync(scope, tenant.Id, ct);
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

    /// <summary>
    /// Durable WebP regeneration backstop. The upload path's in-memory queue is bounded
    /// (DropOldest) and lost on restart; DirtyZoomTiles rows are not. This derives the WebP
    /// zoom-0 cells covering each tenant's dirty rows and re-enqueues every cell whose z0 file
    /// is older than its newest dirty mark (stale content) or missing while zoom-0 rows exist.
    /// Freshly regenerated cells pass the mtime check on later cycles, so nothing loops; the
    /// rows themselves are consumed by ZoomTileRebuildService (legacy pyramid) as before.
    /// </summary>
    private async Task EnqueueStaleWebpCellsAsync(IServiceScope scope, string tenantId, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var largeTileService = scope.ServiceProvider.GetRequiredService<ILargeTileService>();

        var zoom1Rows = await db.DirtyZoomTiles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.Zoom == 1)
            .Select(d => new { d.MapId, d.CoordX, d.CoordY, d.CreatedAt })
            .ToListAsync(ct);

        if (zoom1Rows.Count == 0)
        {
            return;
        }

        var cells = WebpDirtyCellPlanner.DeriveCells(
            zoom1Rows.Select(r => (r.MapId, r.CoordX, r.CoordY, r.CreatedAt)));

        var enqueued = 0;
        var skippedFresh = 0;
        var skippedEmpty = 0;

        foreach (var cell in cells)
        {
            if (ct.IsCancellationRequested) break;

            if (_zoomTileQueue.PendingCount >= MaxQueueDepthForBackfill)
            {
                _logger.LogInformation(
                    "{Prefix} BACKFILL [{Tenant}] queue at capacity, deferring {Remaining} cells to next cycle",
                    LogPrefix, tenantId, cells.Count - enqueued - skippedFresh - skippedEmpty);
                break;
            }

            var z0Path = largeTileService.GetLargeTilePath(tenantId, cell.MapId, 0, cell.CellX, cell.CellY);

            if (File.Exists(z0Path))
            {
                if (File.GetLastWriteTimeUtc(z0Path) >= cell.NewestMark)
                {
                    skippedFresh++;
                    continue; // already regenerated after the newest mark
                }
            }
            else
            {
                // No file: only worth enqueueing if the cell actually has renderable zoom-0 rows
                // (regeneration would otherwise be a no-op every cycle until the rows are consumed).
                var minX = cell.CellX * 4;
                var minY = cell.CellY * 4;
                var hasRows = await db.Tiles
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .AnyAsync(t => t.TenantId == tenantId && t.MapId == cell.MapId && t.Zoom == 0
                        && t.CoordX >= minX && t.CoordX <= minX + 3
                        && t.CoordY >= minY && t.CoordY <= minY + 3
                        && t.File != "", ct);
                if (!hasRows)
                {
                    skippedEmpty++;
                    continue;
                }
            }

            _zoomTileQueue.EnqueueZoomRegeneration(
                new ZoomTileRequest(tenantId, cell.MapId, cell.CellX * 4, cell.CellY * 4));
            enqueued++;
        }

        if (enqueued > 0)
        {
            _logger.LogInformation(
                "{Prefix} BACKFILL [{Tenant}] enqueued {Enqueued} stale webp cells ({Fresh} fresh, {Empty} empty, {Total} candidates)",
                LogPrefix, tenantId, enqueued, skippedFresh, skippedEmpty, cells.Count);
        }
    }
}
