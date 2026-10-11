using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

public sealed record ZoomTileRequest(string TenantId, int MapId, int BaseX, int BaseY);

/// <summary>
/// Singleton service backed by a bounded Channel for queuing zoom tile regeneration requests.
/// Provides O(1) deduplication so the same base tile isn't processed multiple times concurrently.
/// </summary>
public class ZoomTileQueueService
{
    private const int ChannelCapacity = 4096;
    private const string LogPrefix = "[ZoomQ]";

    /// <summary>
    /// Queue depth that bulk producers (the WebP backstop, rebuilds, the missing-source repair,
    /// merges and wipes) may fill the queue to. Leaves headroom in the bounded (DropOldest) channel so a big backlog
    /// never flushes out upload-driven requests; whatever doesn't fit stays in DirtyWebpCells and
    /// is enqueued by a later backstop run.
    /// </summary>
    public const int BulkEnqueueDepthLimit = 3000;

    private static readonly TimeSpan DropWarningInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<ZoomTileRequest> _channel;
    private readonly ConcurrentDictionary<(string TenantId, int MapId, int BaseX, int BaseY), byte> _pending = new();
    // Requests the processor has started (their dedup key already released, see BeginProcessing) and
    // not yet finished: IsQueued stays true for them until the batch is done.
    private readonly ConcurrentDictionary<(string TenantId, int MapId, int BaseX, int BaseY), byte> _inFlight = new();
    private readonly ILogger<ZoomTileQueueService> _logger;

    private long _droppedSinceLastWarning;
    private long _droppedTotal;
    private long _lastDropWarningTicks = long.MinValue;

    public ZoomTileQueueService(ILogger<ZoomTileQueueService> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<ZoomTileRequest>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            OnItemDropped);
    }

    /// <summary>
    /// DropOldest evicts a queued request to make room. Its dedup key must go with it: left in
    /// <see cref="_pending"/>, that base coord could never be enqueued again (every later request
    /// is "a duplicate" of one that no longer exists) and <see cref="PendingCount"/>, which bulk
    /// producers use as their headroom gauge, would only ever grow until a restart. Runs on the
    /// writer's path: keep it to a removal and two counters.
    /// </summary>
    private void OnItemDropped(ZoomTileRequest dropped)
    {
        _pending.TryRemove((dropped.TenantId, dropped.MapId, dropped.BaseX, dropped.BaseY), out _);
        Interlocked.Increment(ref _droppedSinceLastWarning);
        Interlocked.Increment(ref _droppedTotal);
    }

    /// <summary>
    /// Reader for the background processor to drain.
    /// </summary>
    public ChannelReader<ZoomTileRequest> Reader => _channel.Reader;

    /// <summary>
    /// Waits for the first request, then collects everything enqueued during <paramref name="window"/>.
    /// The window is a plain delay followed by a non-blocking drain: ending it by cancelling a pending
    /// ReadAsync (the old CancelAfter debounce) threw one OperationCanceledException per batch.
    /// Throws OperationCanceledException only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<List<ZoomTileRequest>> ReadBatchAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var batch = new List<ZoomTileRequest> { await _channel.Reader.ReadAsync(cancellationToken) };
        await Task.Delay(window, cancellationToken);
        while (_channel.Reader.TryRead(out var next))
        {
            batch.Add(next);
        }
        return batch;
    }

    /// <summary>
    /// Enqueue a zoom tile regeneration request (fire-and-forget, non-blocking).
    /// Deduplicates by (TenantId, MapId, BaseX, BaseY) so the same tile isn't queued twice.
    /// Returns true when the request was newly queued, false for a duplicate of a pending one.
    /// </summary>
    public bool EnqueueZoomRegeneration(ZoomTileRequest request)
    {
        var key = (request.TenantId, request.MapId, request.BaseX, request.BaseY);

        // O(1) dedup check
        if (!_pending.TryAdd(key, 0))
        {
            _logger.LogDebug("{Prefix} Skipped duplicate: tenant={TenantId} map={MapId} ({X},{Y})",
                LogPrefix, request.TenantId, request.MapId, request.BaseX, request.BaseY);
            return false;
        }

        if (!_channel.Writer.TryWrite(request))
        {
            // Only a completed channel refuses a write in DropOldest mode.
            _pending.TryRemove(key, out _);
            _logger.LogWarning("{Prefix} Channel closed, dropped: tenant={TenantId} map={MapId} ({X},{Y})",
                LogPrefix, request.TenantId, request.MapId, request.BaseX, request.BaseY);
            return false;
        }

        _logger.LogDebug("{Prefix} Enqueued: tenant={TenantId} map={MapId} ({X},{Y})",
            LogPrefix, request.TenantId, request.MapId, request.BaseX, request.BaseY);
        WarnAboutDropsIfDue();
        return true;
    }

    /// <summary>
    /// At most one warning per minute while the queue is overflowing. Dropped requests are not
    /// lost work: their zoom-0 saves wrote DirtyWebpCells marks, which the 5-minute backstop
    /// re-enqueues.
    /// </summary>
    private void WarnAboutDropsIfDue()
    {
        if (Interlocked.Read(ref _droppedSinceLastWarning) == 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastDropWarningTicks);
        if (last != long.MinValue && now - last < (long)DropWarningInterval.TotalMilliseconds)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastDropWarningTicks, now, last) != last)
        {
            return; // another writer is logging this interval
        }

        var dropped = Interlocked.Exchange(ref _droppedSinceLastWarning, 0);
        if (dropped > 0)
        {
            _logger.LogWarning(
                "{Prefix} Queue full: dropped {Count} oldest request(s) since the last warning ({Total} since start); the DirtyWebpCells backstop re-enqueues them",
                LogPrefix, dropped, Interlocked.Read(ref _droppedTotal));
        }
    }

    /// <summary>
    /// Releases a request's dedup key without processing it (a request the processor never got to,
    /// e.g. at shutdown), allowing future re-enqueues.
    /// </summary>
    public void MarkCompleted(ZoomTileRequest request)
    {
        var key = (request.TenantId, request.MapId, request.BaseX, request.BaseY);
        _pending.TryRemove(key, out _);
    }

    /// <summary>
    /// Called by the processor just before it reads the request's sources: releases the dedup key,
    /// so an upload landing from now on is queued again (into the next batch) instead of being
    /// skipped as a duplicate of a regeneration that has already read its data. The request stays
    /// <see cref="IsQueued"/> until <see cref="EndProcessing"/>. Every producer commits its file and
    /// tile row before it enqueues, so a request skipped before this point is still covered.
    /// </summary>
    public void BeginProcessing(ZoomTileRequest request)
    {
        var key = (request.TenantId, request.MapId, request.BaseX, request.BaseY);
        _inFlight[key] = 0; // first, so IsQueued never reads false in between
        _pending.TryRemove(key, out _);
    }

    /// <summary>
    /// Called by the processor once the batch that began <paramref name="request"/> is done. Never
    /// touches the dedup key: a re-enqueue made meanwhile stays pending.
    /// </summary>
    public void EndProcessing(ZoomTileRequest request) =>
        _inFlight.TryRemove((request.TenantId, request.MapId, request.BaseX, request.BaseY), out _);

    /// <summary>
    /// True while the request is queued or being processed (until its batch ends, or
    /// <see cref="MarkCompleted"/> or a drop releases it).
    /// </summary>
    public bool IsQueued(ZoomTileRequest request)
    {
        var key = (request.TenantId, request.MapId, request.BaseX, request.BaseY);
        return _pending.ContainsKey(key) || _inFlight.ContainsKey(key);
    }

    /// <summary>
    /// Number of items currently pending in the queue (for diagnostics).
    /// </summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Requests DropOldest evicted since start. The backstop compares it across one of its
    /// requests' lifetime to tell a processed regeneration from a dropped one.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref _droppedTotal);
}
