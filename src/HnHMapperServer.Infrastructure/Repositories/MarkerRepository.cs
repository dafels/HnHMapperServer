using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Infrastructure.Repositories;

public class MarkerRepository : IMarkerRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContextAccessor _tenantContext;

    public MarkerRepository(ApplicationDbContext context, ITenantContextAccessor tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<Marker?> GetMarkerAsync(int markerId)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Markers.AsNoTracking();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(m => m.TenantId == currentTenantId);
        }

        var entity = await query.FirstOrDefaultAsync(m => m.Id == markerId);
        return entity == null ? null : MapToDomain(entity);
    }

    public async Task<Marker?> GetMarkerByKeyAsync(string key)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Markers.AsNoTracking();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(m => m.TenantId == currentTenantId);
        }

        var entity = await query.FirstOrDefaultAsync(m => m.Key == key);
        return entity == null ? null : MapToDomain(entity);
    }

    public async Task<List<Marker>> GetAllMarkersAsync()
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Markers.AsNoTracking();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(m => m.TenantId == currentTenantId);
        }

        var entities = await query.ToListAsync();
        return entities.Select(MapToDomain).ToList();
    }

    public async Task<(List<FrontendMarker> Markers, int OrphanedCount)> GetFrontendMarkersAsync(CancellationToken cancellationToken = default)
    {
        // Explicit tenant filtering for defense-in-depth (the global query filters apply as well)
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var markers = _context.Markers.AsNoTracking();
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            markers = markers.Where(m => m.TenantId == currentTenantId);
        }

        // Left join on the Grids primary key (Id, TenantId): at most one grid per marker.
        var rows = await (
            from m in markers
            join g in _context.Grids.AsNoTracking()
                on new { GridId = m.GridId, m.TenantId } equals new { GridId = g.Id, g.TenantId } into grids
            from g in grids.DefaultIfEmpty()
            orderby m.Id
            select new
            {
                m.Id,
                m.Name,
                m.PositionX,
                m.PositionY,
                m.Image,
                m.Hidden,
                m.MaxReady,
                m.MinReady,
                m.Ready,
                HasGrid = g != null,
                Map = g != null ? g.Map : 0,
                GridX = g != null ? g.CoordX : 0,
                GridY = g != null ? g.CoordY : 0
            }).ToListAsync(cancellationToken);

        var result = new List<FrontendMarker>(rows.Count);
        var orphaned = 0;
        foreach (var row in rows)
        {
            if (!row.HasGrid)
            {
                orphaned++;
                continue;
            }

            result.Add(new FrontendMarker
            {
                Id = row.Id,
                Name = row.Name,
                Map = row.Map,
                Position = new Position(row.PositionX + row.GridX * 100, row.PositionY + row.GridY * 100),
                Image = row.Image,
                Hidden = row.Hidden,
                MaxReady = row.MaxReady,
                MinReady = row.MinReady,
                Ready = row.Ready
            });
        }

        return (result, orphaned);
    }

    public async Task SaveMarkerAsync(Marker marker, string key)
    {
        // Marker Keys are client-generated and can be the same across tenants
        // The UNIQUE constraint is on (Key, TenantId), so each tenant can have their own markers
        var currentTenantId = _tenantContext.GetRequiredTenantId();

        // Use SQLite's native upsert - atomic, no race condition, no error logging
        // ON CONFLICT DO UPDATE handles duplicates at the database level
        var sql = @"
            INSERT INTO Markers (Key, TenantId, Name, GridId, PositionX, PositionY, Image, Hidden, MaxReady, MinReady, Ready)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10})
            ON CONFLICT(Key, TenantId) DO UPDATE SET
                Name = excluded.Name,
                GridId = excluded.GridId,
                PositionX = excluded.PositionX,
                PositionY = excluded.PositionY,
                Image = excluded.Image,
                Hidden = excluded.Hidden,
                MaxReady = excluded.MaxReady,
                MinReady = excluded.MinReady,
                Ready = excluded.Ready";

        await _context.Database.ExecuteSqlRawAsync(sql,
            key, currentTenantId, marker.Name, marker.GridId,
            marker.Position.X, marker.Position.Y, marker.Image,
            marker.Hidden, marker.MaxReady, marker.MinReady, marker.Ready);

        // If the marker needs its ID set, query it back
        if (marker.Id == 0)
        {
            var entity = await _context.Markers
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Key == key && m.TenantId == currentTenantId);

            if (entity != null)
            {
                marker.Id = entity.Id;
            }
        }
    }

    public async Task DeleteMarkerAsync(string key)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Markers.AsQueryable();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(m => m.TenantId == currentTenantId);
        }

        var marker = await query.FirstOrDefaultAsync(m => m.Key == key);

        if (marker != null)
        {
            _context.Markers.Remove(marker);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<int> GetNextMarkerIdAsync()
    {
        // IMPORTANT: Marker IDs must be globally unique across all tenants
        // because Markers.Id is a PRIMARY KEY, not tenant-scoped
        var maxId = await _context.Markers
            .IgnoreQueryFilters()  // Get global max ID, not tenant-scoped
            .AsNoTracking()
            .MaxAsync(m => (int?)m.Id);

        return (maxId ?? 0) + 1;
    }

    public async Task<int> SaveMarkersBatchAsync(List<(Marker marker, string key)> markers)
    {
        if (markers.Count == 0)
            return 0;

        var currentTenantId = _tenantContext.GetRequiredTenantId();

        // Clear change tracker to avoid stale entities from previous operations
        _context.ChangeTracker.Clear();

        // Deduplicate the batch by key (take first marker for each key)
        var uniqueMarkers = markers
            .GroupBy(m => m.key)
            .Select(g => g.First())
            .ToList();

        // Get all unique keys we're trying to insert
        var keysToInsert = uniqueMarkers.Select(m => m.key).ToList();

        // Query existing keys in one batch (much faster than individual queries)
        var existingKeys = await _context.Markers
            .AsNoTracking()
            .Where(m => m.TenantId == currentTenantId && keysToInsert.Contains(m.Key))
            .Select(m => m.Key)
            .ToHashSetAsync();

        // Filter to only new markers (not in DB)
        var newMarkers = uniqueMarkers
            .Where(m => !existingKeys.Contains(m.key))
            .Select(m => MapFromDomain(m.marker, m.key))
            .ToList();

        if (newMarkers.Count == 0)
            return 0;

        // Reset IDs to let database auto-generate
        foreach (var entity in newMarkers)
        {
            entity.Id = 0;
        }

        // Bulk insert all new markers in one transaction
        // Use retry logic to handle race conditions
        const int maxRetries = 3;
        var delay = TimeSpan.FromMilliseconds(100);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _context.Markers.AddRange(newMarkers);
                await _context.SaveChangesAsync();
                return newMarkers.Count;
            }
            catch (DbUpdateException ex) when (
                ex.InnerException?.Message.Contains("UNIQUE constraint failed") == true &&
                attempt < maxRetries)
            {
                // Race condition: another request inserted some markers
                // Clear tracker and retry with re-filtered list
                _context.ChangeTracker.Clear();

                // Re-query existing keys
                existingKeys = await _context.Markers
                    .AsNoTracking()
                    .Where(m => m.TenantId == currentTenantId && keysToInsert.Contains(m.Key))
                    .Select(m => m.Key)
                    .ToHashSetAsync();

                // Re-filter markers
                newMarkers = uniqueMarkers
                    .Where(m => !existingKeys.Contains(m.key))
                    .Select(m => MapFromDomain(m.marker, m.key))
                    .ToList();

                if (newMarkers.Count == 0)
                    return 0;

                foreach (var entity in newMarkers)
                {
                    entity.Id = 0;
                }

                await Task.Delay(delay);
                delay *= 2;
            }
        }

        return newMarkers.Count;
    }

    public async Task<List<Marker>> GetMarkersByTenantAsync(string tenantId)
    {
        // Explicit tenant filtering - bypasses global query filter for background services
        var entities = await _context.Markers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .ToListAsync();

        return entities.Select(MapToDomain).ToList();
    }

    public async Task<int> BatchUpdateReadinessAsync(List<(int markerId, bool ready, long maxReady, long minReady)> updates, string tenantId)
    {
        if (updates.Count == 0)
            return 0;

        var markerIds = updates.Select(u => u.markerId).ToList();

        // Fetch all markers to update in one query
        var markers = await _context.Markers
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && markerIds.Contains(m.Id))
            .ToListAsync();

        if (markers.Count == 0)
            return 0;

        // Create a lookup for the updates
        var updateLookup = updates.ToDictionary(u => u.markerId, u => u);

        // Apply updates
        foreach (var marker in markers)
        {
            if (updateLookup.TryGetValue(marker.Id, out var update))
            {
                marker.Ready = update.ready;
                marker.MaxReady = update.maxReady;
                marker.MinReady = update.minReady;
            }
        }

        // Single SaveChanges for all updates
        await _context.SaveChangesAsync();

        return markers.Count;
    }

    public async Task<List<Marker>> GetOrphanedMarkersAsync(string tenantId)
    {
        // Find markers where the GridId doesn't exist in the Grids table for this tenant
        var orphanedMarkers = await _context.Markers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .Where(m => !_context.Grids.Any(g => g.Id == m.GridId && g.TenantId == tenantId))
            .ToListAsync();

        return orphanedMarkers.Select(MapToDomain).ToList();
    }

    public async Task<int> DeleteMarkersByIdsAsync(List<int> markerIds, string tenantId)
    {
        if (markerIds.Count == 0)
            return 0;

        var deleted = await _context.Markers
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && markerIds.Contains(m.Id))
            .ExecuteDeleteAsync();

        return deleted;
    }

    private static Marker MapToDomain(MarkerEntity entity) => new Marker
    {
        Id = entity.Id,
        Name = entity.Name,
        GridId = entity.GridId,
        Position = new Position(entity.PositionX, entity.PositionY),
        Image = entity.Image,
        Hidden = entity.Hidden,
        MaxReady = entity.MaxReady,
        MinReady = entity.MinReady,
        Ready = entity.Ready
    };

    private MarkerEntity MapFromDomain(Marker marker, string key) => new MarkerEntity
    {
        Id = marker.Id,
        Key = key,
        Name = marker.Name,
        GridId = marker.GridId,
        PositionX = marker.Position.X,
        PositionY = marker.Position.Y,
        Image = marker.Image,
        Hidden = marker.Hidden,
        MaxReady = marker.MaxReady,
        MinReady = marker.MinReady,
        Ready = marker.Ready,
        TenantId = _tenantContext.GetRequiredTenantId()
    };
}
