using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Services;
using Xunit;

namespace HnHMapperServer.Tests;

/// <summary>
/// SSE subscription lifecycle regression tests. The original implementation kept
/// channels in ConcurrentBags nothing ever removed from, and its "dead channel
/// cleanup" was unreachable (TryWrite never fails on unbounded/DropOldest
/// channels) — every dead SSE connection buffered every future event forever.
/// These pin the fix: disposal unregisters, and channels are genuinely bounded.
/// </summary>
public class UpdateNotificationServiceTests
{
    [Fact]
    public void Dispose_UnregistersSubscriber_AndCompletesReader()
    {
        var svc = new UpdateNotificationService();
        var sub = svc.SubscribeToTileUpdates();

        svc.NotifyTileUpdate(new TileData { MapId = 1, TenantId = "t" });
        Assert.True(sub.Reader.TryRead(out _));
        Assert.Equal(1, svc.ActiveSubscriptionCount);

        sub.Dispose();
        Assert.Equal(0, svc.ActiveSubscriptionCount);

        // Events after dispose must not be buffered anywhere for this subscriber.
        svc.NotifyTileUpdate(new TileData { MapId = 2, TenantId = "t" });
        Assert.False(sub.Reader.TryRead(out _));
        Assert.True(sub.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndLeavesOtherSubscribersIntact()
    {
        var svc = new UpdateNotificationService();
        var subA = svc.SubscribeToTileUpdates();
        using var subB = svc.SubscribeToTileUpdates();

        subA.Dispose();
        subA.Dispose();

        svc.NotifyTileUpdate(new TileData { MapId = 7, TenantId = "t" });
        Assert.False(subA.Reader.TryRead(out _));
        Assert.True(subB.Reader.TryRead(out var tile));
        Assert.Equal(7, tile.MapId);
    }

    [Fact]
    public void StalledSubscriber_IsCappedAtChannelCapacity_DroppingOldest()
    {
        var svc = new UpdateNotificationService();
        using var sub = svc.SubscribeToTileUpdates();

        for (var i = 0; i < 1500; i++)
        {
            svc.NotifyTileUpdate(new TileData { MapId = i, TenantId = "t" });
        }

        var drained = new List<int>();
        while (sub.Reader.TryRead(out var tile))
        {
            drained.Add(tile.MapId);
        }

        Assert.Equal(1024, drained.Count);
        Assert.Equal(1500 - 1024, drained[0]); // the oldest events were dropped
        Assert.Equal(1499, drained[^1]);
    }

    [Fact]
    public void ActiveSubscriptionCount_SpansAllEventTypes()
    {
        var svc = new UpdateNotificationService();
        var subs = new IDisposable[]
        {
            svc.SubscribeToTileUpdates(),
            svc.SubscribeToCharacterDelta(),
            svc.SubscribeToNotificationCreated(),
            svc.SubscribeToMarkerDeleted()
        };

        Assert.Equal(4, svc.ActiveSubscriptionCount);

        foreach (var sub in subs)
        {
            sub.Dispose();
        }

        Assert.Equal(0, svc.ActiveSubscriptionCount);
    }
}
