namespace HnHMapperServer.Services.Services;

/// <summary>
/// Pure rules of the durable WebP backstop (<see cref="WebpDirtyCellBackstop"/>).
///
/// A DirtyWebpCells row marks one WebP zoom-0 cell (4x4 base grids) whose data changed at
/// MarkedAt. The mark is settled once every pyramid file that exists in the cell's chain
/// (z0 and the z1-z6 ancestors covering it) was written after MarkedAt: judging the z0 file
/// alone missed parents whose regeneration failed or never ran. Missing parents are fine —
/// on-view generation and the gap-fill batch build those from current data.
/// </summary>
public static class WebpDirtyCellPlanner
{
    /// <summary>Base grids per side of one WebP zoom-0 cell.</summary>
    public const int CellSize = 4;

    /// <summary>Top of the WebP pyramid (LargeTileService.MaxZoom).</summary>
    public const int MaxZoom = 6;

    /// <summary>What a cell's zoom-0 rows can draw, as far as the backstop needs to know.</summary>
    public enum CellSources
    {
        /// <summary>No zoom-0 rows (or only wipe tombstones): regenerating removes leftovers.</summary>
        None,

        /// <summary>
        /// Rows, but none of their source PNGs exist. Nothing can be drawn, and regenerating
        /// would only delete the last WebP copy of the area (REGEN-EMPTY) — restore the files.
        /// </summary>
        Dark,

        /// <summary>
        /// Some rows' source PNGs exist, others do not. The zoom-0 compose skips missing sources,
        /// so regenerating would redraw the cell (and its ancestors) without the missing rows —
        /// over files that may still hold the last copy of them. Restore or forget them first.
        /// </summary>
        PartlyDark,

        /// <summary>Every row's source PNG exists.</summary>
        Renderable
    }

    /// <summary>The cell's chain on disk, judged against one mark.</summary>
    public enum ChainState
    {
        /// <summary>Every existing chain file is a complete WebP written after the mark, z0 included.</summary>
        Fresh,

        /// <summary>z0 is missing; every existing ancestor is fresh.</summary>
        Zoom0Missing,

        /// <summary>An existing chain file predates the mark or is not a complete WebP.</summary>
        Stale
    }

    /// <summary>Existence, length and mtime of one pyramid file.</summary>
    public readonly record struct TileFileStat(bool Exists, long Length, DateTime LastWriteUtc);

    /// <summary>WebP zoom-0 cell of a base grid coordinate (floor division, so negatives work).</summary>
    public static (int X, int Y) CellOf(int baseX, int baseY) =>
        (FloorDiv(baseX, CellSize), FloorDiv(baseY, CellSize));

    /// <summary>The cell's chain: (zoom, x, y) for z0 and each ancestor up to z6, floor-halving.</summary>
    public static IEnumerable<(int Zoom, int X, int Y)> Chain(int cellX, int cellY)
    {
        int x = cellX, y = cellY;
        for (int zoom = 0; zoom <= MaxZoom; zoom++)
        {
            yield return (zoom, x, y);
            x = FloorDiv(x, 2);
            y = FloorDiv(y, 2);
        }
    }

    /// <summary>Judges the whole chain from file stats alone (no DB).</summary>
    public static ChainState InspectChain(
        int cellX, int cellY, DateTime markedAt, Func<int, int, int, TileFileStat> stat)
    {
        var zeroMissing = false;
        foreach (var (zoom, x, y) in Chain(cellX, cellY))
        {
            var file = stat(zoom, x, y);
            if (!file.Exists)
            {
                zeroMissing |= zoom == 0;
                continue;
            }

            if (file.Length < WebpTileFile.HeaderLength || file.LastWriteUtc < markedAt)
            {
                return ChainState.Stale;
            }
        }

        return zeroMissing ? ChainState.Zoom0Missing : ChainState.Fresh;
    }

    /// <summary>
    /// True when the cell must be regenerated: a stale chain always (unless any source is
    /// missing), a missing z0 only when the cell has something to draw. A cell with a missing
    /// source never needs it: regenerating would delete (dark) or redraw without those rows
    /// (partly dark) the last WebP copy of what they showed.
    /// </summary>
    public static bool NeedsRegeneration(ChainState chain, CellSources sources) => chain switch
    {
        ChainState.Fresh => false,
        _ when sources is CellSources.Dark or CellSources.PartlyDark => false,
        ChainState.Stale => true,
        _ => sources == CellSources.Renderable
    };

    private static int FloorDiv(int value, int divisor) => (int)Math.Floor(value / (double)divisor);
}
