using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// NegativeTileCache: time-to-live, removal, eviction order, and eviction under concurrent
/// adds and removes. The old eviction (LINQ straight over the live ConcurrentDictionary) threw
/// ArgumentException and ArgumentNullException in production under that concurrent load.
/// </summary>
public class NegativeTileCacheTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void Contains_UntilTimeToLiveExpires()
    {
        var time = new ManualTimeProvider();
        var cache = new NegativeTileCache(10, TimeSpan.FromMinutes(5), time);

        cache.Add("a");
        Assert.True(cache.Contains("a"));

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(cache.Contains("a"));
        Assert.Equal(0, cache.Count); // the expired entry is dropped on lookup
    }

    [Fact]
    public void Remove_AndRemoveByPrefix()
    {
        var cache = new NegativeTileCache(10, TimeSpan.FromMinutes(5));
        cache.Add("t1/1/0/0_0");
        cache.Add("t1/1/0/1_0");
        cache.Add("t1/2/0/0_0");
        cache.Add("t2/1/0/0_0");

        Assert.True(cache.Remove("t1/1/0/0_0"));
        Assert.False(cache.Remove("t1/1/0/0_0"));

        cache.RemoveByPrefix("t1/");
        Assert.False(cache.Contains("t1/1/0/1_0"));
        Assert.False(cache.Contains("t1/2/0/0_0"));
        Assert.True(cache.Contains("t2/1/0/0_0"));
    }

    [Fact]
    public void Add_OverCapacity_DropsExpiredFirstThenSoonestToExpire()
    {
        var time = new ManualTimeProvider();
        var cache = new NegativeTileCache(10, TimeSpan.FromMinutes(5), time);

        // A second apart, so every entry has its own expiry and the eviction order is exact.
        for (var i = 0; i < 5; i++)
        {
            cache.Add($"early{i}"); // expire 5:00 .. 5:04
            time.Advance(TimeSpan.FromSeconds(1));
        }
        time.Advance(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 6; i++)
        {
            cache.Add($"late{i}"); // expire 7:05 .. 7:10; the 11th entry triggers eviction
            time.Advance(TimeSpan.FromSeconds(1));
        }

        // Nothing has expired yet, so the two soonest to expire go (trim to 90% = 9).
        Assert.Equal(9, cache.Count);
        Assert.False(cache.Contains("early0"));
        Assert.False(cache.Contains("early1"));
        Assert.True(cache.Contains("early2"));
        Assert.True(cache.Contains("late5"));

        time.Advance(TimeSpan.FromMinutes(3)); // 5:11: early2..early4 have expired
        cache.Add("x0");
        cache.Add("x1"); // 11 entries: the 3 expired go, 8 live remain, under the 90% mark

        Assert.Equal(8, cache.Count);
        Assert.False(cache.Contains("early4"));
        Assert.True(cache.Contains("late0"));
        Assert.True(cache.Contains("x1"));
    }

    [Fact]
    public async Task ConcurrentAddsRemovesAndLookups_NeverThrow_AndStayBounded()
    {
        // The production shape: a cache at its cap with many threads adding at once while others
        // remove (upload invalidation) and read. The old eviction threw within seconds of this.
        const int capacity = 1_000;
        var cache = new NegativeTileCache(capacity, TimeSpan.FromMinutes(5));

        var workers = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                cache.Add($"tenant-{t}/1/0/{i}_0");
                if (i % 7 == 0) cache.Remove($"tenant-{t}/1/0/{i / 2}_0");
                if (i % 11 == 0) cache.Contains($"tenant-{(t + 1) % 8}/1/0/{i}_0");
                if (i % 5_000 == 0) cache.RemoveByPrefix($"tenant-{t}/");
            }
        })).ToArray();

        await Task.WhenAll(workers); // an exception on any worker fails the test here

        // One more add without concurrency settles the soft cap.
        cache.Add("settle");
        Assert.InRange(cache.Count, 1, capacity);
    }
}
