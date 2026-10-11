using System.Collections.Concurrent;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static HnHMapperServer.Services.Services.WebpDirtyCellPlanner;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// The single consumer of DirtyWebpCells: the durable, restart-safe half of WebP regeneration.
/// The fast path (ZoomTileQueueService) is in-memory, bounded (DropOldest) and lost on restart;
/// the marks are not. Run by LargeTileGenerationService's 5-minute dirty scan, per tenant.
///
/// A mark is consumed only once the cell's whole existing z0-z6 chain on disk is newer than its
/// MarkedAt (<see cref="WebpDirtyCellPlanner.NeedsRegeneration"/>); until then the cell is
/// re-enqueued on the fast path. Marks younger than <see cref="SettleTime"/> are left to the fast
/// path. Deletes are conditional on the mark not having moved (MarkedAt &lt;= the run's cutoff), so a
/// re-mark that lands while a run is evaluating survives it.
///
/// Every query carries an explicit TenantId (plus IgnoreQueryFilters where the entity has a
/// tenant filter; DirtyWebpCells has none): this runs in a background service with no ambient
/// tenant.
/// </summary>
public sealed class WebpDirtyCellBackstop
{
    private const string LogPrefix = "[LargeTile]";

    /// <summary>The fast path normally regenerates a cell within seconds of its mark.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(5);

    /// <summary>Oldest marks first; the rest wait for the next run.</summary>
    public const int MaxRowsPerRun = 5000;

    /// <summary>
    /// Regenerations a mark may go through without its chain turning fresh before the backstop
    /// gives up on it (an undecodable source PNG, a persistent IO/DB error). Counted per process,
    /// so a restart grants a few more attempts; the drift scan reports whatever is left stale.
    /// Only regenerations the processor actually handled count (<see cref="ProcessedAttempts"/>).
    /// </summary>
    public const int MaxAttempts = 3;

    // SQLite parameter-count safety for Contains() chunks.
    private const int ChunkSize = 500;

    /// <summary>
    /// Per mark (cell + MarkedAt): earlier regenerations known to have been processed without
    /// settling it, plus the queue's drop counter when the latest one was enqueued — that one is
    /// judged by <see cref="ProcessedAttempts"/> once it has left the queue.
    /// </summary>
    private readonly record struct AttemptState(DateTime MarkedAt, int Processed, long DropsAtEnqueue);

    private static readonly ConcurrentDictionary<(string TenantId, int MapId, int CellX, int CellY), AttemptState>
        _attempts = new();

    private readonly ApplicationDbContext _db;
    private readonly ILargeTileService _largeTileService;
    private readonly ZoomTileQueueService _queue;
    private readonly string _gridStorage;
    private readonly ILogger<WebpDirtyCellBackstop> _logger;

    public WebpDirtyCellBackstop(
        ApplicationDbContext db,
        ILargeTileService largeTileService,
        ZoomTileQueueService queue,
        IConfiguration configuration,
        ILogger<WebpDirtyCellBackstop> logger)
    {
        _db = db;
        _largeTileService = largeTileService;
        _queue = queue;
        _gridStorage = configuration["GridStorage"] ?? "map";
        _logger = logger;
    }

    public sealed record MarkedCell(int Id, int MapId, int CellX, int CellY, DateTime MarkedAt);

    /// <summary>What one run decided, before anything was enqueued or deleted.</summary>
    public sealed class BackstopPlan
    {
        public required string TenantId { get; init; }

        /// <summary>Rows with MarkedAt at or before this were read; only those may be deleted.</summary>
        public required DateTime Cutoff { get; init; }

        /// <summary>Chain fresh (or the map is gone): delete the mark.</summary>
        public List<MarkedCell> Settled { get; } = new();

        /// <summary>Stale: enqueue on the fast path, keep the mark.</summary>
        public List<MarkedCell> Regenerate { get; } = new();

        /// <summary>Still stale after <see cref="MaxAttempts"/> regenerations: drop the mark.</summary>
        public List<MarkedCell> GiveUp { get; } = new();
    }

    public sealed record BackstopResult(int Enqueued, int Cleared, int Deferred, int GaveUp);

    /// <summary>Plan + apply for one tenant, with one summary line when there was work.</summary>
    public async Task<BackstopResult> RunAsync(string tenantId, DateTime nowUtc, CancellationToken ct = default)
    {
        var plan = await PlanAsync(tenantId, nowUtc, ct);
        var result = await ApplyAsync(plan, ct);

        if (result.Enqueued + result.Deferred + result.GaveUp > 0)
        {
            _logger.LogInformation(
                "{Prefix} BACKFILL [{Tenant}] enqueued {Enqueued}, cleared {Cleared}, deferred {Deferred}, gave up {GaveUp}",
                LogPrefix, tenantId, result.Enqueued, result.Cleared, result.Deferred, result.GaveUp);
        }

        return result;
    }

    /// <summary>Reads the tenant's settled marks (oldest first) and judges each against the disk.</summary>
    public async Task<BackstopPlan> PlanAsync(string tenantId, DateTime nowUtc, CancellationToken ct = default)
    {
        var plan = new BackstopPlan { TenantId = tenantId, Cutoff = nowUtc - SettleTime };
        var cutoff = plan.Cutoff;

        var rows = await _db.DirtyWebpCells
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.MarkedAt <= cutoff)
            .OrderBy(d => d.MarkedAt)
            .Take(MaxRowsPerRun)
            .Select(d => new MarkedCell(d.Id, d.MapId, d.CellX, d.CellY, d.MarkedAt))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return plan;
        }

        var liveMapIds = (await _db.Maps
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId)
                .Select(m => m.Id)
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (!liveMapIds.Contains(row.MapId))
            {
                plan.Settled.Add(row); // normally cascaded away with the map
                continue;
            }

            // File stats first; the sources lookup (one query) only for a stale or incomplete chain.
            var chain = InspectChain(row.CellX, row.CellY, row.MarkedAt,
                (zoom, x, y) => Stat(_largeTileService.GetLargeTilePath(tenantId, row.MapId, zoom, x, y)));
            var needed = chain != ChainState.Fresh
                && NeedsRegeneration(chain, await ClassifyCellSourcesAsync(tenantId, row.MapId, row.CellX, row.CellY, ct));

            if (!needed)
            {
                plan.Settled.Add(row);
            }
            else if (ProcessedAttempts(tenantId, row) >= MaxAttempts)
            {
                plan.GiveUp.Add(row);
            }
            else
            {
                plan.Regenerate.Add(row);
            }
        }

        return plan;
    }

    /// <summary>
    /// Enqueues the plan's stale cells while the queue has headroom (the rest are deferred: their
    /// marks persist) and deletes the settled and given-up marks — only where MarkedAt has not
    /// moved past the plan's cutoff, so a re-mark made after the read survives.
    /// </summary>
    public async Task<BackstopResult> ApplyAsync(BackstopPlan plan, CancellationToken ct = default)
    {
        var enqueued = 0;
        var deferred = 0;

        for (int i = 0; i < plan.Regenerate.Count; i++)
        {
            if (_queue.PendingCount >= ZoomTileQueueService.BulkEnqueueDepthLimit)
            {
                deferred = plan.Regenerate.Count - i;
                break;
            }

            var row = plan.Regenerate[i];
            // Resolved before the enqueue: afterwards the cell reads as queued again. The drop
            // counter is read first, so a drop racing this enqueue voids the attempt, not another.
            var processed = ProcessedAttempts(plan.TenantId, row);
            var drops = _queue.DroppedCount;
            var request = Request(plan.TenantId, row);
            // IsQueued first: a request the processor has started no longer holds its dedup key
            // (BeginProcessing), so the enqueue alone would accept a duplicate of it.
            if (_queue.IsQueued(request) || !_queue.EnqueueZoomRegeneration(request))
            {
                continue; // still queued or being processed: not a new attempt
            }

            enqueued++;
            _attempts[Key(plan.TenantId, row)] = new AttemptState(row.MarkedAt, processed, drops);
        }

        if (plan.GiveUp.Count > 0)
        {
            _logger.LogWarning(
                "{Prefix} BACKFILL [{Tenant}] gave up on {Count} cell(s) still stale after {Attempts} regenerations, e.g. {Samples}; the Map Integrity drift scan reports what is left",
                LogPrefix, plan.TenantId, plan.GiveUp.Count, MaxAttempts,
                string.Join(", ", plan.GiveUp.Take(5).Select(r => $"map {r.MapId} cell ({r.CellX},{r.CellY})")));
        }

        var done = plan.Settled.Concat(plan.GiveUp).ToList();
        foreach (var row in done)
        {
            _attempts.TryRemove(Key(plan.TenantId, row), out _);
        }
        if (_attempts.Count > 100_000)
        {
            _attempts.Clear(); // marks of deleted maps cascade away unseen; only costs a few retries
        }

        var cleared = 0;
        var cutoff = plan.Cutoff;
        for (int i = 0; i < done.Count; i += ChunkSize)
        {
            var ids = done.Skip(i).Take(ChunkSize).Select(r => r.Id).ToList();
            cleared += await _db.DirtyWebpCells
                .Where(d => d.TenantId == plan.TenantId && ids.Contains(d.Id) && d.MarkedAt <= cutoff)
                .ExecuteDeleteAsync(ct);
        }

        return new BackstopResult(enqueued, cleared, deferred, plan.GiveUp.Count);
    }

    /// <summary>Distinct map ids with pending marks (the dirty scan's gap-fill covers them too).</summary>
    public async Task<HashSet<int>> GetMarkedMapIdsAsync(string tenantId, CancellationToken ct = default)
    {
        var ids = await _db.DirtyWebpCells
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId)
            .Select(d => d.MapId)
            .Distinct()
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    /// <summary>
    /// Carries the WebP work still pending in the legacy queue over to DirtyWebpCells: one mark
    /// per cell covered by a zoom-1 DirtyZoomTiles row (a zoom-1 tile spans 2x2 base grids, so the
    /// cell is coord >> 1 — SQLite's >> is an arithmetic shift, i.e. floor division for negatives
    /// too), stamped with the cell's newest CreatedAt. INSERT OR IGNORE never moves an existing
    /// mark, so running it again (every start) only re-adds already-regenerated cells, which the
    /// next run settles without enqueueing. Rows of dead maps are skipped (the FK would abort the
    /// statement — OR IGNORE does not cover foreign keys). Returns the marks inserted.
    /// </summary>
    public async Task<int> SeedFromLegacyDirtyRowsAsync(CancellationToken ct = default)
    {
        var inserted = await _db.Database.ExecuteSqlRawAsync(
            """
            INSERT OR IGNORE INTO DirtyWebpCells (TenantId, MapId, CellX, CellY, MarkedAt)
            SELECT d.TenantId, d.MapId, d.CoordX >> 1, d.CoordY >> 1, MAX(d.CreatedAt)
            FROM DirtyZoomTiles d
            WHERE d.Zoom = 1
              AND EXISTS (SELECT 1 FROM Maps m WHERE m.Id = d.MapId AND m.TenantId = d.TenantId)
            GROUP BY d.TenantId, d.MapId, d.CoordX >> 1, d.CoordY >> 1
            """,
            ct);

        if (inserted > 0)
        {
            _logger.LogInformation("{Prefix} BACKFILL seeded {Count} WebP cell mark(s) from pending zoom-1 dirty rows",
                LogPrefix, inserted);
        }

        return inserted;
    }

    /// <summary>
    /// Regenerations of this mark the processor handled. The latest one counts once it has left
    /// the queue and its batch has finished (<see cref="ZoomTileQueueService.IsQueued"/> covers
    /// both) with no drop since it was enqueued: DropOldest evicts unprocessed requests, and
    /// counting an evicted one would give up on a cell that was never regenerated while the
    /// queue overflowed (under drops nothing accrues, so the backstop keeps trying).
    /// </summary>
    private int ProcessedAttempts(string tenantId, MarkedCell row)
    {
        if (!_attempts.TryGetValue(Key(tenantId, row), out var tried) || tried.MarkedAt != row.MarkedAt)
        {
            return 0;
        }

        var latestProcessed = !_queue.IsQueued(Request(tenantId, row))
            && _queue.DroppedCount == tried.DropsAtEnqueue;
        return tried.Processed + (latestProcessed ? 1 : 0);
    }

    /// <summary>
    /// What the cell's zoom-0 rows can draw (≤ 16 rows, one stat each): every source present,
    /// some missing, all missing, or no rows at all.
    /// </summary>
    private async Task<CellSources> ClassifyCellSourcesAsync(string tenantId, int mapId, int cellX, int cellY, CancellationToken ct)
    {
        var minX = cellX * CellSize;
        var minY = cellY * CellSize;
        var files = await _db.Tiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.MapId == mapId && t.Zoom == 0
                && t.CoordX >= minX && t.CoordX < minX + CellSize
                && t.CoordY >= minY && t.CoordY < minY + CellSize
                && t.File != "")
            .Select(t => t.File)
            .ToListAsync(ct);

        if (files.Count == 0)
        {
            return CellSources.None;
        }

        var present = files.Count(f => File.Exists(Path.Combine(_gridStorage, f)));
        return present == files.Count ? CellSources.Renderable
            : present == 0 ? CellSources.Dark
            : CellSources.PartlyDark;
    }

    private static ZoomTileRequest Request(string tenantId, MarkedCell row) =>
        new(tenantId, row.MapId, row.CellX * CellSize, row.CellY * CellSize);

    private static TileFileStat Stat(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? new TileFileStat(true, info.Length, info.LastWriteTimeUtc)
            : new TileFileStat(false, 0, default);
    }

    private static (string, int, int, int) Key(string tenantId, MarkedCell row) =>
        (tenantId, row.MapId, row.CellX, row.CellY);
}
