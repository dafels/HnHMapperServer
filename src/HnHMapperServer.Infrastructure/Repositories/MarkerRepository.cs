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

    public async Task SaveMarkerAsync(Marker marker, string key)
    {
        // Marker Keys are client-generated and can be the same across tenants
        // The UNIQUE constraint is on (Key, TenantId), so each tenant can have their own markers
        var currentTenantId = _tenantContext.GetRequiredTenantId();

        // Check if marker exists for current tenant (uses global query filter automatically)
        var existing = await _context.Markers
            .FirstOrDefaultAsync(m => m.Key == key && m.TenantId == currentTenantId);

        if (existing != null)
        {
            // Update existing marker for this tenant
            var entity = MapFromDomain(marker, key);
            entity.Id = existing.Id;
            _context.Entry(existing).CurrentValues.SetValues(entity);
            await _context.SaveChangesAsync();

            // Update the domain object with the ID
            if (marker.Id == 0)
            {
                marker.Id = entity.Id;
            }
        }
        else
        {
            // Insert new marker for this tenant
            var entity = MapFromDomain(marker, key);
            entity.Id = 0;  // Let database auto-generate ID (AUTOINCREMENT)
            _context.Markers.Add(entity);

            try
            {
                await _context.SaveChangesAsync();

                // Update the domain object with the generated ID
                if (marker.Id == 0)
                {
                    marker.Id = entity.Id;
                }
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint failed") == true)
            {
                // Race condition: another request inserted this marker between our check and insert
                // Detach the failed entity and retry as update
                _context.Entry(entity).State = EntityState.Detached;

                // Re-fetch the existing marker that was inserted by the other request
                existing = await _context.Markers
                    .FirstOrDefaultAsync(m => m.Key == key && m.TenantId == currentTenantId);

                if (existing != null)
                {
                    // Update the existing marker
                    var updateEntity = MapFromDomain(marker, key);
                    updateEntity.Id = existing.Id;
                    _context.Entry(existing).CurrentValues.SetValues(updateEntity);
                    await _context.SaveChangesAsync();

                    if (marker.Id == 0)
                    {
                        marker.Id = updateEntity.Id;
                    }
                }
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
