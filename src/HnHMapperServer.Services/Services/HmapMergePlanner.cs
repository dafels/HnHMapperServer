using HnHMapperServer.Core.Models;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Why a segment fell back to CreateNew instead of merging.
/// </summary>
public enum SegmentFallbackReason
{
    /// <summary>Segment merges (no fallback).</summary>
    None,

    /// <summary>No grid id in the segment matched an existing grid.</summary>
    NoMatches,

    /// <summary>The winning (map, offset) group had fewer than <see cref="HmapMergePlanner.MIN_MERGE_MATCHES"/> agreeing matches.</summary>
    BelowMinMatches,

    /// <summary>Too few matches near the already-merged area (secondary segments only).</summary>
    NotProximate,

    /// <summary>High coordinate overlap with different content — a cave layer over the same coords.</summary>
    CaveDetected,

    /// <summary>Grids to be planted would land on cells already owned by different grid ids.</summary>
    CoordConflicts
}

/// <summary>
/// Merge-or-create decision for one .hmap segment.
/// </summary>
public sealed record SegmentMergeDecision(
    long SegmentId,
    bool ShouldMerge,
    int? TargetMapId,
    int OffsetX,
    int OffsetY,
    SegmentFallbackReason FallbackReason,
    string Reason);

/// <summary>
/// Pure decision engine for Merge-mode .hmap imports: which segments merge into which existing
/// map at which offset, and which fall back to new maps. Extracted from HmapImportService so the
/// voting/validation logic is unit-testable without rendering or a database.
///
/// Guard rails (2026-08 map-corruption incident):
/// - Offsets are voted per (map, offset) group — the winning group defines target map AND offset
///   atomically, so a segment can no longer merge into map B using map A's coordinate frame.
/// - The dominant segment must clear MIN_MERGE_MATCHES agreeing matches; previously it merged
///   unconditionally, letting a single stale shared grid misplace thousands of grids.
/// - Every would-merge segment gets a pre-plant conflict scan: if any grid to be inserted lands
///   on a cell already owned by a different grid id (in the DB or by an earlier-accepted segment
///   of the same import), the segment falls back to CreateNew instead of double-claiming cells.
/// - Sentinel grids (GridId == 0 — the client's placeholder) never match and never count.
/// </summary>
public static class HmapMergePlanner
{
    /// <summary>Manhattan distance for the spatial proximity check.</summary>
    public const int PROXIMITY_THRESHOLD = 10;

    /// <summary>Minimum Grid ID matches near the merged area for a secondary segment to merge.</summary>
    public const int MIN_PROXIMATE_MATCHES = 5;

    /// <summary>Minimum agreeing (map, offset) matches for the dominant segment to merge at all.</summary>
    public const int MIN_MERGE_MATCHES = 5;

    /// <summary>% coordinate overlap that triggers cave detection.</summary>
    public const double CAVE_OVERLAP_THRESHOLD = 50.0;

    /// <summary>% content match below which an overlapping segment is treated as a cave.</summary>
    public const double CAVE_CONTENT_THRESHOLD = 10.0;

    public static Dictionary<long, SegmentMergeDecision> Compute(
        IReadOnlyList<(long SegmentId, IReadOnlyList<HmapGridData> Grids)> segments,
        IReadOnlyList<GridData> existingGrids,
        ILogger? logger = null)
    {
        var decisions = new Dictionary<long, SegmentMergeDecision>();

        // Unique Grid ID -> grid (only ids that appear once — unique content)
        var uniqueGridById = existingGrids
            .GroupBy(g => g.Id)
            .Where(grp => grp.Count() == 1)
            .ToDictionary(grp => grp.Key, grp => grp.First());

        // Every existing id (unique or not) — an id already in the DB is skipped at insert time,
        // so it can never double-claim a cell.
        var allExistingIds = new HashSet<string>(existingGrids.Select(g => g.Id));

        // MapId -> (Coord -> owning grid id). TryAdd, not ToDictionary: a previously corrupted
        // database can hold several grids on one cell, and any owner blocks the cell.
        var gridIdByMapAndCoord = new Dictionary<int, Dictionary<Coord, string>>();
        foreach (var grid in existingGrids)
        {
            if (!gridIdByMapAndCoord.TryGetValue(grid.Map, out var byCoord))
            {
                byCoord = new Dictionary<Coord, string>();
                gridIdByMapAndCoord[grid.Map] = byCoord;
            }
            byCoord.TryAdd(grid.Coord, grid.Id);
        }

        logger?.LogInformation("Merge validation: {TotalGrids} existing grids, {UniqueGrids} unique Grid IDs",
            existingGrids.Count, uniqueGridById.Count);

        // Per-segment winning (map, offset) group via per-group voting
        var segmentOffsets = new Dictionary<long, (int TargetMapId, int OffsetX, int OffsetY, int MatchCount, IReadOnlyList<HmapGridData> Grids)>();

        foreach (var (segmentId, segmentGrids) in segments)
        {
            // Sentinel grids never match, never count, and are never planted
            var realGrids = segmentGrids.Where(g => g.GridId != 0).ToList();

            var matches = realGrids
                .Where(g => uniqueGridById.ContainsKey(g.GridIdString))
                .Select(g => (
                    FileGrid: g,
                    DbGrid: uniqueGridById[g.GridIdString],
                    OffsetX: uniqueGridById[g.GridIdString].Coord.X - g.TileX,
                    OffsetY: uniqueGridById[g.GridIdString].Coord.Y - g.TileY
                ))
                .ToList();

            if (matches.Count == 0)
            {
                decisions[segmentId] = new SegmentMergeDecision(segmentId, false, null, 0, 0,
                    SegmentFallbackReason.NoMatches, "no Grid ID matches");
                logger?.LogInformation("Segment {SegId:X}: No Grid ID matches - will create new map", segmentId);
                continue;
            }

            // Vote per (map, offset) — the winner defines target map AND offset together.
            // Offsets from different maps live in different coordinate frames and must never mix.
            var winner = matches
                .GroupBy(m => (m.DbGrid.Map, m.OffsetX, m.OffsetY))
                .OrderByDescending(grp => grp.Count())
                .First();

            segmentOffsets[segmentId] = (winner.Key.Map, winner.Key.OffsetX, winner.Key.OffsetY, winner.Count(), realGrids);

            logger?.LogInformation(
                "Segment {SegId:X}: {MatchCount} agreeing Grid ID matches, offset ({OffsetX},{OffsetY}), target map {MapId}",
                segmentId, winner.Count(), winner.Key.OffsetX, winner.Key.OffsetY, winner.Key.Map);
        }

        if (segmentOffsets.Count == 0)
            return decisions;

        var mergedCoords = new HashSet<(int X, int Y)>();
        var plantedByMap = new Dictionary<int, HashSet<(int X, int Y)>>();

        // Cells this segment would actually insert that are already owned by a different grid id
        List<(int X, int Y)> FindConflicts(IReadOnlyList<HmapGridData> grids, int targetMapId, int offsetX, int offsetY)
        {
            gridIdByMapAndCoord.TryGetValue(targetMapId, out var targetCells);
            plantedByMap.TryGetValue(targetMapId, out var planted);

            var conflicts = new List<(int X, int Y)>();
            foreach (var grid in grids)
            {
                if (allExistingIds.Contains(grid.GridIdString))
                    continue; // skipped at insert time — cannot double-claim

                var dest = (X: grid.TileX + offsetX, Y: grid.TileY + offsetY);
                if ((targetCells != null && targetCells.ContainsKey(new Coord(dest.X, dest.Y)))
                    || (planted != null && planted.Contains(dest)))
                {
                    conflicts.Add(dest);
                }
            }
            return conflicts;
        }

        void RegisterAccepted(IReadOnlyList<HmapGridData> grids, int targetMapId, int offsetX, int offsetY)
        {
            if (!plantedByMap.TryGetValue(targetMapId, out var planted))
            {
                planted = new HashSet<(int X, int Y)>();
                plantedByMap[targetMapId] = planted;
            }
            foreach (var grid in grids)
            {
                var dest = (X: grid.TileX + offsetX, Y: grid.TileY + offsetY);
                mergedCoords.Add(dest);
                if (!allExistingIds.Contains(grid.GridIdString))
                    planted.Add(dest);
            }
        }

        string ConflictReason(List<(int X, int Y)> conflicts, int targetMapId) =>
            $"{conflicts.Count} destination cells already owned on map {targetMapId} " +
            $"(e.g. {string.Join(", ", conflicts.Take(3).Select(c => $"({c.X},{c.Y})"))})";

        var ordered = segmentOffsets.OrderByDescending(s => s.Value.MatchCount).ToList();

        // ---- Dominant segment ----
        var dominant = ordered[0];
        var dom = dominant.Value;
        if (dom.MatchCount < MIN_MERGE_MATCHES)
        {
            decisions[dominant.Key] = new SegmentMergeDecision(dominant.Key, false, null, 0, 0,
                SegmentFallbackReason.BelowMinMatches,
                $"only {dom.MatchCount}/{MIN_MERGE_MATCHES} agreeing Grid ID matches");
            logger?.LogInformation(
                "Segment {SegId:X}: NEW MAP (below minimum) - only {Count}/{Min} agreeing Grid ID matches",
                dominant.Key, dom.MatchCount, MIN_MERGE_MATCHES);
            // mergedCoords stays empty, so every secondary naturally fails the proximity check.
        }
        else
        {
            var conflicts = FindConflicts(dom.Grids, dom.TargetMapId, dom.OffsetX, dom.OffsetY);
            if (conflicts.Count > 0)
            {
                decisions[dominant.Key] = new SegmentMergeDecision(dominant.Key, false, null, 0, 0,
                    SegmentFallbackReason.CoordConflicts, ConflictReason(conflicts, dom.TargetMapId));
                logger?.LogWarning(
                    "Segment {SegId:X}: NEW MAP (coord conflicts) - {Count} destination cells already owned on map {MapId}",
                    dominant.Key, conflicts.Count, dom.TargetMapId);
            }
            else
            {
                decisions[dominant.Key] = new SegmentMergeDecision(dominant.Key, true, dom.TargetMapId,
                    dom.OffsetX, dom.OffsetY, SegmentFallbackReason.None,
                    $"{dom.MatchCount} Grid ID matches (dominant)");
                RegisterAccepted(dom.Grids, dom.TargetMapId, dom.OffsetX, dom.OffsetY);
                logger?.LogInformation("Segment {SegId:X}: MERGE (dominant) - {MatchCount} Grid ID matches",
                    dominant.Key, dom.MatchCount);
            }
        }

        // ---- Secondary segments ----
        foreach (var (segId, so) in ordered.Skip(1))
        {
            // SPATIAL PROXIMITY CHECK
            int proximateMatches = 0;
            foreach (var grid in so.Grids)
            {
                if (uniqueGridById.TryGetValue(grid.GridIdString, out var dbGrid))
                {
                    bool isProximate = mergedCoords.Any(mc =>
                        Math.Abs(dbGrid.Coord.X - mc.X) + Math.Abs(dbGrid.Coord.Y - mc.Y) <= PROXIMITY_THRESHOLD);
                    if (isProximate)
                        proximateMatches++;
                }
            }

            if (proximateMatches < MIN_PROXIMATE_MATCHES)
            {
                decisions[segId] = new SegmentMergeDecision(segId, false, null, 0, 0,
                    SegmentFallbackReason.NotProximate,
                    $"not proximate: only {proximateMatches}/{MIN_PROXIMATE_MATCHES} matches near merged area");
                logger?.LogInformation("Segment {SegId:X}: NEW MAP (not proximate) - only {Count} matches near merged area",
                    segId, proximateMatches);
                continue;
            }

            // CAVE DETECTION - check against target map's grids only
            int coordOverlapCount = 0, contentMatchCount = 0;
            if (gridIdByMapAndCoord.TryGetValue(so.TargetMapId, out var targetMapCoords))
            {
                foreach (var grid in so.Grids)
                {
                    var adjustedCoord = new Coord(grid.TileX + so.OffsetX, grid.TileY + so.OffsetY);
                    if (targetMapCoords.TryGetValue(adjustedCoord, out var dbGridId))
                    {
                        coordOverlapCount++;
                        if (grid.GridIdString == dbGridId)
                            contentMatchCount++;
                    }
                }
            }

            double coordOverlapPct = so.Grids.Count > 0 ? (double)coordOverlapCount / so.Grids.Count * 100 : 0;
            double contentMatchRate = coordOverlapCount > 0 ? (double)contentMatchCount / coordOverlapCount * 100 : 0;

            if (coordOverlapPct >= CAVE_OVERLAP_THRESHOLD && contentMatchRate < CAVE_CONTENT_THRESHOLD)
            {
                decisions[segId] = new SegmentMergeDecision(segId, false, null, 0, 0,
                    SegmentFallbackReason.CaveDetected,
                    $"cave detected: {coordOverlapPct:F0}% overlap, {contentMatchRate:F0}% content match");
                logger?.LogInformation("Segment {SegId:X}: NEW MAP (cave) - {Overlap:F0}% coord overlap, {Content:F0}% content match",
                    segId, coordOverlapPct, contentMatchRate);
                continue;
            }

            // PRE-PLANT CONFLICT SCAN
            var segConflicts = FindConflicts(so.Grids, so.TargetMapId, so.OffsetX, so.OffsetY);
            if (segConflicts.Count > 0)
            {
                decisions[segId] = new SegmentMergeDecision(segId, false, null, 0, 0,
                    SegmentFallbackReason.CoordConflicts, ConflictReason(segConflicts, so.TargetMapId));
                logger?.LogWarning(
                    "Segment {SegId:X}: NEW MAP (coord conflicts) - {Count} destination cells already owned on map {MapId}",
                    segId, segConflicts.Count, so.TargetMapId);
                continue;
            }

            // Passed validation - will merge
            decisions[segId] = new SegmentMergeDecision(segId, true, so.TargetMapId, so.OffsetX, so.OffsetY,
                SegmentFallbackReason.None,
                $"{so.MatchCount} Grid ID matches, {proximateMatches} proximate");
            RegisterAccepted(so.Grids, so.TargetMapId, so.OffsetX, so.OffsetY);
            logger?.LogInformation("Segment {SegId:X}: MERGE - {MatchCount} Grid ID matches, {Proximate} proximate",
                segId, so.MatchCount, proximateMatches);
        }

        return decisions;
    }
}
