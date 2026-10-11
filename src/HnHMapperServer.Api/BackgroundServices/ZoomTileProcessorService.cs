using System.Diagnostics;
using System.Net.Http.Json;
using HnHMapperServer.Api.Services;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that drains the ZoomTileQueueService channel with time-based batching.
/// Each batch is planned as distinct tiles (ZoomTileBatchPlanner) and rebuilt once each, bottom-up
/// (ZoomTileBatchRegenerator). Then it bumps mapRevision and sends SSE notifications naming the
/// regenerated zoom-0 cells, so browsers refresh only those cells and their ancestors.
/// </summary>
public class ZoomTileProcessorService : BackgroundService
{
    private const string LogPrefix = "[ZoomQ]";
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    private readonly ZoomTileQueueService _queue;
    private readonly ZoomTileBatchRegenerator _regenerator;
    private readonly MapRevisionCache _revisionCache;
    private readonly IUpdateNotificationService _updateNotificationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ZoomTileProcessorService> _logger;

    public ZoomTileProcessorService(
        ZoomTileQueueService queue,
        ZoomTileBatchRegenerator regenerator,
        MapRevisionCache revisionCache,
        IUpdateNotificationService updateNotificationService,
        IHttpClientFactory httpClientFactory,
        ILogger<ZoomTileProcessorService> logger)
    {
        _queue = queue;
        _regenerator = regenerator;
        _revisionCache = revisionCache;
        _updateNotificationService = updateNotificationService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Prefix} Processor started", LogPrefix);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the first request, then collect a DebounceWindow batch. No exception on the
                // normal path: the old debounce ended every window by cancelling a pending read.
                var batch = await _queue.ReadBatchAsync(DebounceWindow, stoppingToken);

                await ProcessBatchAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Prefix} Unhandled error in processor loop", LogPrefix);
                // Brief delay before retrying to avoid tight error loop
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("{Prefix} Processor stopped", LogPrefix);
    }

    private async Task ProcessBatchAsync(List<ZoomTileRequest> requests, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // The queue dedups by key, so the batch holds no duplicates; the planner merges requests
        // that share a zoom-0 cell and the ancestors cells share.
        var plan = ZoomTileBatchPlanner.Plan(requests);
        _logger.LogDebug("{Prefix} Processing batch: {RawCount} requests -> {Cells} z0 cells, {Tiles} tiles",
            LogPrefix, requests.Count, plan.Cells.Count, plan.TileCount);

        var result = await _regenerator.RegenerateAsync(plan, ct);

        // Invalidate Web process tile caches FIRST (cross-process)
        // This ensures the Web process reads fresh files before browsers are notified
        try
        {
            var webClient = _httpClientFactory.CreateClient("Web");
            var payload = requests.Select(r => new
            {
                tenantId = r.TenantId,
                mapId = r.MapId,
                baseX = r.BaseX,
                baseY = r.BaseY
            });
            await webClient.PostAsJsonAsync("/internal/tile-cache/invalidate", payload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Prefix} Failed to invalidate Web tile cache", LogPrefix);
        }

        // (No TileCacheService invalidation: the batch writes WebP files, never Tiles rows. The row
        // writers that feed it invalidate themselves: gridUpload, merges, both imports, wipeTile,
        // setCoords and the superadmin region wipe / missing-source repair.)

        // LAST: Bump mapRevision and send SSE notifications
        // Both API and Web processes now have fresh tiles, so browsers get correct data on refresh.
        // The event names the regenerated zoom-0 cells (null = too many, refresh everything); a
        // client honours the list only when the revision follows the one it holds by exactly 1.
        var changedCells = ZoomTileCells.ChangedCellsByMap(requests);
        var revisions = new List<string>(result.AffectedMaps.Count);
        foreach (var mapId in result.AffectedMaps)
        {
            var newRevision = _revisionCache.Increment(mapId);
            changedCells.TryGetValue(mapId, out var cells);
            _updateNotificationService.NotifyMapRevision(mapId, newRevision, cells);
            revisions.Add($"{mapId}@{newRevision}:{cells?.Length.ToString() ?? "all"}");
        }

        sw.Stop();
        _logger.LogInformation(
            "{Prefix} Batch complete: {Generated} generated, {Failed} failed, {MapCount} maps revised in {Ms}ms ({Tiles} tiles, {Cells} z0 cells for {Requests} requests; revisions {Revisions})",
            LogPrefix, result.Generated, result.Failed, result.AffectedMaps.Count, sw.ElapsedMilliseconds,
            result.Tiles, result.Cells, requests.Count, string.Join(", ", revisions));
    }
}
