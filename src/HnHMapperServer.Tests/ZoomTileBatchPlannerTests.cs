using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// ZoomQ batches are planned as distinct tiles: the per-request loop rebuilt the z0 cell and all six
/// ancestors of every uploaded grid, so a 3x3 gridUpdate neighbourhood (which shares z0 cells and
/// nearly every ancestor) cost 63 rebuilds where 7-28 tiles change.
/// </summary>
public class ZoomTileBatchPlannerTests
{
    private static ZoomTileRequest R(int x, int y, string tenant = "t", int map = 1) => new(tenant, map, x, y);

    private static List<ZoomTileRequest> Block3x3(int centerX, int centerY, string tenant = "t", int map = 1) =>
        (from dy in Enumerable.Range(-1, 3)
         from dx in Enumerable.Range(-1, 3)
         select R(centerX + dx, centerY + dy, tenant, map)).ToList();

    /// <summary>The old ZoomTileProcessorService loop's chain for one request.</summary>
    private static List<(int Zoom, int X, int Y)> OldChain(int baseX, int baseY)
    {
        var x = (int)Math.Floor(baseX / 4.0);
        var y = (int)Math.Floor(baseY / 4.0);
        var chain = new List<(int, int, int)> { (0, x, y) };
        for (var zoom = 1; zoom <= 6; zoom++)
        {
            x = (int)Math.Floor(x / 2.0);
            y = (int)Math.Floor(y / 2.0);
            chain.Add((zoom, x, y));
        }
        return chain;
    }

    [Theory]
    [InlineData(4, 4, 4, 10)]   // bases 3..5 straddle cells 0|1 on both axes; one ancestor per zoom
    [InlineData(2, 2, 1, 7)]    // bases 1..3: one cell
    [InlineData(0, 0, 4, 28)]   // the origin: -1|0 at every zoom, so four tiles per level
    public void Plan_3x3Block_PlansEachDistinctTileOnce(int centerX, int centerY, int cells, int tiles)
    {
        var plan = ZoomTileBatchPlanner.Plan(Block3x3(centerX, centerY));

        Assert.Equal(cells, plan.Cells.Count);
        Assert.Equal(tiles, plan.TileCount);         // the per-request loop did 9 x 7 = 63
        Assert.Equal(9, plan.Requests.Count);
        Assert.Equal(9, plan.Cells.Sum(c => c.Requests.Count));

        var keys = plan.Cells.Select(c => c.Key).Concat(plan.Parents).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(plan.Cells, c => Assert.Equal(0, c.Key.Zoom));
        Assert.All(plan.Parents, p => Assert.InRange(p.Zoom, 1, 6));
        var zooms = plan.Parents.Select(p => p.Zoom).ToList();
        Assert.Equal(zooms.OrderBy(z => z), zooms); // bottom-up: every child before its parent
    }

    [Theory]
    [InlineData(5, 9)]
    [InlineData(-1, -3)]
    [InlineData(-200, 77)]
    public void Plan_SingleRequest_IsTheOldLoopsZ0ToZ6Chain(int baseX, int baseY)
    {
        var plan = ZoomTileBatchPlanner.Plan(new[] { R(baseX, baseY) });

        var planned = plan.Cells.Select(c => c.Key).Concat(plan.Parents)
            .Select(k => (k.Zoom, k.X, k.Y))
            .ToList();
        Assert.Equal(OldChain(baseX, baseY), planned);
    }

    [Fact]
    public void Plan_NegativeCoordinates_UseFloorDivision()
    {
        var plan = ZoomTileBatchPlanner.Plan(new[] { R(-1, -3) });

        Assert.Equal(new ZoomTileKey("t", 1, 0, -1, -1), Assert.Single(plan.Cells).Key);
        Assert.All(plan.Parents, p => Assert.Equal((-1, -1), (p.X, p.Y)));
        Assert.Equal(6, plan.Parents.Count);
    }

    [Fact]
    public void Plan_GroupsRequestsByCell()
    {
        var plan = ZoomTileBatchPlanner.Plan(new[] { R(4, 4), R(5, 5), R(7, 7), R(8, 4) });

        Assert.Equal(2, plan.Cells.Count);
        var first = plan.Cells[0];
        Assert.Equal(new ZoomTileKey("t", 1, 0, 1, 1), first.Key);
        Assert.Equal(new[] { R(4, 4), R(5, 5), R(7, 7) }, first.Requests);
        Assert.Equal(new[] { R(8, 4) }, plan.Cells[1].Requests);
    }

    [Fact]
    public void Plan_SeparatesTenantsAndMaps()
    {
        var requests = new[]
        {
            R(4, 4, "a", 1), R(4, 4, "a", 2), R(4, 4, "b", 1), R(4, 4, "b", 2)
        };

        var plan = ZoomTileBatchPlanner.Plan(requests);

        Assert.Equal(4, plan.Cells.Count);
        Assert.Equal(24, plan.Parents.Count);
        Assert.Equal(4, plan.Parents.Count(p => p.Zoom == 6));
    }

    [Fact]
    public void Plan_Empty_PlansNothing()
    {
        var plan = ZoomTileBatchPlanner.Plan(Array.Empty<ZoomTileRequest>());

        Assert.Empty(plan.Cells);
        Assert.Empty(plan.Parents);
        Assert.Equal(0, plan.TileCount);
    }
}
