using HnHMapperServer.Web.Services.Map;

namespace HnHMapperServer.Web.Tests;

public sealed class MarkerPollBackoffTests
{
    private static readonly TimeSpan[] Fast = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    [Fact]
    public void Failures_retry_fast_then_every_interval_forever()
    {
        var poll = new MarkerPollBackoff();

        Assert.Equal(Fast, Enumerable.Range(0, 3).Select(_ => poll.OnFailure().Delay));
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(MarkerPollBackoff.Interval, poll.OnFailure().Delay);
        }
    }

    [Fact]
    public void Failing_is_reported_exactly_once_per_streak()
    {
        var poll = new MarkerPollBackoff();

        var notices = Enumerable.Range(0, 20).Select(_ => poll.OnFailure()).Count(d => d.NotifyFailing);

        Assert.Equal(1, notices);
    }

    [Fact]
    public void Fast_retries_do_not_notify()
    {
        var poll = new MarkerPollBackoff();

        Assert.All(Enumerable.Range(0, 3).Select(_ => poll.OnFailure()), d => Assert.False(d.NotifyFailing));
        Assert.True(poll.OnFailure().NotifyFailing);
    }

    [Fact]
    public void Success_returns_to_interval_and_reports_recovery_once_after_a_notified_streak()
    {
        var poll = new MarkerPollBackoff();
        for (var i = 0; i < 5; i++) poll.OnFailure();

        var first = poll.OnSuccess();
        var second = poll.OnSuccess();

        Assert.Equal(MarkerPollBackoff.Interval, first.Delay);
        Assert.True(first.NotifyRecovered);
        Assert.False(second.NotifyRecovered);
        Assert.Equal(TimeSpan.FromSeconds(2), poll.OnFailure().Delay);
    }

    [Fact]
    public void Success_after_only_fast_retries_reports_nothing()
    {
        var poll = new MarkerPollBackoff();
        poll.OnFailure();
        poll.OnFailure();

        var decision = poll.OnSuccess();

        Assert.False(decision.NotifyRecovered);
        Assert.False(decision.NotifyFailing);
    }

    [Fact]
    public void Reset_after_slow_mode_restarts_fast_retries_without_renotifying()
    {
        var poll = new MarkerPollBackoff();
        for (var i = 0; i < 5; i++) poll.OnFailure();

        poll.ResetFastRetries();
        var afterReset = Enumerable.Range(0, 6).Select(_ => poll.OnFailure()).ToList();

        Assert.Equal(Fast, afterReset.Take(3).Select(d => d.Delay));
        Assert.All(afterReset, d => Assert.False(d.NotifyFailing));
        // The notice shown before the reset still gets its "resumed"
        Assert.True(poll.OnSuccess().NotifyRecovered);
    }

    [Fact]
    public void Concurrent_use_is_consistent()
    {
        var poll = new MarkerPollBackoff();
        var notices = 0;

        Parallel.For(0, 10_000, i =>
        {
            var decision = i % 50 == 0 ? poll.OnSuccess() : poll.OnFailure();
            if (decision.NotifyFailing) Interlocked.Increment(ref notices);
            Assert.True(decision.Delay > TimeSpan.Zero);
        });

        // At most one notice per streak: every streak but the last is ended by one of the 200 successes
        Assert.InRange(notices, 0, 10_000 / 50 + 1);

        // Counters are still consistent afterwards
        poll.OnSuccess();
        Assert.Equal(Fast, Enumerable.Range(0, 3).Select(_ => poll.OnFailure().Delay));
        Assert.True(poll.OnFailure().NotifyFailing);
    }
}
