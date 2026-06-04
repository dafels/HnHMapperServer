using HnHMapperServer.Services.Alignment;

namespace HnHMapperServer.Tests;

/// <summary>
/// The cross-type conflict policy: tenant beats hmap (hmaps fill gaps), then freshest, then
/// source key, then grid id — deterministic regardless of which order two candidates are compared.
/// </summary>
public class CellConflictTests
{
    // (isTenant, freshness, key, gridId)
    private static bool Wins((bool t, long f, string k, string? g) a, (bool t, long f, string k, string? g) b)
        => CellConflict.Wins(a.t, a.f, a.k, a.g, b.t, b.f, b.k, b.g);

    [Fact]
    public void Tenant_Beats_Hmap_RegardlessOfFreshness()
    {
        var tenant = (t: true, f: 1L, k: "x:1", g: (string?)"g1");      // older tenant
        var hmap = (t: false, f: 9_999L, k: "hmap:1", g: (string?)"g2"); // much newer hmap

        Assert.True(Wins(tenant, hmap));   // tenant replaces hmap
        Assert.False(Wins(hmap, tenant));  // hmap never replaces tenant
    }

    [Fact]
    public void SameType_FreshestWins_ThenKey_ThenGrid()
    {
        var newer = (t: true, f: 200L, k: "a:1", g: (string?)"g");
        var older = (t: true, f: 100L, k: "a:1", g: (string?)"g");
        Assert.True(Wins(newer, older));
        Assert.False(Wins(older, newer));

        // Freshness tie -> smaller source key wins.
        var keyA = (t: false, f: 5L, k: "hmap:1", g: (string?)"g");
        var keyB = (t: false, f: 5L, k: "hmap:2", g: (string?)"g");
        Assert.True(Wins(keyA, keyB));
        Assert.False(Wins(keyB, keyA));

        // Freshness + key tie -> smaller grid id wins.
        var gridA = (t: true, f: 5L, k: "a:1", g: (string?)"g1");
        var gridB = (t: true, f: 5L, k: "a:1", g: (string?)"g2");
        Assert.True(Wins(gridA, gridB));
        Assert.False(Wins(gridB, gridA));
    }

    [Fact]
    public void IdenticalCandidate_DoesNotReplace_NoChurn()
    {
        var c = (t: true, f: 5L, k: "a:1", g: (string?)"g1");
        Assert.False(Wins(c, c));
    }

    [Fact]
    public void IsAntisymmetric_ForDistinctCandidates()
    {
        var samples = new (bool t, long f, string k, string? g)[]
        {
            (true, 1, "a:1", "g1"),
            (true, 2, "a:1", "g1"),
            (false, 1, "hmap:1", "g1"),
            (false, 9, "hmap:2", null),
            (true, 1, "b:1", "g2"),
        };
        for (int i = 0; i < samples.Length; i++)
            for (int j = i + 1; j < samples.Length; j++)
                Assert.True(Wins(samples[i], samples[j]) != Wins(samples[j], samples[i]),
                    $"exactly one of {i},{j} must win");
    }
}
