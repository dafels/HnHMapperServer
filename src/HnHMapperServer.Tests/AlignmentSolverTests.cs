using HnHMapperServer.Services.Alignment;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// Unit tests for the order-independent public-map source aligner. No DB/IO — pure algorithm.
/// The headline guarantee under test: the result is a pure function of the sources' grid CONTENT,
/// independent of input list order (and therefore of any row id / priority a caller might have).
/// </summary>
public class AlignmentSolverTests
{
    private readonly AlignmentSolver _solver = new();

    // ---- helpers ----

    private static SourceGridSet Src(string key, params (string id, int x, int y)[] gridList)
    {
        var grids = gridList.ToDictionary(g => g.id, g => (g.x, g.y));
        return new SourceGridSet(key, grids);
    }

    /// <summary>A horizontal strip of `count` grids with ids prefix+i at (originX+i, originY).</summary>
    private static (string id, int x, int y)[] Strip(string prefix, int count, int originX, int originY)
        => Enumerable.Range(0, count)
            .Select(i => ($"{prefix}{i}", originX + i, originY))
            .ToArray();

    /// <summary>Unified coord of a grid as placed by the solver: localCoord + source offset.</summary>
    private static (int X, int Y) Unified(AlignmentResult r, SourceGridSet s, string gridId)
    {
        var off = r.Offsets[s.SourceKey];
        var c = s.Grids[gridId];
        return (c.X + off.X, c.Y + off.Y);
    }

    private static IReadOnlyList<List<SourceGridSet>> Permutations(IReadOnlyList<SourceGridSet> items)
    {
        var results = new List<List<SourceGridSet>>();
        void Recurse(List<SourceGridSet> current, List<SourceGridSet> remaining)
        {
            if (remaining.Count == 0) { results.Add(new List<SourceGridSet>(current)); return; }
            for (int i = 0; i < remaining.Count; i++)
            {
                current.Add(remaining[i]);
                var rest = new List<SourceGridSet>(remaining);
                rest.RemoveAt(i);
                Recurse(current, rest);
                current.RemoveAt(current.Count - 1);
            }
        }
        Recurse(new List<SourceGridSet>(), items.ToList());
        return results;
    }

    // ---- 1. order-independence headline ----

    [Fact]
    public void Align_IsOrderIndependent_AcrossAllPermutations()
    {
        // Two landmasses (A-B, C-D) plus one standalone (E).
        var a = Src("t1:1", Strip("s", 6, 0, 0));
        var b = Src("t2:1", Strip("s", 6, 100, 50).Concat(new[] { ("bx", 106, 50) }).ToArray());
        var c = Src("t3:1", Strip("t", 6, 0, 0));
        var d = Src("t4:1", Strip("t", 6, 200, 200));
        var e = Src("t5:1", Strip("e", 6, 0, 0)); // unique ids -> standalone

        var baseline = _solver.Align(new[] { a, b, c, d, e });

        foreach (var perm in Permutations(new[] { a, b, c, d, e }))
        {
            var result = _solver.Align(perm);

            // Offsets identical for every source key.
            Assert.Equal(baseline.Offsets.Count, result.Offsets.Count);
            foreach (var (key, off) in baseline.Offsets)
                Assert.Equal(off, result.Offsets[key]);

            // Cluster placement identical (same order, origins, membership).
            Assert.Equal(baseline.Clusters.Count, result.Clusters.Count);
            for (int i = 0; i < baseline.Clusters.Count; i++)
            {
                Assert.Equal(baseline.Clusters[i].SourceKeys.OrderBy(x => x),
                             result.Clusters[i].SourceKeys.OrderBy(x => x));
                Assert.Equal((baseline.Clusters[i].PlacedOriginX, baseline.Clusters[i].PlacedOriginY),
                             (result.Clusters[i].PlacedOriginX, result.Clusters[i].PlacedOriginY));
            }
        }
    }

    [Fact]
    public void Align_OverlappingSources_LandOnSameUnifiedCoord()
    {
        var a = Src("t1:1", Strip("s", 6, 0, 0));
        var b = Src("t2:1", Strip("s", 6, 100, 50));

        var r = _solver.Align(new[] { a, b });

        Assert.Single(r.Clusters);
        Assert.Equal(2, r.Clusters[0].SourceKeys.Count);
        // Every shared grid resolves to the same unified coordinate in both sources.
        foreach (var g in a.Grids.Keys)
            Assert.Equal(Unified(r, a, g), Unified(r, b, g));
    }

    // ---- 2. transitive bridging ----

    [Fact]
    public void Align_BridgesTransitively_WhenAandCShareNothingButBothShareB()
    {
        // A-B share "p*", B-C share "q*", A and C share nothing.
        var a = Src("t1:1", Strip("p", 6, 0, 0));
        var b = Src("t2:1", Strip("p", 6, 10, 0).Concat(Strip("q", 6, 16, 0)).ToArray());
        var c = Src("t3:1", Strip("q", 6, 100, 0));

        var r = _solver.Align(new[] { a, b, c });

        Assert.Single(r.Clusters);
        Assert.Equal(3, r.Clusters[0].SourceKeys.Count);
        // C is aligned via B: a shared "q" grid lands at the same unified coord in B and C.
        Assert.Equal(Unified(r, b, "q0"), Unified(r, c, "q0"));
        // And A connects through B too.
        Assert.Equal(Unified(r, a, "p0"), Unified(r, b, "p0"));
    }

    // ---- 3. robust offset + single-grid rejection ----

    [Fact]
    public void Align_RobustOffset_OutvotesASingleOutlier()
    {
        var a = Src("t1:1", Strip("g", 6, 0, 0).Concat(new[] { ("gOut", 0, 0) }).ToArray());
        var b = Src("t2:1", Strip("g", 6, 50, 0).Concat(new[] { ("gOut", 999, 999) }).ToArray());

        var r = _solver.Align(new[] { a, b });

        var edge = Assert.Single(r.Edges);
        Assert.True(edge.Accepted);
        Assert.Equal(7, edge.TotalMatches);
        Assert.Equal(6, edge.ModeSupport);
        Assert.Equal((-50, 0), (edge.OffsetX, edge.OffsetY)); // coordA - coordB on the dominant 6
        // The 6 consistent grids align; the outlier does not break placement.
        Assert.Equal(Unified(r, a, "g0"), Unified(r, b, "g0"));
    }

    [Fact]
    public void Align_SingleSharedGrid_DoesNotFormAnEdge()
    {
        var a = Src("t1:1", Strip("a", 6, 0, 0).Concat(new[] { ("shared", 3, 3) }).ToArray());
        var c = Src("t2:1", Strip("c", 6, 0, 0).Concat(new[] { ("shared", 9, 9) }).ToArray());

        var r = _solver.Align(new[] { a, c });

        var edge = Assert.Single(r.Edges);
        Assert.False(edge.Accepted);
        Assert.Equal("insufficient_matches", edge.RejectReason);
        Assert.Equal(2, r.Clusters.Count); // each its own standalone landmass
    }

    // ---- 4. multi-modal rejection / split ----

    [Fact]
    public void Align_MultiModalDeltas_RejectedAsContradiction_AndSplit()
    {
        // 3 grids agree on delta (10,0), 3 agree on (20,0): consensus 0.5 < 0.6 -> reject.
        var a = Src("t1:1",
            ("m0", 0, 0), ("m1", 1, 0), ("m2", 2, 0),
            ("n0", 0, 0), ("n1", 1, 0), ("n2", 2, 0));
        var b = Src("t2:1",
            ("m0", -10, 0), ("m1", -9, 0), ("m2", -8, 0),   // delta (10,0)
            ("n0", -20, 0), ("n1", -19, 0), ("n2", -18, 0)); // delta (20,0)

        var r = _solver.Align(new[] { a, b });

        var edge = Assert.Single(r.Edges);
        Assert.False(edge.Accepted);
        Assert.Equal("contradiction", edge.RejectReason);
        Assert.Equal(2, r.Clusters.Count);
        Assert.Contains(r.Warnings, w => w.Type == "contradiction");
    }

    // ---- 5. standalone layout ----

    [Fact]
    public void Align_DisjointClusters_DoNotOverlap_AndOriginsAre4Aligned()
    {
        var a = Src("t1:1", Strip("a", 8, 0, 0));
        var b = Src("t2:1", Strip("b", 8, 0, 0));
        var c = Src("t3:1", Strip("c", 8, 0, 0));

        var r = _solver.Align(new[] { a, b, c });

        Assert.Equal(3, r.Clusters.Count);
        foreach (var cl in r.Clusters)
        {
            Assert.True(cl.IsStandalone);
            Assert.Equal(0, cl.PlacedOriginX % 4);
            Assert.Equal(0, cl.PlacedOriginY % 4);
        }

        // No two cluster bounding boxes share any grid coordinate.
        var rects = r.Clusters
            .Select(cl => (x0: cl.PlacedOriginX, y0: cl.PlacedOriginY,
                           x1: cl.PlacedOriginX + cl.LocalWidth, y1: cl.PlacedOriginY + cl.LocalHeight))
            .ToList();
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                bool overlap = rects[i].x0 < rects[j].x1 && rects[j].x0 < rects[i].x1 &&
                               rects[i].y0 < rects[j].y1 && rects[j].y0 < rects[i].y1;
                Assert.False(overlap, $"clusters {i} and {j} overlap");
            }
    }

    // ---- 6. cycle consistency ----

    [Fact]
    public void Align_ConsistentTriangle_HasFullConfidence()
    {
        // A,B,C mutually overlap with consistent offsets (all in one frame, shifted constants).
        var a = Src("t1:1", Strip("x", 8, 0, 0));
        var b = Src("t2:1", Strip("x", 8, 100, 0));   // delta vs A constant
        var c = Src("t3:1", Strip("x", 8, 50, 30));   // delta vs A and vs B both consistent

        var r = _solver.Align(new[] { a, b, c });

        Assert.Single(r.Clusters);
        Assert.Equal(1.0, r.Clusters[0].Confidence, 3);
        Assert.Equal(0, r.Clusters[0].MaxResidual);
        Assert.DoesNotContain(r.Warnings, w => w.Type == "cycle_inconsistency");
    }

    [Fact]
    public void Align_InconsistentTriangle_ReportsResidual_ButStaysDeterministic()
    {
        // A-B and A-C consistent; B-C deliberately inconsistent on its own ids.
        // Build so each pair has >=5 shared grids but the loop doesn't close.
        var a = Src("t1:1",
            Strip("ab", 6, 0, 0).Concat(Strip("ac", 6, 0, 10)).ToArray());
        var b = Src("t2:1",
            Strip("ab", 6, 100, 0).Concat(Strip("bc", 6, 100, 20)).ToArray());
        var c = Src("t3:1",
            Strip("ac", 6, 0, 510).Concat(Strip("bc", 6, 777, 999)).ToArray());

        var r1 = _solver.Align(new[] { a, b, c });
        var r2 = _solver.Align(new[] { c, b, a });

        Assert.Single(r1.Clusters);
        Assert.True(r1.Clusters[0].MaxResidual > 0);
        Assert.True(r1.Clusters[0].Confidence < 1.0);
        Assert.Contains(r1.Warnings, w => w.Type == "cycle_inconsistency");

        // Deterministic despite the contradiction.
        foreach (var (key, off) in r1.Offsets)
            Assert.Equal(off, r2.Offsets[key]);
    }

    // ---- edge: empty / single ----

    [Fact]
    public void Align_Empty_ReturnsEmpty()
    {
        var r = _solver.Align(Array.Empty<SourceGridSet>());
        Assert.Empty(r.Offsets);
        Assert.Empty(r.Clusters);
    }

    [Fact]
    public void Align_SingleSource_IsStandaloneAtOrigin()
    {
        var a = Src("t1:1", Strip("a", 5, 7, 9));
        var r = _solver.Align(new[] { a });
        Assert.Single(r.Clusters);
        Assert.True(r.Clusters[0].IsStandalone);
        // min-normalized + placed at (0,0): smallest unified coord is (0,0).
        var minX = a.Grids.Values.Min(v => v.Item1) + r.Offsets["t1:1"].X;
        var minY = a.Grids.Values.Min(v => v.Item2) + r.Offsets["t1:1"].Y;
        Assert.Equal((0, 0), (minX, minY));
    }
}
