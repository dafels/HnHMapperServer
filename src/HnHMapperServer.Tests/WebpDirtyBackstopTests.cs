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
/// Tests for the dirty funnels (batched DirtyZoomTiles + DirtyWebpCells marking used by imports,
/// merges and the rebuild tool, and the per-tile path) and the pure rules of the durable WebP
/// backstop (WebpDirtyCellPlanner). Negative coordinates get special attention — plain integer
/// division truncates toward zero and used to mark the wrong parents. The backstop itself runs on
/// real SQLite in WebpDirtyCellBackstopTests.
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

    // ---------- DirtyWebpCells marking ----------

    [Fact]
    public async Task MarkParentTilesDirtyBatch_AlsoMarksWebpCell_FloorsNegatives()
    {
        await _tileService.MarkParentTilesDirtyBatchAsync(TenantId, 5, new[] { new Coord(-1, -5), new Coord(-4, -8) });

        var cells = await _db.DirtyWebpCells
            .Where(d => d.TenantId == TenantId && d.MapId == 5)
            .Select(d => new { d.CellX, d.CellY })
            .ToListAsync();

        // floor(-1/4) = -1, floor(-5/4) = -2 (truncation would give (0,-1)); (-4,-8) is the same cell.
        var cell = Assert.Single(cells);
        Assert.Equal((-1, -2), (cell.CellX, cell.CellY));
    }

    [Fact]
    public async Task MarkParentTilesDirtyBatch_ReMarkBumpsMarkedAt()
    {
        // The legacy DirtyZoomTiles CreatedAt never moves forward, so a second import flush into
        // an already-marked cell looked fresh once the cell had been regenerated in between.
        await _tileService.MarkParentTilesDirtyBatchAsync(TenantId, 6, new[] { new Coord(1, 1) });
        var first = await _db.DirtyWebpCells.AsNoTracking().SingleAsync(d => d.TenantId == TenantId && d.MapId == 6);
        var firstZoom1 = await _db.DirtyZoomTiles.AsNoTracking()
            .SingleAsync(d => d.TenantId == TenantId && d.MapId == 6 && d.Zoom == 1);

        await Task.Delay(20);
        await _tileService.MarkParentTilesDirtyBatchAsync(TenantId, 6, new[] { new Coord(0, 1) }); // same cell and z1 parent

        var second = await _db.DirtyWebpCells.AsNoTracking().SingleAsync(d => d.TenantId == TenantId && d.MapId == 6);
        Assert.Equal(first.Id, second.Id);
        Assert.True(second.MarkedAt > first.MarkedAt, "a re-mark must move MarkedAt forward");

        var secondZoom1 = await _db.DirtyZoomTiles.AsNoTracking()
            .SingleAsync(d => d.TenantId == TenantId && d.MapId == 6 && d.Zoom == 1);
        Assert.Equal(firstZoom1.CreatedAt, secondZoom1.CreatedAt); // legacy rows untouched
    }

    [Fact]
    public async Task SaveTileAsync_Zoom0_MarksWebpCell_ZoomN_DoesNot()
    {
        await _tileService.SaveTileAsync(7, new Coord(9, -1), 0, "some/file.png", 1, TenantId, 1);
        await _tileService.SaveTileAsync(7, new Coord(40, 40), 2, "some/zoom2.png", 1, TenantId, 1);

        var cell = await _db.DirtyWebpCells.SingleAsync(d => d.TenantId == TenantId && d.MapId == 7);
        Assert.Equal((2, -1), (cell.CellX, cell.CellY));
    }

    // ---------- planner (pure) ----------

    private static readonly DateTime Mark = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Chain stats from (zoom, length, mtime) entries; zooms not listed have no file.</summary>
    private static Func<int, int, int, WebpDirtyCellPlanner.TileFileStat> Files(
        params (int Zoom, long Length, DateTime Mtime)[] files) =>
        (zoom, _, _) =>
        {
            foreach (var f in files)
            {
                if (f.Zoom == zoom)
                    return new WebpDirtyCellPlanner.TileFileStat(true, f.Length, f.Mtime);
            }
            return new WebpDirtyCellPlanner.TileFileStat(false, 0, default);
        };

    [Fact]
    public void Planner_CellOf_FloorsNegatives()
    {
        Assert.Equal((0, 0), WebpDirtyCellPlanner.CellOf(3, 3));
        Assert.Equal((1, -1), WebpDirtyCellPlanner.CellOf(4, -1));
        Assert.Equal((-1, -2), WebpDirtyCellPlanner.CellOf(-4, -5));
    }

    [Fact]
    public void Planner_Chain_FloorHalvesNegativesUpToZoom6()
    {
        var chain = WebpDirtyCellPlanner.Chain(-3, 5).ToList();

        Assert.Equal(7, chain.Count);
        Assert.Equal((0, -3, 5), chain[0]);
        Assert.Equal((1, -2, 2), chain[1]); // floor(-3/2) = -2
        Assert.Equal((6, -1, 0), chain[6]);
    }

    [Fact]
    public void Planner_ParentOlderThanMark_StaleEvenWhenZoom0Fresh()
    {
        // Judging z0 alone called this cell done; its z1 parent never got the new data.
        var state = WebpDirtyCellPlanner.InspectChain(0, 0, Mark,
            Files((0, 500, Mark.AddMinutes(1)), (1, 500, Mark.AddHours(-2))));

        Assert.Equal(WebpDirtyCellPlanner.ChainState.Stale, state);
        Assert.True(WebpDirtyCellPlanner.NeedsRegeneration(state, WebpDirtyCellPlanner.CellSources.Renderable));
    }

    [Fact]
    public void Planner_WholeChainNewer_Fresh()
    {
        var state = WebpDirtyCellPlanner.InspectChain(0, 0, Mark,
            Files((0, 500, Mark.AddMinutes(1)), (1, 500, Mark.AddMinutes(1)), (6, 500, Mark.AddMinutes(2))));

        Assert.Equal(WebpDirtyCellPlanner.ChainState.Fresh, state);
        Assert.False(WebpDirtyCellPlanner.NeedsRegeneration(state, WebpDirtyCellPlanner.CellSources.Renderable));
    }

    [Fact]
    public void Planner_Zoom0Missing_OnlyRegeneratedWhenSomethingCanBeDrawn()
    {
        var state = WebpDirtyCellPlanner.InspectChain(0, 0, Mark, Files((1, 500, Mark.AddMinutes(1))));

        Assert.Equal(WebpDirtyCellPlanner.ChainState.Zoom0Missing, state);
        Assert.True(WebpDirtyCellPlanner.NeedsRegeneration(state, WebpDirtyCellPlanner.CellSources.Renderable));
        // A wipe already propagated: no rows, missing z0, fresh parents.
        Assert.False(WebpDirtyCellPlanner.NeedsRegeneration(state, WebpDirtyCellPlanner.CellSources.None));
    }

    [Fact]
    public void Planner_ZeroLengthFile_IsStale()
    {
        var state = WebpDirtyCellPlanner.InspectChain(0, 0, Mark,
            Files((0, 500, Mark.AddMinutes(1)), (3, 0, Mark.AddMinutes(1))));

        Assert.Equal(WebpDirtyCellPlanner.ChainState.Stale, state);
    }

    [Fact]
    public void Planner_StaleWithoutSources_RegeneratesToRemoveGhosts_DarkNever()
    {
        // No rows left (wipe): the stale z0 must go. Rows whose PNGs are all gone (dark): a
        // regeneration would only delete the last WebP copy of the area, so it is left alone.
        Assert.True(WebpDirtyCellPlanner.NeedsRegeneration(
            WebpDirtyCellPlanner.ChainState.Stale, WebpDirtyCellPlanner.CellSources.None));
        Assert.False(WebpDirtyCellPlanner.NeedsRegeneration(
            WebpDirtyCellPlanner.ChainState.Stale, WebpDirtyCellPlanner.CellSources.Dark));
        Assert.False(WebpDirtyCellPlanner.NeedsRegeneration(
            WebpDirtyCellPlanner.ChainState.Zoom0Missing, WebpDirtyCellPlanner.CellSources.Dark));
        // Some PNGs gone: a redraw would drop those rows from the cell's last WebP copy.
        Assert.False(WebpDirtyCellPlanner.NeedsRegeneration(
            WebpDirtyCellPlanner.ChainState.Stale, WebpDirtyCellPlanner.CellSources.PartlyDark));
        Assert.False(WebpDirtyCellPlanner.NeedsRegeneration(
            WebpDirtyCellPlanner.ChainState.Zoom0Missing, WebpDirtyCellPlanner.CellSources.PartlyDark));
    }
}
