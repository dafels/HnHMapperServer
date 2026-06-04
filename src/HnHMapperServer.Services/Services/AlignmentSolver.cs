using HnHMapperServer.Services.Alignment;
using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Order-independent "pose-graph" aligner for public-map sources. See <see cref="IAlignmentSolver"/>.
///
/// Pipeline: inverted grid-id index → robust per-pair offsets (mode vote + consensus gate) →
/// connected components (union-find) → per-component offsets via maximum-spanning-tree propagation →
/// cycle-consistency check → min-normalize → deterministic shelf bin-pack of disjoint landmasses.
///
/// Determinism: every aggregation is commutative and every forced choice (pair orientation, edge
/// processing order, MST tie-break, anchor, layout order, packing) is broken by <c>SourceKey</c> or
/// by content-derived edge weight — never by list position, row id, or priority.
/// </summary>
public sealed class AlignmentSolver : IAlignmentSolver
{
    public AlignmentResult Align(IReadOnlyList<SourceGridSet> sources, AlignmentOptions? options = null)
    {
        options ??= new AlignmentOptions();

        var emptyWarnings = Array.Empty<AlignmentWarning>();
        if (sources.Count == 0)
        {
            return new AlignmentResult(
                new Dictionary<string, (int X, int Y)>(),
                Array.Empty<AlignmentCluster>(),
                Array.Empty<AlignmentEdge>(),
                emptyWarnings);
        }

        // Stable index list sorted by SourceKey so nothing downstream depends on input order.
        var ordered = sources
            .Select((s, i) => (src: s, origIndex: i))
            .OrderBy(t => t.src.SourceKey, StringComparer.Ordinal)
            .ToList();
        var keys = ordered.Select(t => t.src.SourceKey).ToArray();
        var grids = ordered.Select(t => t.src.Grids).ToArray();
        var n = ordered.Count;

        // ---- Step 1: inverted index gridId -> source indices ----
        var gridToSources = new Dictionary<string, List<int>>();
        for (int i = 0; i < n; i++)
        {
            foreach (var gridId in grids[i].Keys)
            {
                if (!gridToSources.TryGetValue(gridId, out var list))
                {
                    list = new List<int>();
                    gridToSources[gridId] = list;
                }
                list.Add(i);
            }
        }

        // ---- Step 2: accumulate per-pair delta samples (canonically oriented a < b) ----
        // pairKey (a,b) with a<b by index (indices already follow SourceKey order) -> delta counts.
        var pairSamples = new Dictionary<(int A, int B), Dictionary<(int Dx, int Dy), int>>();
        foreach (var (gridId, srcList) in gridToSources)
        {
            if (srcList.Count < 2) continue;
            srcList.Sort(); // ascending index == ascending SourceKey
            for (int x = 0; x < srcList.Count; x++)
            {
                for (int y = x + 1; y < srcList.Count; y++)
                {
                    int a = srcList[x], b = srcList[y];
                    var ca = grids[a][gridId];
                    var cb = grids[b][gridId];
                    var delta = (Dx: ca.X - cb.X, Dy: ca.Y - cb.Y);
                    if (!pairSamples.TryGetValue((a, b), out var counts))
                    {
                        counts = new Dictionary<(int, int), int>();
                        pairSamples[(a, b)] = counts;
                    }
                    counts.TryGetValue(delta, out var c);
                    counts[delta] = c + 1;
                }
            }
        }

        // ---- Step 3: resolve each pair into an edge (mode vote + consensus gate) ----
        var edges = new List<AlignmentEdge>();
        var acceptedEdges = new List<Edge>();
        foreach (var ((a, b), counts) in pairSamples)
        {
            int total = counts.Values.Sum();
            // Mode group: highest count, tie-break by (dx,dy) ascending for determinism.
            var mode = counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key.Item1)
                .ThenBy(kv => kv.Key.Item2)
                .First();
            int modeSupport = mode.Value;
            var (dx, dy) = mode.Key;

            string? reject = null;
            if (total < options.MinEdgeMatches)
                reject = "insufficient_matches";
            else if ((double)modeSupport / total < options.ConsensusRatio)
                reject = "contradiction";

            bool accepted = reject == null;
            edges.Add(new AlignmentEdge(keys[a], keys[b], dx, dy, total, modeSupport, accepted, reject));
            if (accepted)
                acceptedEdges.Add(new Edge(a, b, dx, dy, modeSupport));
        }

        // Deterministic edge processing order: strongest first, then by canonical key pair.
        acceptedEdges.Sort((e1, e2) =>
        {
            int w = e2.Weight.CompareTo(e1.Weight);
            if (w != 0) return w;
            int ka = string.CompareOrdinal(keys[e1.A], keys[e2.A]);
            if (ka != 0) return ka;
            return string.CompareOrdinal(keys[e1.B], keys[e2.B]);
        });

        // ---- Step 4: connected components (union-find over accepted edges) ----
        var uf = new UnionFind(n);
        foreach (var e in acceptedEdges)
            uf.Union(e.A, e.B);

        var componentsByRoot = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = uf.Find(i);
            if (!componentsByRoot.TryGetValue(r, out var members))
            {
                members = new List<int>();
                componentsByRoot[r] = members;
            }
            members.Add(i);
        }

        // adjacency among accepted edges, per source
        var adjacency = new Dictionary<int, List<Edge>>();
        foreach (var e in acceptedEdges)
        {
            (adjacency.TryGetValue(e.A, out var la) ? la : adjacency[e.A] = new List<Edge>()).Add(e);
            (adjacency.TryGetValue(e.B, out var lb) ? lb : adjacency[e.B] = new List<Edge>()).Add(e);
        }

        // ---- Step 5+6: per-component global offsets via MST + BFS, cycle check, min-normalize ----
        var localOffset = new (int X, int Y)[n];     // component-local offset to ADD to a source's coords
        var warnings = new List<AlignmentWarning>();

        // Build per-component computed data, keyed by a deterministic component order later.
        var componentData = new List<ComponentBuild>();

        foreach (var members in componentsByRoot.Values)
        {
            members.Sort(); // ascending SourceKey
            var memberSet = new HashSet<int>(members);

            if (members.Count == 1)
            {
                int s = members[0];
                NormalizeAndRecord(new[] { s }, localOffset, grids, out int w, out int h, out int gridCount);
                componentData.Add(new ComponentBuild(members, w, h, gridCount, Confidence: 1.0, MaxResidual: 0, IsStandalone: true));
                continue;
            }

            // MST (maximum spanning tree) via Kruskal over this component's accepted edges.
            var compEdges = acceptedEdges.Where(e => memberSet.Contains(e.A)).ToList(); // A in set ⇒ both in set
            // already weight-desc sorted (acceptedEdges sorted); Kruskal:
            var mstUf = new UnionFind(n);
            var treeEdges = new List<Edge>();
            foreach (var e in compEdges)
            {
                if (mstUf.Find(e.A) != mstUf.Find(e.B))
                {
                    mstUf.Union(e.A, e.B);
                    treeEdges.Add(e);
                }
            }

            // Tree adjacency
            var treeAdj = new Dictionary<int, List<Edge>>();
            foreach (var e in treeEdges)
            {
                (treeAdj.TryGetValue(e.A, out var la) ? la : treeAdj[e.A] = new List<Edge>()).Add(e);
                (treeAdj.TryGetValue(e.B, out var lb) ? lb : treeAdj[e.B] = new List<Edge>()).Add(e);
            }

            // Anchor = smallest SourceKey (members[0]); BFS accumulating offsets.
            int anchor = members[0];
            var global = new Dictionary<int, (int X, int Y)>();
            global[anchor] = (0, 0);
            var queue = new Queue<int>();
            queue.Enqueue(anchor);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (!treeAdj.TryGetValue(cur, out var nbrs)) continue;
                // visit in deterministic order
                foreach (var e in nbrs.OrderBy(e => string.CompareOrdinal(keys[e.A], keys[e.B])))
                {
                    int other = e.A == cur ? e.B : e.A;
                    if (global.ContainsKey(other)) continue;
                    // edge stored as coordA - coordB = (Dx,Dy); globalOffset[B] = globalOffset[A] + (Dx,Dy)
                    if (cur == e.A)
                        global[other] = (global[cur].X + e.Dx, global[cur].Y + e.Dy);   // other == B
                    else
                        global[other] = (global[cur].X - e.Dx, global[cur].Y - e.Dy);   // other == A
                    queue.Enqueue(other);
                }
            }

            // Cycle-consistency over accepted non-tree edges.
            var treeSet = new HashSet<(int, int)>(treeEdges.Select(e => (e.A, e.B)));
            int nonTree = 0, inconsistent = 0, maxResidual = 0;
            foreach (var e in compEdges)
            {
                if (treeSet.Contains((e.A, e.B))) continue;
                nonTree++;
                // expected: global[B] - global[A] == (Dx,Dy)
                int rx = (global[e.B].X - global[e.A].X) - e.Dx;
                int ry = (global[e.B].Y - global[e.A].Y) - e.Dy;
                int res = Math.Abs(rx) + Math.Abs(ry);
                if (res != 0)
                {
                    inconsistent++;
                    maxResidual = Math.Max(maxResidual, res);
                    warnings.Add(new AlignmentWarning(
                        "cycle_inconsistency",
                        $"Sources {keys[e.A]} and {keys[e.B]} share grids but their alignment disagrees with the rest of the landmass by {res} grid(s).",
                        keys[e.A], keys[e.B], res));
                }
            }
            double confidence = nonTree == 0 ? 1.0 : 1.0 - (double)inconsistent / nonTree;

            foreach (var (s, off) in global)
                localOffset[s] = off;

            NormalizeAndRecord(members, localOffset, grids, out int cw, out int ch, out int cgrids);
            componentData.Add(new ComponentBuild(members, cw, ch, cgrids, confidence, maxResidual, IsStandalone: false));
        }

        // Surface rejected "contradiction" edges as warnings too.
        foreach (var e in edges.Where(e => e.RejectReason == "contradiction"))
        {
            warnings.Add(new AlignmentWarning(
                "contradiction",
                $"Sources {e.SourceA} and {e.SourceB} share {e.TotalMatches} grid(s) but no single offset is agreed by ≥{(int)(options.ConsensusRatio * 100)}% — likely a content-hash collision or a cave/overlay, not a real overlap. Treated as not overlapping.",
                e.SourceA, e.SourceB));
        }

        // ---- Step 7: deterministic shelf bin-pack of components ----
        // Order: biggest landmass first (grid count, then area), then smallest SourceKey.
        componentData.Sort((c1, c2) =>
        {
            int g = c2.GridCount.CompareTo(c1.GridCount);
            if (g != 0) return g;
            long a1 = (long)c1.Width * c1.Height, a2 = (long)c2.Width * c2.Height;
            int ar = a2.CompareTo(a1);
            if (ar != 0) return ar;
            return string.CompareOrdinal(keys[c1.Members[0]], keys[c2.Members[0]]);
        });

        int gutter = SnapUp(Math.Max(0, options.StandaloneGutterGrids), 4);
        long totalArea = componentData.Sum(c => (long)c.Width * c.Height);
        int maxBlobW = componentData.Count == 0 ? 0 : componentData.Max(c => c.Width);
        int targetWidth = Math.Max(maxBlobW, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, totalArea)) * 1.5));

        var offsets = new Dictionary<string, (int X, int Y)>();
        var clusters = new List<AlignmentCluster>();

        int cursorX = 0, shelfBaseY = 0, shelfMaxH = 0, clusterIndex = 0;
        foreach (var c in componentData)
        {
            if (cursorX > 0 && cursorX + c.Width > targetWidth)
            {
                shelfBaseY = SnapUp(shelfBaseY + shelfMaxH + gutter, 4);
                cursorX = 0;
                shelfMaxH = 0;
            }
            int ox = SnapUp(cursorX, 4);
            int oy = SnapUp(shelfBaseY, 4);

            foreach (var s in c.Members)
                offsets[keys[s]] = (localOffset[s].X + ox, localOffset[s].Y + oy);

            clusters.Add(new AlignmentCluster(
                clusterIndex++,
                c.Members.Select(s => keys[s]).ToList(),
                c.GridCount, c.Width, c.Height, ox, oy,
                c.Confidence, c.MaxResidual, c.IsStandalone));

            cursorX = ox + c.Width + gutter;
            shelfMaxH = Math.Max(shelfMaxH, (oy - shelfBaseY) + c.Height);
        }

        return new AlignmentResult(offsets, clusters, edges, warnings);
    }

    /// <summary>
    /// Translate a component's member offsets so its min grid sits at local (0,0); also returns the
    /// component's grid-unit width/height and total grid count. Cancels the arbitrary anchor choice.
    /// </summary>
    private static void NormalizeAndRecord(
        IReadOnlyList<int> members,
        (int X, int Y)[] localOffset,
        IReadOnlyDictionary<string, (int X, int Y)>[] grids,
        out int width, out int height, out int gridCount)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, count = 0;
        foreach (var s in members)
        {
            var off = localOffset[s];
            foreach (var (gx, gy) in grids[s].Values)
            {
                int ux = gx + off.X, uy = gy + off.Y;
                if (ux < minX) minX = ux;
                if (uy < minY) minY = uy;
                if (ux > maxX) maxX = ux;
                if (uy > maxY) maxY = uy;
                count++;
            }
        }
        if (count == 0)
        {
            width = 0; height = 0; gridCount = 0;
            return;
        }
        foreach (var s in members)
            localOffset[s] = (localOffset[s].X - minX, localOffset[s].Y - minY);

        width = maxX - minX + 1;
        height = maxY - minY + 1;
        gridCount = count;
    }

    private static int SnapUp(int value, int multiple)
    {
        if (multiple <= 1) return value;
        int rem = ((value % multiple) + multiple) % multiple;
        return rem == 0 ? value : value + (multiple - rem);
    }

    private readonly record struct Edge(int A, int B, int Dx, int Dy, int Weight);

    private sealed record ComponentBuild(
        IReadOnlyList<int> Members, int Width, int Height, int GridCount,
        double Confidence, int MaxResidual, bool IsStandalone);

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public UnionFind(int n)
        {
            _parent = new int[n];
            _rank = new int[n];
            for (int i = 0; i < n; i++) _parent[i] = i;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            if (_rank[ra] == _rank[rb]) _rank[ra]++;
        }
    }
}
