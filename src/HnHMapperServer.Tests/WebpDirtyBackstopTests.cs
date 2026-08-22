using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using ITenantContextAccessor = HnHMapperServer.Core.Interfaces.ITenantContextAccessor;

namespace HnHMapperServer.Tests;

/// <summary>
/// Tests for the durable WebP regeneration backstop: batched DirtyZoomTiles marking (used by
/// imports, merges and the rebuild tool) and the pure cell derivation the 5-minute scan uses.
/// Negative coordinates get special attention — plain integer division truncates toward zero
/// and used to mark the wrong parents.
/// </summary>
public class WebpDirtyBackstopTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly TileService _tileService;

    private const string TenantId = "dirty-batch-tenant";

    public WebpDirtyBackstopTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDbContext(options);

        var mockTenantContext = new Mock<ITenantContextAccessor>();
        mockTenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TenantId);
        var tileRepository = new TileRepository(_db, mockTenantContext.Object);
        var gridRepository = new GridRepository(_db, mockTenantContext.Object);

        _tileService = new TileService(
            tileRepository,
            gridRepository,
            Mock.Of<IUpdateNotificationService>(),
            Mock.Of<IStorageQuotaService>(),
            Mock.Of<ILogger<TileService>>(),
            _db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task MarkParentTilesDirtyBatch_WritesAllSixZoomParents()
    {
        await _tileService.MarkParentTilesDirtyBatchAsync(TenantId, 1, new[] { new Coord(5, 3) });

        var rows = await _db.DirtyZoomTiles.IgnoreQueryFilters()
            .Where(d => d.TenantId == TenantId && d.MapId == 1)
            .OrderBy(d => d.Zoom)
            .Select(d => new { d.Zoom, d.CoordX, d.CoordY })
            .ToListAsync();

        // Parents of (5,3): z1 (2,1), z2 (1,0), z3 (0,0), z4-z6 (0,0)
        Assert.Equal(6, rows.Count);
        Assert.Equal((1, 2, 1), (rows[0].Zoom, rows[0].CoordX, rows[0].CoordY));
        Assert.Equal((2, 1, 0), (rows[1].Zoom, rows[1].CoordX, rows[1].CoordY));
        Assert.Equal((3, 0, 0), (rows[2].Zoom, rows[2].CoordX, rows[2].CoordY));
    }

    [Fact]
    public async Task MarkParentTilesDirtyBatch_FloorsNegativeCoordinates()
    {
        await _tileService.MarkParentTilesDirtyBatchAsync(TenantId, 2, new[] { new Coord(-1, -3) });

        var z1 = await _db.DirtyZoomTiles.IgnoreQueryFilters()
            .SingleAsync(d => d.TenantId == TenantId && d.MapId == 2 && d.Zoom == 1);

        // floor(-1/2) = -1 (truncation would give 0), floor(-3/2) = -2 (truncation: -1)
        Assert.Equal(-1, z1.CoordX);
        Assert.Equal(-2, z1.CoordY);
    }

    [Fact]
    public async Task MarkParentTilesDirtyBatch_DedupesAndIsIdempotent()
    {
        // 16 base coords of one 4x4 cell share most parents; a second call adds nothing.
        var coords = new List<Coord>();
        for (int x = 0; x < 4; x++)
            for (int y = 0; y < 4; y++)
                coords.Add(new Coord(x, y));

        await _tileService.MarkParentTilesDirtyBatchAsync(TenantId, 3, coords);
        var afterFirst = await _db.DirtyZoomTiles.IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == TenantId && d.MapId == 3);

        await _tileService.MarkParentTilesDirtyBatchAsync(TenantId, 3, coords);
        var afterSecond = await _db.DirtyZoomTiles.IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == TenantId && d.MapId == 3);

        // z1: 2x2=4 parents, z2: 1, z3-z6: 1 each = 9 distinct rows
        Assert.Equal(9, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task SaveTileAsync_MarksFloorCorrectParents_ForNegativeCoords()
    {
        // The per-upload path used plain /2 and marked parent (0,0) for base (-1,-1),
        // so the legacy rebuild regenerated the wrong tile at every negative coordinate.
        await _tileService.SaveTileAsync(4, new Coord(-1, -1), 0, "some/file.png", 1, TenantId, 1);

        var z1 = await _db.DirtyZoomTiles.IgnoreQueryFilters()
            .SingleAsync(d => d.TenantId == TenantId && d.MapId == 4 && d.Zoom == 1);

        Assert.Equal(-1, z1.CoordX);
        Assert.Equal(-1, z1.CoordY);
    }

    [Fact]
    public void PlannerDerivesCellsWithNewestMarkAndFloorsNegatives()
    {
        var t1 = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);

        var cells = WebpDirtyCellPlanner.DeriveCells(new[]
        {
            // zoom-1 coords (2,1) and (3,1) both fall into webp cell (1,0)
            (MapId: 7, CoordX: 2, CoordY: 1, CreatedAt: t1),
            (MapId: 7, CoordX: 3, CoordY: 1, CreatedAt: t2),
            // zoom-1 (-1,-1) -> cell (-1,-1) (truncation would give (0,0))
            (MapId: 7, CoordX: -1, CoordY: -1, CreatedAt: t1),
            // different map, same coords -> separate cell
            (MapId: 8, CoordX: 2, CoordY: 1, CreatedAt: t1),
        });

        Assert.Equal(3, cells.Count);

        var merged = Assert.Single(cells, c => c.MapId == 7 && c.CellX == 1 && c.CellY == 0);
        Assert.Equal(t2, merged.NewestMark);

        Assert.Single(cells, c => c.MapId == 7 && c.CellX == -1 && c.CellY == -1);
        Assert.Single(cells, c => c.MapId == 8 && c.CellX == 1 && c.CellY == 0);
    }
}
