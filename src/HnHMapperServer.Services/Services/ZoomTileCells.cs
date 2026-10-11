namespace HnHMapperServer.Services.Services;

/// <summary>
/// The WebP zoom-0 cells a ZoomQ batch regenerated, per map: what its mapRevision event carries
/// (<c>MapRevisionDto.Cells</c>) so viewers refresh only those cells and their ancestors instead
/// of every tile in view.
/// </summary>
public static class ZoomTileCells
{
    /// <summary>
    /// Cells listed per map at most. A map with more gets null, which clients read as "refresh
    /// everything" — the same as a bump from any other writer.
    /// </summary>
    public const int MaxCellsPerMap = 256;

    /// <summary>
    /// Distinct zoom-0 cells <c>[x, y]</c> (floor(base/4), as the regeneration uses) per map, in
    /// first-seen order; null for a map over <paramref name="maxCellsPerMap"/>.
    /// </summary>
    public static Dictionary<int, int[][]?> ChangedCellsByMap(
        IEnumerable<ZoomTileRequest> requests, int maxCellsPerMap = MaxCellsPerMap)
    {
        var result = new Dictionary<int, int[][]?>();
        foreach (var map in requests.GroupBy(r => r.MapId))
        {
            var cells = map
                .Select(r => WebpDirtyCellPlanner.CellOf(r.BaseX, r.BaseY))
                .Distinct()
                .ToList();
            result[map.Key] = cells.Count > maxCellsPerMap
                ? null
                : cells.Select(c => new[] { c.X, c.Y }).ToArray();
        }
        return result;
    }
}
