using HnHMapperServer.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Outcome of one batch. Generated/Failed count requests (a request is generated when its zoom-0
/// cell produced bytes, as before the planner); Cells/Tiles count the distinct tiles rebuilt.
/// AffectedMaps are the maps of every cell that was processed.
/// </summary>
public sealed record ZoomTileBatchResult(
    int Generated,
    int Failed,
    int Cells,
    int Tiles,
    IReadOnlyCollection<int> AffectedMaps);

/// <summary>
/// Regenerates a <see cref="ZoomTileBatchPlan"/>: every zoom-0 cell once, then every planned
/// parent once, bottom-up (see <see cref="ZoomTileBatchPlanner"/>). Owns the release of the
/// batch's dedup keys.
/// </summary>
public sealed class ZoomTileBatchRegenerator
{
    private const string LogPrefix = "[ZoomQ]";

    private readonly ZoomTileQueueService _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ZoomTileBatchRegenerator> _logger;

    public ZoomTileBatchRegenerator(
        ZoomTileQueueService queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ZoomTileBatchRegenerator> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ZoomTileBatchResult> RegenerateAsync(ZoomTileBatchPlan plan, CancellationToken ct)
    {
        var cellsBegun = 0;
        var generated = 0;
        var failed = 0;
        var tiles = 0;
        var affectedMaps = new HashSet<int>();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var largeTileService = scope.ServiceProvider.GetRequiredService<ILargeTileService>();

            foreach (var cell in plan.Cells)
            {
                if (ct.IsCancellationRequested) break;

                // Release the cell's dedup keys just before its sources are read: every upload saves
                // its file and tile row before it enqueues, so an enqueue skipped up to here is still
                // read by this regeneration, and one arriving from now on goes into the next batch
                // instead of being lost behind a read that already happened.
                foreach (var request in cell.Requests)
                {
                    _queue.BeginProcessing(request);
                }
                cellsBegun++;

                var bytes = await RegenerateOneAsync(largeTileService, cell.Key);
                tiles++;
                affectedMaps.Add(cell.Key.MapId);
                if (bytes != null)
                {
                    generated += cell.Requests.Count;
                }
                else
                {
                    failed += cell.Requests.Count;
                }
            }

            foreach (var parent in plan.Parents)
            {
                if (ct.IsCancellationRequested) break;

                await RegenerateOneAsync(largeTileService, parent);
                tiles++;
            }
        }
        finally
        {
            for (var i = 0; i < plan.Cells.Count; i++)
            {
                foreach (var request in plan.Cells[i].Requests)
                {
                    if (i < cellsBegun)
                    {
                        _queue.EndProcessing(request);
                    }
                    else
                    {
                        // Never reached (shutdown, a scope that failed to resolve): the cell's
                        // DirtyWebpCells mark still stands, and the key must not stay pending.
                        _queue.MarkCompleted(request);
                    }
                }
            }
        }

        return new ZoomTileBatchResult(generated, failed, cellsBegun, tiles, affectedMaps);
    }

    private async Task<byte[]?> RegenerateOneAsync(ILargeTileService largeTileService, ZoomTileKey tile)
    {
        try
        {
            return await largeTileService.ForceRegenerateLargeTileAsync(
                tile.TenantId, tile.MapId, tile.Zoom, tile.X, tile.Y);
        }
        catch (Exception ex)
        {
            // Only a guard against unexpected throws: generation errors are caught and logged inside
            // ForceRegenerate, which keeps the old file. One tile's failure never stops the batch.
            _logger.LogWarning(ex, "{Prefix} Failed to process tile: tenant={TenantId} map={MapId} z={Zoom} ({X},{Y})",
                LogPrefix, tile.TenantId, tile.MapId, tile.Zoom, tile.X, tile.Y);
            return null;
        }
    }
}
