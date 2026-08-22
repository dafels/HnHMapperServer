namespace HnHMapperServer.Services.Services;

/// <summary>
/// Pure derivation of WebP zoom-0 cells from DirtyZoomTiles rows.
///
/// DirtyZoomTiles rows are written (zooms 1-6) for every zoom-0 tile save — uploads, merges,
/// imports — and double as the durable, restart-safe backstop for WebP regeneration: the
/// in-memory ZoomTileQueueService fast path is bounded (DropOldest) and lost on restart, so the
/// 5-minute scan re-derives affected cells from these rows and re-enqueues anything still stale.
///
/// One zoom-1 legacy parent covers 2x2 base coords; one WebP zoom-0 cell covers 4x4 base coords,
/// so cell = floor(zoom1Coord / 2).
/// </summary>
public static class WebpDirtyCellPlanner
{
    public sealed record DirtyCell(int MapId, int CellX, int CellY, DateTime NewestMark);

    public static List<DirtyCell> DeriveCells(
        IEnumerable<(int MapId, int CoordX, int CoordY, DateTime CreatedAt)> zoom1Rows)
    {
        var newest = new Dictionary<(int MapId, int CellX, int CellY), DateTime>();

        foreach (var row in zoom1Rows)
        {
            var key = (row.MapId,
                (int)Math.Floor(row.CoordX / 2.0),
                (int)Math.Floor(row.CoordY / 2.0));

            if (!newest.TryGetValue(key, out var current) || row.CreatedAt > current)
            {
                newest[key] = row.CreatedAt;
            }
        }

        return newest
            .Select(kv => new DirtyCell(kv.Key.MapId, kv.Key.CellX, kv.Key.CellY, kv.Value))
            .ToList();
    }
}
