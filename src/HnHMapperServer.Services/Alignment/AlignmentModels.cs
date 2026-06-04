namespace HnHMapperServer.Services.Alignment;

/// <summary>
/// One merge source reduced to just what the aligner needs: a stable content-derived key and
/// the source's grids as <c>gridId -&gt; (x,y)</c> in the source's own local coordinate space.
/// <para>
/// <see cref="SourceKey"/> MUST be a pure function of which map this is (e.g. "tenantId:mapId"),
/// never of a database row id, insertion time, or admin priority — every deterministic tie-break
/// in the solver keys on it.
/// </para>
/// </summary>
public sealed record SourceGridSet(string SourceKey, IReadOnlyDictionary<string, (int X, int Y)> Grids);

/// <summary>
/// Tunables for the alignment solver. Defaults mirror the existing hmap-import heuristics
/// (<c>MIN_PROXIMATE_MATCHES = 5</c>).
/// </summary>
public sealed record AlignmentOptions
{
    /// <summary>Minimum shared grid ids before a pair is allowed to form an alignment edge.
    /// A single coincidental shared grid can never align two sources.</summary>
    public int MinEdgeMatches { get; init; } = 5;

    /// <summary>Fraction of shared-grid deltas that must agree on the dominant offset for the
    /// edge to be trusted. Below this the distribution is multi-modal — a content-hash collision
    /// or overlapping cave/overlay — and the edge is rejected as a contradiction.</summary>
    public double ConsensusRatio { get; init; } = 0.6;

    /// <summary>Clear space (in base grids) kept between disjoint landmasses. Must be a multiple
    /// of 4 (the output-tile size in grids) so no 400×400 output tile ever spans two landmasses.</summary>
    public int StandaloneGutterGrids { get; init; } = 16;
}

/// <summary>
/// A candidate or accepted alignment relationship between two sources, derived purely from their
/// shared grid ids. Oriented canonically: <see cref="SourceA"/> is the lexicographically smaller
/// <c>SourceKey</c>, so <see cref="OffsetX"/>/<see cref="OffsetY"/> (= coordA − coordB on shared
/// grids) has a stable sign independent of input order.
/// </summary>
public sealed record AlignmentEdge(
    string SourceA,
    string SourceB,
    int OffsetX,
    int OffsetY,
    int TotalMatches,
    int ModeSupport,
    bool Accepted,
    string? RejectReason)
{
    /// <summary>Agreement ratio of the dominant offset (modeSupport / totalMatches).</summary>
    public double Consensus => TotalMatches == 0 ? 0 : (double)ModeSupport / TotalMatches;
}

/// <summary>
/// A connected set of sources that align into one rigid landmass (or a single standalone source).
/// Origins are in unified grid coordinates after layout.
/// </summary>
public sealed record AlignmentCluster(
    int Index,
    IReadOnlyList<string> SourceKeys,
    int GridCount,
    int LocalWidth,
    int LocalHeight,
    int PlacedOriginX,
    int PlacedOriginY,
    double Confidence,
    int MaxResidual,
    bool IsStandalone);

/// <summary>A reportable issue surfaced to the (blind) admin during analysis.</summary>
public sealed record AlignmentWarning(
    string Type,
    string Message,
    string? SourceA = null,
    string? SourceB = null,
    int Residual = 0);

/// <summary>
/// Output of <see cref="IAlignmentSolver.Align"/>. <see cref="Offsets"/> maps each source's
/// <c>SourceKey</c> to the (x,y) that must be ADDED to that source's local grid coords to land in
/// the unified public space — the same "add to source coord" convention the generation merge uses.
/// </summary>
public sealed record AlignmentResult(
    IReadOnlyDictionary<string, (int X, int Y)> Offsets,
    IReadOnlyList<AlignmentCluster> Clusters,
    IReadOnlyList<AlignmentEdge> Edges,
    IReadOnlyList<AlignmentWarning> Warnings);
