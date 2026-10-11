namespace HnHMapperServer.Services.Services;

/// <summary>One WebP pyramid tile of one map.</summary>
public readonly record struct ZoomTileKey(string TenantId, int MapId, int Zoom, int X, int Y);

/// <summary>A zoom-0 cell of a batch with the requests (base grids) that fall into it.</summary>
public sealed record ZoomTileCellWork(ZoomTileKey Key, IReadOnlyList<ZoomTileRequest> Requests);

/// <summary>
/// The distinct tiles one ZoomQ batch regenerates: its zoom-0 cells, then their distinct
/// ancestors level by level (all z1 tiles, then all z2, ... up to z6).
/// </summary>
public sealed record ZoomTileBatchPlan(
    IReadOnlyList<ZoomTileCellWork> Cells,
    IReadOnlyList<ZoomTileKey> Parents,
    IReadOnlyList<ZoomTileRequest> Requests)
{
    public int TileCount => Cells.Count + Parents.Count;
}

/// <summary>
/// Plans a ZoomQ batch as a set of distinct tiles. Requests are per uploaded grid, but the work
/// is per tile: one zoom-0 cell covers 4x4 base grids and one parent covers 2x2 children, so the
/// 3x3 neighbourhood of one gridUpdate shares z0 cells and nearly all of its z1-z6 ancestors.
/// Regenerating z0..z6 per request rebuilt the shared tiles once per grid (7 rebuilds per upload
/// where 2.5-4 tiles change) and threw every intermediate version away.
///
/// Bottom-up order composites each parent once, after all of its changed children in the batch.
/// Parents are planned whatever their zoom-0 cell yields: a cell whose sources are gone comes
/// back null (its stale file deleted), and the parents must recomposite without it or the old
/// imagery ghosts on at far-out zoom levels (wipes, merges, moved grids).
/// </summary>
public static class ZoomTileBatchPlanner
{
    /// <summary>The zoom-0 cell of a request (floor division, so negatives work).</summary>
    public static ZoomTileKey CellOf(ZoomTileRequest request)
    {
        var (x, y) = WebpDirtyCellPlanner.CellOf(request.BaseX, request.BaseY);
        return new ZoomTileKey(request.TenantId, request.MapId, 0, x, y);
    }

    public static ZoomTileBatchPlan Plan(IEnumerable<ZoomTileRequest> requests)
    {
        var all = requests.ToList();
        var cells = all
            .GroupBy(CellOf)
            .Select(group => new ZoomTileCellWork(group.Key, group.ToList()))
            .ToList();

        var parents = new List<ZoomTileKey>();
        IReadOnlyList<ZoomTileKey> level = cells.Select(cell => cell.Key).ToList();
        for (var zoom = 1; zoom <= WebpDirtyCellPlanner.MaxZoom; zoom++)
        {
            var next = level
                .Select(tile => tile with { Zoom = zoom, X = FloorDiv(tile.X, 2), Y = FloorDiv(tile.Y, 2) })
                .Distinct()
                .ToList();
            parents.AddRange(next);
            level = next;
        }

        return new ZoomTileBatchPlan(cells, parents, all);
    }

    private static int FloorDiv(int value, int divisor) => (int)Math.Floor(value / (double)divisor);
}
