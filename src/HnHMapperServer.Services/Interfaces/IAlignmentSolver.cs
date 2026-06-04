using HnHMapperServer.Services.Alignment;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Pure, deterministic, order-independent aligner for public-map sources.
/// <para>
/// Given N sources (each a set of <c>gridId -&gt; localCoord</c>), it discovers how they overlap
/// via shared content-hash grid ids, weaves overlapping sources into rigid landmasses, and lays
/// out disjoint landmasses side-by-side without collision. The result is a pure function of the
/// sources' grid CONTENT: permuting the input list or changing row ids / priorities never changes
/// the output.
/// </para>
/// </summary>
public interface IAlignmentSolver
{
    AlignmentResult Align(IReadOnlyList<SourceGridSet> sources, AlignmentOptions? options = null);
}
