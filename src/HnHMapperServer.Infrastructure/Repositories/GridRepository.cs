using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Infrastructure.Repositories;

public class GridRepository : IGridRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContextAccessor _tenantContext;

    public GridRepository(ApplicationDbContext context, ITenantContextAccessor tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<GridData?> GetGridAsync(string gridId)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Grids.AsNoTracking();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(g => g.TenantId == currentTenantId);
        }

        var entity = await query.FirstOrDefaultAsync(g => g.Id == gridId);
        return entity == null ? null : MapToDomain(entity);
    }

    public async Task SaveGridAsync(GridData gridData)
    {
        // Retry logic for SQLite lock errors during concurrent imports
        const int maxRetries = 5;
        var delay = TimeSpan.FromMilliseconds(100);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Grid IDs are client-generated content hashes that can be the same across tenants
                // The PRIMARY KEY is (Id, TenantId), so each tenant can have their own copy
                var currentTenantId = _tenantContext.GetRequiredTenantId();

                // Check if grid exists for current tenant (uses global query filter automatically)
                var existing = await _context.Grids
                    .FirstOrDefaultAsync(g => g.Id == gridData.Id && g.TenantId == currentTenantId);

                if (existing != null)
                {
                    // Update existing grid for this tenant
                    var entity = MapFromDomain(gridData);
                    _context.Entry(existing).CurrentValues.SetValues(entity);
                }
                else
                {
                    // Insert new grid for this tenant
                    var entity = MapFromDomain(gridData);
                    _context.Grids.Add(entity);
                }

                await _context.SaveChangesAsync();
                return; // Success
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (
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

    public async Task DeleteGridAsync(string gridId)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Grids.AsQueryable();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(g => g.TenantId == currentTenantId);
        }

        var grid = await query.FirstOrDefaultAsync(g => g.Id == gridId);
        if (grid != null)
        {
            _context.Grids.Remove(grid);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<List<GridData>> GetAllGridsAsync()
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Grids.AsNoTracking();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(g => g.TenantId == currentTenantId);
        }

        var entities = await query.ToListAsync();
        return entities.Select(MapToDomain).ToList();
    }

    public async Task<List<GridData>> GetGridsByMapAsync(int mapId)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Grids.AsNoTracking().Where(g => g.Map == mapId);

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(g => g.TenantId == currentTenantId);
        }

        var entities = await query.ToListAsync();
        return entities.Select(MapToDomain).ToList();
    }

    public async Task<List<GridData>> GetGridsByMapInAreaAsync(int mapId, int minX, int minY, int maxX, int maxY)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        // Range predicate rides IX_Grids_Map_CoordX_CoordY; bounds are inclusive.
        var query = _context.Grids.AsNoTracking()
            .Where(g => g.Map == mapId
                        && g.CoordX >= minX && g.CoordX <= maxX
                        && g.CoordY >= minY && g.CoordY <= maxY);

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(g => g.TenantId == currentTenantId);
        }

        var entities = await query.ToListAsync();
        return entities.Select(MapToDomain).ToList();
    }

    public async Task DeleteGridsByMapAsync(int mapId)
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Grids.Where(g => g.Map == mapId);

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(g => g.TenantId == currentTenantId);
        }

        var grids = await query.ToListAsync();

        _context.Grids.RemoveRange(grids);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> AnyGridsExistAsync()
    {
        // Explicit tenant filtering for defense-in-depth
        var currentTenantId = _tenantContext.GetCurrentTenantId();

        var query = _context.Grids.AsNoTracking();

        // If tenant context is available, add explicit filter
        if (!string.IsNullOrEmpty(currentTenantId))
        {
            query = query.Where(g => g.TenantId == currentTenantId);
        }

        return await query.AnyAsync();
    }

    private static GridData MapToDomain(GridDataEntity entity) => new GridData
    {
        Id = entity.Id,
        Coord = new Coord(entity.CoordX, entity.CoordY),
        NextUpdate = entity.NextUpdate,
        Map = entity.Map
    };

    private GridDataEntity MapFromDomain(GridData grid) => new GridDataEntity
    {
        Id = grid.Id,
        CoordX = grid.Coord.X,
        CoordY = grid.Coord.Y,
        NextUpdate = grid.NextUpdate,
        Map = grid.Map,
        TenantId = _tenantContext.GetRequiredTenantId()
    };

    public async Task<HashSet<string>> GetExistingGridIdsAsync(IEnumerable<string> gridIds)
    {
        var currentTenantId = _tenantContext.GetRequiredTenantId();
        var idList = gridIds.ToList();
        var result = new HashSet<string>();

        // SQLite performs well with IN clauses up to ~500 items
        const int chunkSize = 500;
        foreach (var chunk in idList.Chunk(chunkSize))
        {
            var existing = await _context.Grids
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(g => g.TenantId == currentTenantId && chunk.Contains(g.Id))
                .Select(g => g.Id)
                .ToListAsync();

            result.UnionWith(existing);
        }

        return result;
    }

    public async Task SaveGridsBatchAsync(IEnumerable<GridData> grids, bool skipExistenceCheck = false)
    {
        var gridList = grids.ToList();
        if (gridList.Count == 0) return;

        var currentTenantId = _tenantContext.GetRequiredTenantId();

        List<GridData> newGrids;
        if (skipExistenceCheck)
        {
            // Caller has already filtered - skip redundant DB query
            newGrids = gridList;
        }
        else
        {
            // Filter out grids that already exist to avoid UNIQUE constraint violations
            var gridIds = gridList.Select(g => g.Id).ToList();
            var existingIds = await GetExistingGridIdsAsync(gridIds);
            newGrids = gridList.Where(g => !existingIds.Contains(g.Id)).ToList();
        }

        if (newGrids.Count == 0) return;

        var entities = newGrids.Select(g => new GridDataEntity
        {
            Id = g.Id,
            CoordX = g.Coord.X,
            CoordY = g.Coord.Y,
            NextUpdate = g.NextUpdate,
            Map = g.Map,
            TenantId = currentTenantId
        }).ToList();

        _context.Grids.AddRange(entities);
        await _context.SaveChangesAsync();
    }
}
