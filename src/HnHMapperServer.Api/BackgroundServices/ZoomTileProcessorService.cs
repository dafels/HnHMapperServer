using System.Diagnostics;
using System.Net.Http.Json;
using HnHMapperServer.Api.Services;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that drains the ZoomTileQueueService channel with time-based batching.
/// After generating zoom tiles, it bumps mapRevision and sends SSE notifications so the browser refreshes.
/// </summary>
public class ZoomTileProcessorService : BackgroundService
{
    private const string LogPrefix = "[ZoomQ]";
    private const int DebounceWindowMs = 500;

    private readonly ZoomTileQueueService _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MapRevisionCache _revisionCache;
    private readonly IUpdateNotificationService _updateNotificationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ZoomTileProcessorService> _logger;

    public ZoomTileProcessorService(
        ZoomTileQueueService queue,
        IServiceScopeFactory scopeFactory,
        MapRevisionCache revisionCache,
        IUpdateNotificationService updateNotificationService,
        IHttpClientFactory httpClientFactory,
        ILogger<ZoomTileProcessorService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
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
                // Block efficiently until the first item arrives
                var firstRequest = await _queue.Reader.ReadAsync(stoppingToken);
                var batch = new List<ZoomTileRequest> { firstRequest };

                // Collect more items for up to DebounceWindowMs
                var debounceDeadline = DateTime.UtcNow.AddMilliseconds(DebounceWindowMs);
                while (DateTime.UtcNow < debounceDeadline)
                {
                    if (_queue.Reader.TryRead(out var next))
                    {
                        batch.Add(next);
                    }
                    else
                    {
                        // No more items immediately available, wait a bit
                        try
                        {
                            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            var remaining = debounceDeadline - DateTime.UtcNow;
                            if (remaining > TimeSpan.Zero)
                            {
                                delayCts.CancelAfter(remaining);
                                var item = await _queue.Reader.ReadAsync(delayCts.Token);
                                batch.Add(item);
                            }
                        }
                        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                        {
                            // Debounce window expired, process what we have
                            break;
                        }
                    }
                }

                // Deduplicate batch by (TenantId, MapId, BaseX, BaseY)
                var deduplicated = batch
                    .DistinctBy(r => (r.TenantId, r.MapId, r.BaseX, r.BaseY))
                    .ToList();

                _logger.LogInformation("{Prefix} Processing batch: {RawCount} items -> {DedupCount} unique tiles",
                    LogPrefix, batch.Count, deduplicated.Count);

                await ProcessBatchAsync(deduplicated, stoppingToken);
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
        var affectedMaps = new HashSet<int>();
        var affectedTenants = new HashSet<string>();
        var generated = 0;
        var failed = 0;

        using var scope = _scopeFactory.CreateScope();
        var largeTileService = scope.ServiceProvider.GetRequiredService<ILargeTileService>();

        foreach (var request in requests)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Calculate the large tile coordinates from the base tile coordinates
                var largeTileX = (int)Math.Floor(request.BaseX / 4.0);
                var largeTileY = (int)Math.Floor(request.BaseY / 4.0);

                // Force-regenerate zoom 0 (bypasses caches, overwrites old file on disk)
                var result = await largeTileService.ForceRegenerateLargeTileAsync(
                    request.TenantId, request.MapId, 0, largeTileX, largeTileY);

                if (result != null)
                {
                    // Force-regenerate zoom levels 1-6 (parent tiles)
                    var parentX = largeTileX;
                    var parentY = largeTileY;
                    for (int zoom = 1; zoom <= 6; zoom++)
                    {
                        parentX = (int)Math.Floor(parentX / 2.0);
                        parentY = (int)Math.Floor(parentY / 2.0);
                        await largeTileService.ForceRegenerateLargeTileAsync(
                            request.TenantId, request.MapId, zoom, parentX, parentY);
                    }

                    generated++;
                }
                else
                {
                    failed++;
                }

                affectedMaps.Add(request.MapId);
                affectedTenants.Add(request.TenantId);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "{Prefix} Failed to process tile: tenant={TenantId} map={MapId} ({X},{Y})",
                    LogPrefix, request.TenantId, request.MapId, request.BaseX, request.BaseY);
            }
            finally
            {
                _queue.MarkCompleted(request);
            }
        }

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

        // Invalidate tile caches for affected tenants
        var tileCacheService = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        foreach (var tenantId in affectedTenants)
        {
            await tileCacheService.InvalidateCacheAsync(tenantId);
        }

        // LAST: Bump mapRevision and send SSE notifications
        // Both API and Web processes now have fresh tiles, so browsers get correct data on refresh
        foreach (var mapId in affectedMaps)
        {
            var newRevision = _revisionCache.Increment(mapId);
            _updateNotificationService.NotifyMapRevision(mapId, newRevision);
        }

        sw.Stop();
        _logger.LogInformation(
            "{Prefix} Batch complete: {Generated} generated, {Failed} failed, {MapCount} maps revised in {Ms}ms",
            LogPrefix, generated, failed, affectedMaps.Count, sw.ElapsedMilliseconds);
    }
}
