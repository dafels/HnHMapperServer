using HnHMapperServer.Services.Alignment;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// Unit tests for turning a parsed .hmap into an aligner <see cref="SourceGridSet"/> — the bridge
/// that lets hmap sources merge with tenant sources through the same order-independent solver.
/// </summary>
public class HmapSourceGridSetTests
{
    private static HmapData Hmap(params (long gridId, int x, int y)[] grids)
    {
        var data = new HmapData();
        foreach (var (gridId, x, y) in grids)
            data.Grids.Add(new HmapGridData { GridId = gridId, TileX = x, TileY = y });
        return data;
    }

    [Fact]
    public void Build_MapsGridIdStringToTileCoords_WithHmapKey()
    {
        var data = Hmap((1001, 0, 0), (1002, 1, 0), (1003, 2, 0));

        var set = PublicMapSourceLoader.BuildHmapSourceGridSet(data, hmapSourceId: 7);

        Assert.Equal("hmap:7", set.SourceKey);
        Assert.Equal((0, 0), set.Grids["1001"]);
        Assert.Equal((1, 0), set.Grids["1002"]);
        Assert.Equal((2, 0), set.Grids["1003"]);
    }

    [Fact]
    public void Build_ZeroGridId_SynthesizesUniquePerSourceId_NoCollision()
    {
        // Two grids with the sentinel id 0 must NOT collapse onto one key.
        var data = Hmap((0, 5, 5), (0, 6, 5), (2002, 7, 5));

        var set = PublicMapSourceLoader.BuildHmapSourceGridSet(data, hmapSourceId: 3);

        Assert.Equal(3, set.Grids.Count);
        Assert.Equal((5, 5), set.Grids["_raw:3:5:5"]);
        Assert.Equal((6, 5), set.Grids["_raw:3:6:5"]);
        Assert.Equal((7, 5), set.Grids["2002"]);
        // Synthetic ids are namespaced to the source, so a different source's sentinel never matches.
        var other = PublicMapSourceLoader.BuildHmapSourceGridSet(Hmap((0, 5, 5)), hmapSourceId: 9);
        Assert.DoesNotContain("_raw:3:5:5", other.Grids.Keys);
    }

    [Fact]
    public void Build_TwoHmaps_SharingGridIds_AlignViaTheSolver()
    {
        // Same physical cells (shared game grid ids) exported by two hmaps at different origins.
        var a = PublicMapSourceLoader.BuildHmapSourceGridSet(
            Hmap((10, 0, 0), (11, 1, 0), (12, 2, 0), (13, 3, 0), (14, 4, 0), (15, 5, 0)), 1);
        var b = PublicMapSourceLoader.BuildHmapSourceGridSet(
            Hmap((10, 100, 50), (11, 101, 50), (12, 102, 50), (13, 103, 50), (14, 104, 50), (15, 105, 50)), 2);

        var result = new AlignmentSolver().Align(new[] { a, b });

        Assert.Single(result.Clusters);
        Assert.Equal(2, result.Clusters[0].SourceKeys.Count);
        // Shared grid "10" lands at one unified coord in both sources.
        var offA = result.Offsets["hmap:1"];
        var offB = result.Offsets["hmap:2"];
        Assert.Equal((0 + offA.X, 0 + offA.Y), (100 + offB.X, 50 + offB.Y));
    }
}
