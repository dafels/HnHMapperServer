using System.Collections.Concurrent;
using System.Diagnostics;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Singleton source of the tile tokens (map, coords, zoom, Cache) /map/updates sends at connect.
///
/// - <c>since &gt; 0</c> (every reconnect, i.e. nearly every read): an indexed DB delta query
///   (IX_Tiles_TenantId_Cache) returning only the changed rows. It never touches the snapshot, so it
///   reflects every committed writer (uploads, zoom rebuilds, imports, merges) without any of them
///   having to invalidate anything.
/// - <c>since &lt;= 0</c> (a page load): the tenant's whole list, from a per-tenant in-memory snapshot
///   of projected DTOs — once a full query per connect cost 3-5 s and starved the thread pool.
///   Concurrent first reads share one load; a failed load is not cached; an invalidation drops the
///   snapshot (and an in-flight load can never re-insert itself after it).
///
/// It used to cache one whole List per tenant for both paths, dropped by every gridUpload and every
/// ZoomQ batch, so most reconnects reloaded the whole tenant (~22k rows) under one process-wide lock
/// to answer a 0-6 row delta.
///
/// The DTOs of a snapshot are shared by every reader: never mutate them.
/// </summary>
public class TileCacheService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<TileCacheService> _logger;

    // TenantId -> snapshot load. Lazy<Task>: concurrent first reads share one load.
    private readonly ConcurrentDictionary<string, Lazy<Task<Snapshot>>> _snapshots = new(StringComparer.Ordinal);

    private sealed record Snapshot(IReadOnlyList<TileCacheDto> Tiles, DateTime LoadedAt);

    public TileCacheService(IServiceScopeFactory serviceScopeFactory, ILogger<TileCacheService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Tile tokens of one tenant: rows with Cache &gt; <paramref name="since"/> from the database when
    /// since &gt; 0, otherwise every row (tombstones included) from the cached snapshot.
    /// </summary>
    public async Task<IReadOnlyList<TileCacheDto>> GetTilesSinceAsync(string tenantId, long since)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        if (since > 0)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ITileRepository>()
                .GetTileCacheTokensAsync(tenantId, since);
        }

        var entry = _snapshots.GetOrAdd(
            tenantId,
            static (id, self) => new Lazy<Task<Snapshot>>(() => Task.Run(() => self.LoadSnapshotAsync(id))),
            this);
        try
        {
            return (await entry.Value).Tiles;
        }
        catch
        {
            // Not cached: value-matched, so a newer entry (after an invalidation) stays
            _snapshots.TryRemove(KeyValuePair.Create(tenantId, entry));
            throw;
        }
    }

    // Task.Run (above): Microsoft.Data.Sqlite runs "async" queries synchronously, and concurrent
    // first readers should await the shared task rather than block on Lazy's lock meanwhile.
    private async Task<Snapshot> LoadSnapshotAsync(string tenantId)
    {
        _logger.LogDebug("Loading tiles from database for tenant {TenantId} (first request)...", tenantId);
        var sw = Stopwatch.StartNew();

        var tiles = await LoadSnapshotTilesAsync(tenantId);

        _logger.LogInformation("Loaded {Count} tiles for tenant {TenantId} into cache in {TimeMs}ms",
            tiles.Count, tenantId, sw.Elapsed.TotalMilliseconds);
        return new Snapshot(tiles, DateTime.UtcNow);
    }

    /// <summary>Reads the whole tenant's tokens (test seam).</summary>
    protected virtual async Task<IReadOnlyList<TileCacheDto>> LoadSnapshotTilesAsync(string tenantId)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITileRepository>()
            .GetTileCacheTokensAsync(tenantId, 0);
    }

    /// <summary>
    /// Drops the page-load snapshot of a tenant (or of all tenants for null); the next since=0 read
    /// reloads it. Delta reads never need this. Call it when tiles are created, updated or deleted, so
    /// page loads see them.
    /// </summary>
    /// <param name="tenantId">The tenant ID to invalidate cache for, or null to invalidate all tenants</param>
    public Task InvalidateCacheAsync(string? tenantId = null)
    {
        if (tenantId == null)
        {
            var count = _snapshots.Count;
            _snapshots.Clear();
            if (count > 0)
            {
                _logger.LogInformation("Invalidating tile cache for all tenants ({Count} tenants cached)", count);
            }
            return Task.CompletedTask;
        }

        if (_snapshots.TryRemove(tenantId, out var removed) && TryGetCompleted(removed, out var snapshot))
        {
            _logger.LogDebug("Invalidated tile cache for tenant {TenantId} ({Count} tiles cached since {LoadedAt})",
                tenantId, snapshot.Tiles.Count, snapshot.LoadedAt);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Cache statistics for monitoring/debugging: completed snapshots only.
    /// </summary>
    /// <param name="tenantId">The tenant ID to get stats for, or null for all tenants</param>
    public Dictionary<string, (bool IsCached, int TileCount, DateTime LoadedAt)> GetCacheStats(string? tenantId = null)
    {
        var stats = new Dictionary<string, (bool IsCached, int TileCount, DateTime LoadedAt)>();
        foreach (var (id, entry) in _snapshots)
        {
            if ((tenantId == null || id == tenantId) && TryGetCompleted(entry, out var snapshot))
            {
                stats[id] = (true, snapshot.Tiles.Count, snapshot.LoadedAt);
            }
        }
        return stats;
    }

    private static bool TryGetCompleted(Lazy<Task<Snapshot>> entry, out Snapshot snapshot)
    {
        if (entry.IsValueCreated && entry.Value.IsCompletedSuccessfully)
        {
            snapshot = entry.Value.Result;
            return true;
        }

        snapshot = null!;
        return false;
    }
}
