using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// Pure unit tests for the Merge-mode .hmap decision engine. Pins the 2026-08 incident fixes:
/// per-(map, offset) group voting (a segment can no longer merge into map B using map A's frame),
/// the dominant-segment minimum-match floor, the pre-plant conflict scan, sentinel GridId==0
/// exclusion, and preservation of the legacy proximity/cave checks for secondary segments.
/// No database, no rendering — HmapGridData/GridData are built directly.
/// </summary>
public class HmapMergePlannerTests
{
    private static HmapGridData G(long id, int x, int y) => new() { GridId = id, TileX = x, TileY = y };

    private static GridData Db(long id, int map, int x, int y) => new()
    {
        Id = id.ToString(),
        Map = map,
        Coord = new Coord(x, y),
        NextUpdate = DateTime.UtcNow
    };

    private static (long SegmentId, IReadOnlyList<HmapGridData> Grids) Seg(long segId, params HmapGridData[] grids)
        => (segId, grids);

    private static Dictionary<long, SegmentMergeDecision> Compute(
        List<(long SegmentId, IReadOnlyList<HmapGridData> Grids)> segments,
        params GridData[] existing)
        => HmapMergePlanner.Compute(segments, existing);

    [Fact]
    public void Compute_NoMatches_SegmentFallsBackToCreateNew()
    {
        var decisions = Compute(
            new() { Seg(1, G(1, 0, 0), G(2, 1, 0), G(3, 2, 0)) },
            Db(100, 1, 0, 0), Db(101, 1, 1, 0));

        var d = decisions[1];
        Assert.False(d.ShouldMerge);
        Assert.Equal(SegmentFallbackReason.NoMatches, d.FallbackReason);
        Assert.Null(d.TargetMapId);
    }

    [Fact]
    public void Compute_DominantBelowMinMatches_FallsBackToCreateNew()
    {
        // 4 agreeing matches — one short of MIN_MERGE_MATCHES. Before the fix the dominant
        // segment merged unconditionally, letting a handful of stale grids misplace a segment.
        var decisions = Compute(
            new() { Seg(1, G(1, 0, 0), G(2, 1, 0), G(3, 2, 0), G(4, 3, 0), G(50, 4, 0)) },
            Db(1, 1, 10, 0), Db(2, 1, 11, 0), Db(3, 1, 12, 0), Db(4, 1, 13, 0));

        var d = decisions[1];
        Assert.False(d.ShouldMerge);
        Assert.Equal(SegmentFallbackReason.BelowMinMatches, d.FallbackReason);
        Assert.Contains($"4/{HmapMergePlanner.MIN_MERGE_MATCHES}", d.Reason);
    }

    [Fact]
    public void Compute_DominantAtMinMatches_Merges()
    {
        // Exactly MIN_MERGE_MATCHES agreeing matches — the gate boundary.
        var decisions = Compute(
            new() { Seg(1, G(1, 0, 0), G(2, 1, 0), G(3, 2, 0), G(4, 3, 0), G(5, 4, 0), G(60, 5, 0)) },
            Db(1, 1, 10, 0), Db(2, 1, 11, 0), Db(3, 1, 12, 0), Db(4, 1, 13, 0), Db(5, 1, 14, 0));

        var d = decisions[1];
        Assert.True(d.ShouldMerge);
        Assert.Equal(1, d.TargetMapId);
        Assert.Equal(10, d.OffsetX);
        Assert.Equal(0, d.OffsetY);
        Assert.Equal(SegmentFallbackReason.None, d.FallbackReason);
    }

    [Fact]
    public void Compute_VotesPerMapAndOffsetGroup_WinnerDefinesTargetAndOffsetTogether()
    {
        // D6 regression: the old code voted the offset across ALL maps but took the target map
        // from matches.First(). Map-2 matches are listed FIRST in the file order here; the
        // winning group (6 agreeing matches on map 1) must define both target AND offset.
        var segment = Seg(1,
            G(21, 50, 0), G(22, 51, 0), G(23, 52, 0),          // map-2 matches, file-order first
            G(1, 0, 0), G(2, 1, 0), G(3, 2, 0), G(4, 3, 0), G(5, 4, 0), G(6, 5, 0), // map-1 matches
            G(70, 6, 0));                                       // genuinely new grid

        var decisions = Compute(
            new() { segment },
            Db(21, 2, 150, 0), Db(22, 2, 151, 0), Db(23, 2, 152, 0),
            Db(1, 1, 10, 0), Db(2, 1, 11, 0), Db(3, 1, 12, 0), Db(4, 1, 13, 0), Db(5, 1, 14, 0), Db(6, 1, 15, 0));

        var d = decisions[1];
        Assert.True(d.ShouldMerge);
        Assert.Equal(1, d.TargetMapId);   // the winning group's map — not First()'s map 2
        Assert.Equal(10, d.OffsetX);      // the winning group's offset — not map 2's (100,0)
        Assert.Equal(0, d.OffsetY);
    }

    [Fact]
    public void Compute_SplitOffsetsWithinOneMap_MajorityGroupWins()
    {
        // Same map, two internally-consistent offset groups (a corrupted DB can produce this):
        // 4 matches imply (10,0), 6 imply (20,0) — the majority group wins.
        var grids = new List<HmapGridData>();
        var existing = new List<GridData>();
        for (int i = 0; i < 4; i++) { grids.Add(G(i + 1, i, 0)); existing.Add(Db(i + 1, 1, i + 10, 0)); }
        for (int i = 4; i < 10; i++) { grids.Add(G(i + 1, i, 0)); existing.Add(Db(i + 1, 1, i + 20, 0)); }

        var decisions = Compute(new() { (1L, grids) }, existing.ToArray());

        var d = decisions[1];
        Assert.True(d.ShouldMerge);
        Assert.Equal(1, d.TargetMapId);
        Assert.Equal(20, d.OffsetX);
    }

    [Fact]
    public void Compute_ConflictScan_DestinationCellOwnedByDifferentGrid_AbortsMergeToCreateNew()
    {
        // 5 clean matches, but the one genuinely-new grid would land on a cell owned by a
        // different grid id — the whole segment falls back to CreateNew instead of double-claiming.
        var decisions = Compute(
            new() { Seg(1, G(1, 0, 0), G(2, 1, 0), G(3, 2, 0), G(4, 3, 0), G(5, 4, 0), G(99, 5, 0)) },
            Db(1, 1, 10, 0), Db(2, 1, 11, 0), Db(3, 1, 12, 0), Db(4, 1, 13, 0), Db(5, 1, 14, 0),
            Db(777, 1, 15, 0)); // owns the destination cell of grid 99

        var d = decisions[1];
        Assert.False(d.ShouldMerge);
        Assert.Equal(SegmentFallbackReason.CoordConflicts, d.FallbackReason);
        Assert.Contains("1 destination cells", d.Reason);
        Assert.Contains("(15,0)", d.Reason);
    }

    [Fact]
    public void Compute_ConflictScan_CellOwnedBySameGridId_NotAConflict()
    {
        // Grid 6 exists in the DB at exactly its destination cell — it is skipped at insert
        // time (existing id), so it cannot double-claim and must not abort the merge.
        var decisions = Compute(
            new() { Seg(1, G(1, 0, 0), G(2, 1, 0), G(3, 2, 0), G(4, 3, 0), G(5, 4, 0), G(6, 5, 0)) },
            Db(1, 1, 10, 0), Db(2, 1, 11, 0), Db(3, 1, 12, 0), Db(4, 1, 13, 0), Db(5, 1, 14, 0),
            Db(6, 1, 15, 0));

        var d = decisions[1];
        Assert.True(d.ShouldMerge);
        Assert.Equal(SegmentFallbackReason.None, d.FallbackReason);
    }

    [Fact]
    public void Compute_ConflictScan_SecondSegmentPlantingOnFirstSegmentsCell_AbortsSecond()
    {
        // Both segments would plant a new grid at cell (16,0) on map 1. The dominant segment
        // claims it first; the second segment must fall back instead of double-claiming.
        var segA = Seg(0xA,
            G(1, 0, 0), G(2, 1, 0), G(3, 2, 0), G(4, 3, 0), G(5, 4, 0), G(6, 5, 0),
            G(100, 6, 0)); // new -> dest (16,0)
        var segB = Seg(0xB,
            G(21, 0, 1), G(22, 1, 1), G(23, 2, 1), G(24, 3, 1), G(25, 4, 1),
            G(200, 6, 0)); // new -> dest (16,0) — same cell as segment A's plant

        var decisions = Compute(
            new() { segA, segB },
            Db(1, 1, 10, 0), Db(2, 1, 11, 0), Db(3, 1, 12, 0), Db(4, 1, 13, 0), Db(5, 1, 14, 0), Db(6, 1, 15, 0),
            Db(21, 1, 10, 1), Db(22, 1, 11, 1), Db(23, 1, 12, 1), Db(24, 1, 13, 1), Db(25, 1, 14, 1));

        Assert.True(decisions[0xA].ShouldMerge);
        var b = decisions[0xB];
        Assert.False(b.ShouldMerge);
        Assert.Equal(SegmentFallbackReason.CoordConflicts, b.FallbackReason);
    }

    [Fact]
    public void Compute_SecondarySegmentNotProximate_CreateNew()
    {
        // Secondary segment's matches sit hundreds of grids from the merged area — the legacy
        // proximity check still applies.
        var segA = Seg(0xA, G(1, 0, 0), G(2, 1, 0), G(3, 2, 0), G(4, 3, 0), G(5, 4, 0));
        var segB = Seg(0xB, G(31, 0, 50), G(32, 1, 50), G(33, 2, 50), G(34, 3, 50), G(35, 4, 50));

        var decisions = Compute(
            new() { segA, segB },
            Db(1, 1, 10, 0), Db(2, 1, 11, 0), Db(3, 1, 12, 0), Db(4, 1, 13, 0), Db(5, 1, 14, 0),
            Db(31, 1, 500, 0), Db(32, 1, 501, 0), Db(33, 1, 502, 0), Db(34, 1, 503, 0), Db(35, 1, 504, 0));

        Assert.True(decisions[0xA].ShouldMerge);
        var b = decisions[0xB];
        Assert.False(b.ShouldMerge);
        Assert.Equal(SegmentFallbackReason.NotProximate, b.FallbackReason);
    }

    [Fact]
    public void Compute_SecondarySegmentCaveDetected_CreateNew()
    {
        // Secondary segment overlaps the target area heavily (100%) with almost no content
        // match (<10%) — the legacy cave detection still fires before the conflict scan.
        var segAGrids = new List<HmapGridData>();
        var segBGrids = new List<HmapGridData>();
        var existing = new List<GridData>();

        // Dominant segment A: 5 matches at (10..14, 0)
        for (int i = 0; i < 5; i++)
        {
            segAGrids.Add(G(i + 1, i, 0));
            existing.Add(Db(i + 1, 1, i + 10, 0));
        }

        // Segment B: 5 proximate matches at (10..14, 1)...
        for (int i = 0; i < 5; i++)
        {
            segBGrids.Add(G(i + 61, i, 1));
            existing.Add(Db(i + 61, 1, i + 10, 1));
        }
        // ...plus 46 new grids whose destination cells are all owned by different ids:
        // overlap = 51/51 = 100%, content match = 5/51 ≈ 9.8% < CAVE_CONTENT_THRESHOLD.
        for (int i = 0; i < 46; i++)
        {
            segBGrids.Add(G(i + 300, i, 2));
            existing.Add(Db(i + 400, 1, i + 10, 2));
        }

        var decisions = Compute(
            new() { (0xAL, segAGrids), (0xBL, segBGrids) },
            existing.ToArray());

        Assert.True(decisions[0xA].ShouldMerge);
        var b = decisions[0xB];
        Assert.False(b.ShouldMerge);
        Assert.Equal(SegmentFallbackReason.CaveDetected, b.FallbackReason);
    }

    [Fact]
    public void Compute_SentinelZeroGridId_IgnoredForMatching()
    {
        // A legacy "0" row exists in the DB positioned so that, if the sentinel were allowed to
        // match, it would join the winning offset group as the 5th vote and unlock the merge.
        // With the filter the group stays at 4 -> BelowMinMatches.
        var decisions = Compute(
            new() { Seg(1, G(0, 9, 9), G(1, 0, 0), G(2, 1, 0), G(3, 2, 0), G(4, 3, 0), G(80, 4, 0)) },
            Db(0, 1, 19, 9), // sentinel row at the offset-consistent position (would vote (10,0))
            Db(1, 1, 10, 0), Db(2, 1, 11, 0), Db(3, 1, 12, 0), Db(4, 1, 13, 0));

        var d = decisions[1];
        Assert.False(d.ShouldMerge);
        Assert.Equal(SegmentFallbackReason.BelowMinMatches, d.FallbackReason);
    }

    [Fact]
    public void Compute_ReturnsDecisionForEverySegment()
    {
        // The invariant that justified deleting the zero-validation fallback in
        // ImportSegmentAsync: every segment always gets a decision.
        var segA = Seg(0xA, G(1, 0, 0), G(2, 1, 0), G(3, 2, 0), G(4, 3, 0), G(5, 4, 0));
        var segB = Seg(0xB, G(900, 0, 0));
        var segC = Seg(0xC, G(31, 0, 50), G(32, 1, 50), G(33, 2, 50), G(34, 3, 50), G(35, 4, 50));

        var decisions = Compute(
            new() { segA, segB, segC },
            Db(1, 1, 10, 0), Db(2, 1, 11, 0), Db(3, 1, 12, 0), Db(4, 1, 13, 0), Db(5, 1, 14, 0),
            Db(31, 1, 500, 0), Db(32, 1, 501, 0), Db(33, 1, 502, 0), Db(34, 1, 503, 0), Db(35, 1, 504, 0));

        Assert.Equal(3, decisions.Count);
        Assert.True(decisions[0xA].ShouldMerge);
        Assert.Equal(SegmentFallbackReason.NoMatches, decisions[0xB].FallbackReason);
        Assert.Equal(SegmentFallbackReason.NotProximate, decisions[0xC].FallbackReason);
    }
}
