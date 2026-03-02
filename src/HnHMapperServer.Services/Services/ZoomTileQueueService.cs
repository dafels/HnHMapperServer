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

    private readonly Channel<ZoomTileRequest> _channel;
    private readonly ConcurrentDictionary<(string TenantId, int MapId, int BaseX, int BaseY), byte> _pending = new();
    private readonly ILogger<ZoomTileQueueService> _logger;

    public ZoomTileQueueService(ILogger<ZoomTileQueueService> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<ZoomTileRequest>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Reader for the background processor to drain.
    /// </summary>
    public ChannelReader<ZoomTileRequest> Reader => _channel.Reader;

    /// <summary>
    /// Enqueue a zoom tile regeneration request (fire-and-forget, non-blocking).
    /// Deduplicates by (TenantId, MapId, BaseX, BaseY) so the same tile isn't queued twice.
    /// </summary>
    public void EnqueueZoomRegeneration(ZoomTileRequest request)
    {
        var key = (request.TenantId, request.MapId, request.BaseX, request.BaseY);

        // O(1) dedup check
        if (!_pending.TryAdd(key, 0))
        {
            _logger.LogDebug("{Prefix} Skipped duplicate: tenant={TenantId} map={MapId} ({X},{Y})",
                LogPrefix, request.TenantId, request.MapId, request.BaseX, request.BaseY);
            return;
        }

        if (!_channel.Writer.TryWrite(request))
        {
            // Channel is full (DropOldest mode handles this, but TryWrite can still fail in edge cases)
            _pending.TryRemove(key, out _);
            _logger.LogWarning("{Prefix} Channel full, dropped: tenant={TenantId} map={MapId} ({X},{Y})",
                LogPrefix, request.TenantId, request.MapId, request.BaseX, request.BaseY);
        }
        else
        {
            _logger.LogDebug("{Prefix} Enqueued: tenant={TenantId} map={MapId} ({X},{Y})",
                LogPrefix, request.TenantId, request.MapId, request.BaseX, request.BaseY);
        }
    }

    /// <summary>
    /// Called by the processor after a request is fully processed, allowing future re-enqueues.
    /// </summary>
    public void MarkCompleted(ZoomTileRequest request)
    {
        var key = (request.TenantId, request.MapId, request.BaseX, request.BaseY);
        _pending.TryRemove(key, out _);
    }

    /// <summary>
    /// Number of items currently pending in the queue (for diagnostics).
    /// </summary>
    public int PendingCount => _pending.Count;
}
