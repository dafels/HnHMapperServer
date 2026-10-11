using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// The zoom-0 cells a ZoomQ mapRevision event names (MapRevisionDto.Cells): the same floor(base/4)
/// the regeneration uses, distinct per map, and null (= refresh everything) over the cap.
/// </summary>
public class ZoomTileCellsTests
{
    private static ZoomTileRequest R(int x, int y, int map = 1) => new("t", map, x, y);

    [Fact]
    public void SixteenBaseGridsOfOneCell_AreOneCell()
    {
        var requests = from x in Enumerable.Range(0, 4) from y in Enumerable.Range(0, 4) select R(x, y);

        var cells = ZoomTileCells.ChangedCellsByMap(requests);

        Assert.Equal(new[] { 0, 0 }, Assert.Single(Assert.Single(cells).Value!));
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-4, -1)]
    [InlineData(-5, -2)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    public void CellsUseFloorDivision_LikeTheRegeneration(int baseX, int cellX)
    {
        var cells = ZoomTileCells.ChangedCellsByMap(new[] { R(baseX, baseX) });

        Assert.Equal(new[] { cellX, cellX }, Assert.Single(cells[1]!));
        Assert.Equal(ZoomTileBatchPlanner.CellOf(R(baseX, baseX)).X, cellX);
    }

    [Fact]
    public void Maps_AreListedSeparately()
    {
        var cells = ZoomTileCells.ChangedCellsByMap(new[] { R(1, 1, map: 7), R(9, 1, map: 7), R(1, 1, map: 8) });

        Assert.Equal(2, cells.Count);
        Assert.Equal(new[] { new[] { 0, 0 }, new[] { 2, 0 } }, cells[7]);
        Assert.Equal(new[] { new[] { 0, 0 } }, cells[8]);
    }

    [Fact]
    public void DuplicateRequests_AreNotCountedTwice()
    {
        var cells = ZoomTileCells.ChangedCellsByMap(new[] { R(5, 5), R(5, 5), R(6, 6) });

        Assert.Single(cells[1]!);
    }

    [Fact]
    public void OverTheCap_TheMapGetsNull_AtTheCapItIsListed()
    {
        var atCap = Enumerable.Range(0, 3).Select(i => R(i * 4, 0));
        var overCap = Enumerable.Range(0, 4).Select(i => R(i * 4, 0, map: 2));

        var cells = ZoomTileCells.ChangedCellsByMap(atCap.Concat(overCap), maxCellsPerMap: 3);

        Assert.Equal(3, cells[1]!.Length);
        Assert.True(cells.ContainsKey(2));
        Assert.Null(cells[2]); // a client refreshes the whole map
    }

    [Fact]
    public void DefaultCap_Is256()
    {
        var requests = Enumerable.Range(0, 257).Select(i => R(i * 4, 0));

        Assert.Null(ZoomTileCells.ChangedCellsByMap(requests)[1]);
        Assert.Equal(256, ZoomTileCells.ChangedCellsByMap(requests.Take(256))[1]!.Length);
    }

    [Fact]
    public void Empty_IsEmpty()
    {
        Assert.Empty(ZoomTileCells.ChangedCellsByMap(Array.Empty<ZoomTileRequest>()));
    }
}
