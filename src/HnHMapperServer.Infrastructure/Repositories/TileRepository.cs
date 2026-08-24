using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Infrastructure.Repositories;

public class TileRepository : ITileRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContextAccessor _tenantContext;

    public TileRepository(ApplicationDbContext context, ITenantContextAccessor tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<TileData?> GetTileAsync(int mapId, Coord coord, int zoom)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Tiles.AsNoTracking();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(t => t.TenantId == currentTenantId);
        }

        var entity = await query
            .FirstOrDefaultAsync(t =>
                t.MapId == mapId &&
                t.CoordX == coord.X &&
                t.CoordY == coord.Y &&
                t.Zoom == zoom);

        return entity == null ? null : MapToDomain(entity);
    }

    public async Task SaveTileAsync(TileData tileData)
    {
        // Retry logic for SQLite lock errors during concurrent imports
        const int maxRetries = 5;
        var delay = TimeSpan.FromMilliseconds(100);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Use IgnoreQueryFilters to work in background services (no HTTP context)
                // Then manually filter by the TenantId from the incoming tileData
                var existing = await _context.Tiles
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t =>
                        t.MapId == tileData.MapId &&
                        t.CoordX == tileData.Coord.X &&
                        t.CoordY == tileData.Coord.Y &&
                        t.Zoom == tileData.Zoom &&
                        t.TenantId == tileData.TenantId);

                var entity = MapFromDomain(tileData);

                if (existing != null)
                {
                    entity.Id = existing.Id;
                    _context.Entry(existing).CurrentValues.SetValues(entity);
                }
                else
                {
                    _context.Tiles.Add(entity);
                }

                await _context.SaveChangesAsync();
                return; // Success
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                (sqliteEx.SqliteErrorCode == 5 || sqliteEx.SqliteErrorCode == 6)) // SQLITE_BUSY or SQLITE_LOCKED
            {
                if (attempt == maxRetries)
                    throw; // Rethrow on final attempt

                // Exponential backoff with jitter
                await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(50)));
                delay *= 2;

                // Detach any tracked entities to avoid state issues on retry
                _context.ChangeTracker.Clear();
            }
        }
    }

    public async Task<List<TileData>> GetAllTilesAsync()
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Tiles.AsNoTracking();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(t => t.TenantId == currentTenantId);
        }

        var entities = await query.ToListAsync();
        return entities.Select(MapToDomain).ToList();
    }

    public async Task<Dictionary<int, int>> GetTileCountsByMapAsync(int zoom = 0)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Tiles.AsNoTracking().Where(t => t.Zoom == zoom);

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(t => t.TenantId == currentTenantId);
        }

        return await query
            .GroupBy(t => t.MapId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task DeleteTilesByMapAsync(int mapId)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Tiles.Where(t => t.MapId == mapId);

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(t => t.TenantId == currentTenantId);
        }

        var tiles = await query.ToListAsync();

        _context.Tiles.RemoveRange(tiles);
        await _context.SaveChangesAsync();
    }
    private static TileData MapToDomain(TileDataEntity entity) => new TileData
    {
        MapId = entity.MapId,
        Coord = new Coord(entity.CoordX, entity.CoordY),
        Zoom = entity.Zoom,
        File = entity.File,
        Cache = entity.Cache,
        TenantId = entity.TenantId,
        FileSizeBytes = entity.FileSizeBytes
    };

    private static TileDataEntity MapFromDomain(TileData tile) => new TileDataEntity
    {
        MapId = tile.MapId,
        CoordX = tile.Coord.X,
        CoordY = tile.Coord.Y,
        Zoom = tile.Zoom,
        File = tile.File,
        Cache = tile.Cache,
        TenantId = tile.TenantId,
        FileSizeBytes = tile.FileSizeBytes
    };

    public async Task SaveTilesBatchAsync(IEnumerable<TileData> tiles, bool skipExistenceCheck = false)
    {
        var tileList = tiles.ToList();
        if (tileList.Count == 0) return;

        // Dedupe within the incoming batch by the DB unique index (MapId, Zoom, CoordX, CoordY):
        // the existence check below only filters against rows already in the database, so two
        // tiles sharing a key inside ONE batch would both pass it and fail SaveChanges. First
        // occurrence wins — callers that care pick the winner before calling.
        if (tileList.Count > 1)
        {
            tileList = tileList.DistinctBy(t => (t.MapId, t.Zoom, t.Coord.X, t.Coord.Y)).ToList();
        }

        if (skipExistenceCheck)
        {
            // Caller guarantees the tiles don't already exist in the DB (e.g., newly generated
            // zoom tiles for a fresh map) — skip the expensive existence check query
            var tileEntities = tileList.Select(MapFromDomain).ToList();
            _context.Tiles.AddRange(tileEntities);
            await _context.SaveChangesAsync();
            return;
        }

        var newTiles = await FilterToTilesMissingFromDbAsync(tileList);
        if (newTiles.Count == 0) return;

        var entities = newTiles.Select(MapFromDomain).ToList();
        _context.Tiles.AddRange(entities);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a check-then-insert race: a concurrent writer (live gridUpdate, background
            // zoom rebuild) inserted one of these keys between the existence check and the
            // save. Detach this batch so the failed inserts stop poisoning the context,
            // re-check, and retry once with the survivors — a second failure is a real error
            // and propagates.
            foreach (var entity in entities)
            {
                _context.Entry(entity).State = EntityState.Detached;
            }

            var retryTiles = await FilterToTilesMissingFromDbAsync(newTiles);
            if (retryTiles.Count == 0) return;

            _context.Tiles.AddRange(retryTiles.Select(MapFromDomain));
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Filters out tiles that already exist in the database. Tile uniqueness is the
    /// (MapId, Zoom, CoordX, CoordY) unique index; TenantId is part of the key here for
    /// defense-in-depth (map ids never span tenants).
    /// </summary>
    private async Task<List<TileData>> FilterToTilesMissingFromDbAsync(List<TileData> tileList)
    {
        var existingKeys = new HashSet<(int MapId, int X, int Y, int Zoom, string TenantId)>();

        // Check in chunks with optimized coordinate-specific queries
        const int chunkSize = 500;
        foreach (var chunk in tileList.Chunk(chunkSize))
        {
            // Group by (MapId, Zoom, TenantId) for more efficient queries
            foreach (var group in chunk.GroupBy(t => new { t.MapId, t.Zoom, t.TenantId }))
            {
                var xCoords = group.Select(t => t.Coord.X).Distinct().ToList();
                var yCoords = group.Select(t => t.Coord.Y).Distinct().ToList();

                // Query only for specific coordinates instead of entire map
                var existing = await _context.Tiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(t => t.MapId == group.Key.MapId
                             && t.Zoom == group.Key.Zoom
                             && t.TenantId == group.Key.TenantId
                             && xCoords.Contains(t.CoordX)
                             && yCoords.Contains(t.CoordY))
                    .Select(t => new { t.MapId, t.CoordX, t.CoordY, t.Zoom, t.TenantId })
                    .ToListAsync();

                foreach (var e in existing)
                {
                    existingKeys.Add((e.MapId, e.CoordX, e.CoordY, e.Zoom, e.TenantId));
                }
            }
        }

        return tileList
            .Where(t => !existingKeys.Contains((t.MapId, t.Coord.X, t.Coord.Y, t.Zoom, t.TenantId)))
            .ToList();
    }
}
