using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using HnHMapperServer.Services.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// The bounded (4096, DropOldest) ZoomQ channel: a dropped request's dedup key used to stay in the
/// pending set forever, so that base coord could never be enqueued again and PendingCount — the
/// headroom gauge of every bulk producer — only grew until a restart. Also the batch read (a plain
/// delay + drain, where the old debounce threw one OperationCanceledException per batch) and the
/// processing window (BeginProcessing / EndProcessing).
/// </summary>
public class ZoomTileQueueServiceTests
{
    private const int Capacity = 4096;

    private readonly Mock<ILogger<ZoomTileQueueService>> _logger = new();

    private static ZoomTileRequest Request(int i) => new("tenant", 1, i, 0);

    [Fact]
    public void DropOldest_ReleasesTheDroppedRequestsDedupKey()
    {
        var queue = new ZoomTileQueueService(_logger.Object);

        for (var i = 0; i <= Capacity; i++) // one more than fits: request 0 is dropped
        {
            Assert.True(queue.EnqueueZoomRegeneration(Request(i)));
        }

        Assert.Equal(Capacity, queue.PendingCount);
        Assert.Equal(1, queue.DroppedCount);

        // Pre-fix this was "a duplicate" of a request no longer in the channel, forever.
        Assert.True(queue.EnqueueZoomRegeneration(Request(0)));

        var drained = new List<ZoomTileRequest>();
        while (queue.Reader.TryRead(out var request))
        {
            queue.MarkCompleted(request);
            drained.Add(request);
        }

        Assert.Contains(Request(0), drained);
        Assert.DoesNotContain(Request(1), drained); // the re-enqueue dropped the next oldest
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Duplicate_IsNotQueuedTwice_UntilCompleted()
    {
        var queue = new ZoomTileQueueService(_logger.Object);

        Assert.True(queue.EnqueueZoomRegeneration(Request(7)));
        Assert.False(queue.EnqueueZoomRegeneration(Request(7)));

        Assert.True(queue.Reader.TryRead(out var read));
        queue.MarkCompleted(read!);

        Assert.True(queue.EnqueueZoomRegeneration(Request(7)));
    }

    [Fact]
    public void Overflow_WarnsAtMostOncePerMinute()
    {
        var queue = new ZoomTileQueueService(_logger.Object);

        for (var i = 0; i < Capacity + 50; i++)
        {
            queue.EnqueueZoomRegeneration(Request(i));
        }

        Assert.Equal(50, queue.DroppedCount);
        _logger.Verify(x => x.Log(
            LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Queue full: dropped")),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    // First-chance exceptions are process-wide; only those raised in this test's own async flow count.
    private static readonly AsyncLocal<bool> InThisFlow = new();

    private static async Task<List<Exception>> FirstChanceExceptionsDuring(Func<Task> action)
    {
        var thrown = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, e) =>
        {
            if (InThisFlow.Value)
            {
                thrown.Enqueue(e.Exception);
            }
        };

        AppDomain.CurrentDomain.FirstChanceException += handler;
        try
        {
            InThisFlow.Value = true;
            await action();
        }
        finally
        {
            InThisFlow.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }
        return thrown.ToList();
    }

    [Fact]
    public async Task ReadBatchAsync_CollectsRequestsEnqueuedDuringTheWindow()
    {
        var queue = new ZoomTileQueueService(_logger.Object);
        queue.EnqueueZoomRegeneration(Request(1));

        var read = queue.ReadBatchAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        queue.EnqueueZoomRegeneration(Request(2)); // queued long before the window ends
        queue.EnqueueZoomRegeneration(Request(3));
        var batch = await read;

        Assert.Equal(new[] { 1, 2, 3 }, batch.Select(r => r.BaseX));
    }

    [Fact]
    public async Task ReadBatchAsync_WaitsForTheFirstRequest()
    {
        var queue = new ZoomTileQueueService(_logger.Object);

        var read = queue.ReadBatchAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(read.IsCompleted);

        queue.EnqueueZoomRegeneration(Request(9));
        Assert.Equal(9, Assert.Single(await read).BaseX);
    }

    [Fact]
    public async Task ReadBatchAsync_RequestsAfterTheWindow_GoToTheNextBatch()
    {
        var queue = new ZoomTileQueueService(_logger.Object);

        queue.EnqueueZoomRegeneration(Request(1));
        var first = await queue.ReadBatchAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        queue.EnqueueZoomRegeneration(Request(2));
        var second = await queue.ReadBatchAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

        Assert.Equal(1, Assert.Single(first).BaseX);
        Assert.Equal(2, Assert.Single(second).BaseX);
    }

    [Fact]
    public async Task ReadBatchAsync_WindowExpiry_ThrowsNoFirstChanceException()
    {
        var queue = new ZoomTileQueueService(_logger.Object);
        List<ZoomTileRequest>? batch = null;

        var thrown = await FirstChanceExceptionsDuring(async () =>
        {
            queue.EnqueueZoomRegeneration(Request(1));
            batch = await queue.ReadBatchAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        });

        Assert.Single(batch!);
        Assert.Empty(thrown); // the CancelAfter debounce threw one OperationCanceledException per window
    }

    [Fact]
    public async Task FirstChanceDetector_SeesTheOldCancelAfterDebounce()
    {
        // Proves the detector above can fail: the replaced pattern, verbatim in shape
        var channel = Channel.CreateUnbounded<int>();

        var thrown = await FirstChanceExceptionsDuring(async () =>
        {
            try
            {
                using var delayCts = new CancellationTokenSource();
                delayCts.CancelAfter(TimeSpan.FromMilliseconds(20));
                await channel.Reader.ReadAsync(delayCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });

        Assert.Contains(thrown, e => e is OperationCanceledException);
    }

    [Fact]
    public async Task ReadBatchAsync_Shutdown_ThrowsOperationCanceled()
    {
        var queue = new ZoomTileQueueService(_logger.Object);
        queue.EnqueueZoomRegeneration(Request(1));
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // The processor's `catch (OperationCanceledException) when (stoppingToken...)` relies on it
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.ReadBatchAsync(TimeSpan.FromSeconds(30), stopping.Token));
    }

    [Fact]
    public async Task ReadBatchAsync_Shutdown_WhileWaitingForTheFirstRequest()
    {
        var queue = new ZoomTileQueueService(_logger.Object);
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.ReadBatchAsync(TimeSpan.FromMilliseconds(10), stopping.Token));
    }

    [Fact]
    public void BeginProcessing_ReleasesTheKey_ButTheRequestStaysQueuedUntilEndProcessing()
    {
        var queue = new ZoomTileQueueService(_logger.Object);
        queue.EnqueueZoomRegeneration(Request(5));
        Assert.True(queue.Reader.TryRead(out var read));

        queue.BeginProcessing(read!);

        Assert.Equal(0, queue.PendingCount);
        Assert.True(queue.IsQueued(Request(5)));                     // the backstop still sees it in flight
        Assert.True(queue.EnqueueZoomRegeneration(Request(5)));      // a later upload is queued, not skipped

        queue.EndProcessing(read!);

        Assert.True(queue.IsQueued(Request(5)));                     // the re-enqueued request
        Assert.Equal(1, queue.PendingCount);                         // EndProcessing never releases it
        Assert.False(queue.EnqueueZoomRegeneration(Request(5)));
    }

    [Fact]
    public void EndProcessing_WithoutReEnqueue_LeavesNothingQueued()
    {
        var queue = new ZoomTileQueueService(_logger.Object);
        queue.EnqueueZoomRegeneration(Request(5));
        Assert.True(queue.Reader.TryRead(out var read));

        queue.BeginProcessing(read!);
        queue.EndProcessing(read!);

        Assert.False(queue.IsQueued(Request(5)));
        Assert.Equal(0, queue.PendingCount);
    }
}
