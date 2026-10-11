using HnHMapperServer.Core.Enums;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// ClientTokenRejectionTracker: first sighting per (tenant, token hash) → one Warning, then one
/// summary per interval; bounded per tenant; unattributed rejections pooled per reason; pruning
/// safe under concurrent recording (ToArray + value-matched TryRemove, like NegativeTileCache).
/// The replay test is one client retrying with a rejected token: 189 positionUpdates 8 s apart
/// must produce 2 Warnings, not one or more per request.
/// </summary>
public class ClientTokenRejectionTrackerTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private const string Tenant = "sketchbook-pot-u-2046";
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private const ClientTokenRejectionReason NotFound = ClientTokenRejectionReason.NotFound;

    private static string Hash(int i) => i.ToString("x64");

    [Fact]
    public void FirstSighting_ReturnsFirst_RepeatWithinInterval_ReturnsNone()
    {
        var time = new ManualTimeProvider();
        var tracker = new ClientTokenRejectionTracker(new ClientTokenRejectionOptions(), time);

        var first = tracker.RecordAttributed(Tenant, HashA, NotFound);
        Assert.Equal(RejectionLogAction.First, first.Action);
        Assert.Equal(1, first.Count);
        Assert.False(first.Folded);

        time.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(RejectionLogAction.None, tracker.RecordAttributed(Tenant, HashA, NotFound).Action);
    }

    [Fact]
    public void SessionB_Replay_FirstThenOneSummary()
    {
        var time = new ManualTimeProvider();
        var start = time.GetUtcNow();
        var tracker = new ClientTokenRejectionTracker(new ClientTokenRejectionOptions(), time);

        var decisions = new List<(int Index, RejectionDecision Decision)>();
        for (var i = 0; i < 189; i++)
        {
            var d = tracker.RecordAttributed(Tenant, HashA, NotFound);
            if (d.Action != RejectionLogAction.None)
                decisions.Add((i, d));
            time.Advance(TimeSpan.FromSeconds(8));
        }

        Assert.Equal(2, decisions.Count);
        Assert.Equal((0, RejectionLogAction.First), (decisions[0].Index, decisions[0].Decision.Action));
        // t = 113 * 8 s = 904 s, the first request at or past the 15-minute mark.
        Assert.Equal(113, decisions[1].Index);
        Assert.Equal(RejectionLogAction.Summary, decisions[1].Decision.Action);
        Assert.Equal(113, decisions[1].Decision.Count);
        Assert.Equal(start, decisions[1].Decision.Since);
    }

    [Fact]
    public void IdleLongerThanIdleExpiry_IsFirstAgain()
    {
        var time = new ManualTimeProvider();
        var tracker = new ClientTokenRejectionTracker(new ClientTokenRejectionOptions(), time);
        tracker.RecordAttributed(Tenant, HashA, NotFound);

        time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(RejectionLogAction.First, tracker.RecordAttributed(Tenant, HashA, NotFound).Action);
    }

    [Fact]
    public void ReasonChange_IsFirstAgain()
    {
        var time = new ManualTimeProvider();
        var tracker = new ClientTokenRejectionTracker(new ClientTokenRejectionOptions(), time);
        tracker.RecordAttributed(Tenant, HashA, NotFound);
        time.Advance(TimeSpan.FromSeconds(8));

        Assert.Equal(RejectionLogAction.First,
            tracker.RecordAttributed(Tenant, HashA, ClientTokenRejectionReason.TenantInactive).Action);
    }

    [Fact]
    public void DistinctHashesSameTenant_AreTrackedSeparately()
    {
        var tracker = new ClientTokenRejectionTracker(new ClientTokenRejectionOptions(), new ManualTimeProvider());

        Assert.Equal(RejectionLogAction.First, tracker.RecordAttributed(Tenant, HashA, NotFound).Action);
        Assert.Equal(RejectionLogAction.First, tracker.RecordAttributed(Tenant, HashB, NotFound).Action);
        Assert.Equal(RejectionLogAction.None, tracker.RecordAttributed(Tenant, HashA, NotFound).Action);
        Assert.Equal(2, tracker.TrackedEntryCount(Tenant));
    }

    [Fact]
    public void PerTenantCap_FoldsFurtherHashes_IntoOneOverflowEntry_OtherTenantUnaffected()
    {
        var tracker = new ClientTokenRejectionTracker(new ClientTokenRejectionOptions(), new ManualTimeProvider());
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(RejectionLogAction.First, tracker.RecordAttributed(Tenant, Hash(i), NotFound).Action);
        }

        var seventeenth = tracker.RecordAttributed(Tenant, Hash(16), NotFound);
        Assert.Equal(RejectionLogAction.First, seventeenth.Action);
        Assert.True(seventeenth.Folded);
        for (var i = 17; i < 40; i++)
        {
            var d = tracker.RecordAttributed(Tenant, Hash(i), NotFound);
            Assert.Equal(RejectionLogAction.None, d.Action);
            Assert.True(d.Folded);
        }

        Assert.Equal(17, tracker.TrackedEntryCount(Tenant));
        var other = tracker.RecordAttributed("other-tenant-1", Hash(99), NotFound);
        Assert.Equal(RejectionLogAction.First, other.Action);
        Assert.False(other.Folded);
    }

    [Fact]
    public void Unattributed_ManyRequests_AtMostOneWarningPerReasonPerInterval()
    {
        var time = new ManualTimeProvider();
        var tracker = new ClientTokenRejectionTracker(new ClientTokenRejectionOptions(), time);
        var reasons = Enum.GetValues<ClientTokenRejectionReason>();
        var warnings = new Dictionary<ClientTokenRejectionReason, int>();

        // 10,000 requests over 30 minutes (~0.18 s apart), cycling through every reason.
        for (var i = 0; i < 10_000; i++)
        {
            var reason = reasons[i % reasons.Length];
            if (tracker.RecordUnattributed(reason).Action != RejectionLogAction.None)
                warnings[reason] = warnings.GetValueOrDefault(reason) + 1;
            time.Advance(TimeSpan.FromMilliseconds(180));
        }

        // First + one summary at the 15-minute mark (+ possibly one more right at 30 min).
        Assert.All(reasons, r => Assert.InRange(warnings.GetValueOrDefault(r), 1, 3));
        Assert.Equal(0, tracker.TrackedTenantCount); // nothing attributed, nothing per tenant
    }

    [Fact]
    public void Prune_DropsIdleEntriesAndEmptyTenants()
    {
        var time = new ManualTimeProvider();
        var tracker = new ClientTokenRejectionTracker(new ClientTokenRejectionOptions(), time);
        tracker.RecordAttributed(Tenant, HashA, NotFound);
        tracker.RecordAttributed("other-tenant-1", HashB, NotFound);
        Assert.Equal(2, tracker.TrackedTenantCount);

        time.Advance(TimeSpan.FromHours(25));
        tracker.RecordUnattributed(ClientTokenRejectionReason.MalformedToken); // any record triggers the prune

        Assert.Equal(0, tracker.TrackedTenantCount);
        Assert.Equal(RejectionLogAction.First, tracker.RecordAttributed(Tenant, HashA, NotFound).Action);
    }

    [Fact]
    public async Task Concurrent_RecordAndPrune_DoNotThrow_AndStayBounded()
    {
        var time = new ManualTimeProvider();
        var tracker = new ClientTokenRejectionTracker(
            new ClientTokenRejectionOptions { IdleExpiry = TimeSpan.FromMinutes(2) }, time);
        using var stop = new CancellationTokenSource();

        // Advances the clock in 30 s steps so the once-a-minute prune keeps firing and entries
        // keep expiring while 50,000 records land from the thread pool.
        var clock = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                time.Advance(TimeSpan.FromSeconds(30));
                await Task.Delay(1);
            }
        });

        Parallel.For(0, 50_000, i =>
        {
            var tenant = $"tenant-{i % 100}";
            tracker.RecordAttributed(tenant, Hash(i % 40), NotFound);
            if (i % 7 == 0)
                tracker.RecordUnattributed(ClientTokenRejectionReason.MalformedToken);
        });

        stop.Cancel();
        await clock;

        for (var t = 0; t < 100; t++)
        {
            Assert.InRange(tracker.TrackedEntryCount($"tenant-{t}"), 0, 17);
        }
        Assert.InRange(tracker.TrackedTenantCount, 0, 100);
    }
}
