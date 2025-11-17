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
}
