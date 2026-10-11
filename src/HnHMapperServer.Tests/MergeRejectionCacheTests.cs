using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// The merge preflight's verdict memory. A double-claim rejection changes nothing in the
/// database, so without it every gridUpdate of every client in the area re-ran two map loads and
/// logged the full matrix again.
/// </summary>
public class MergeRejectionCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Key_IsIndependentOfSourceOrder_AndSeparatesShifts()
    {
        var a = MergeRejectionCache.KeyOf("t", 1, new[] { (3, 0, 1), (2, 5, -5) });
        var b = MergeRejectionCache.KeyOf("t", 1, new[] { (2, 5, -5), (3, 0, 1) });
        var otherShift = MergeRejectionCache.KeyOf("t", 1, new[] { (2, 5, -4), (3, 0, 1) });
        var otherTenant = MergeRejectionCache.KeyOf("u", 1, new[] { (2, 5, -5), (3, 0, 1) });

        Assert.Equal(a, b);
        Assert.NotEqual(a, otherShift);
        Assert.NotEqual(a, otherTenant);
    }

    [Fact]
    public void RememberedRejection_HoldsForTheTtl_CountsRepeats_ThenExpires()
    {
        var cache = new MergeRejectionCache();
        const string key = "t|1|2:0,0";

        Assert.False(cache.IsRejected(key, Now));
        Assert.Equal(0, cache.Remember(key, Now));

        Assert.True(cache.IsRejected(key, Now.AddMinutes(1)));
        Assert.True(cache.IsRejected(key, Now.AddMinutes(2)));
        Assert.False(cache.IsRejected(key, Now + MergeRejectionCache.Ttl));

        // The next warning says how many identical rejections the expired verdict stood for.
        Assert.Equal(2, cache.Remember(key, Now + MergeRejectionCache.Ttl));
        Assert.True(cache.IsRejected(key, Now + MergeRejectionCache.Ttl + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Forget_DropsTheVerdict()
    {
        var cache = new MergeRejectionCache();
        const string key = "t|1|2:0,0";
        cache.Remember(key, Now);

        cache.Forget(key);

        Assert.False(cache.IsRejected(key, Now));
        Assert.Equal(0, cache.Remember(key, Now));
    }
}
