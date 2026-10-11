using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// ZoomTileBatchRegenerator: each planned tile is force-regenerated exactly once, zoom-0 cells before
/// any parent; Generated/Failed are per request as before; and the dedup keys are released just
/// before each cell's sources are read (an upload landing later is queued again instead of being
/// lost behind a read that already happened) while the request stays IsQueued until the batch ends.
/// </summary>
public class ZoomTileBatchRegeneratorTests
{
    private readonly ZoomTileQueueService _queue = new(Mock.Of<ILogger<ZoomTileQueueService>>());
    private readonly Mock<ILargeTileService> _tiles = new();
    private readonly List<(int Zoom, int X, int Y)> _calls = new();

    private static readonly byte[] SomeBytes = { 1, 2, 3 };

    public ZoomTileBatchRegeneratorTests()
    {
        _tiles
            .Setup(t => t.ForceRegenerateLargeTileAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Callback<string, int, int, int, int>((_, _, z, x, y) => _calls.Add((z, x, y)))
            .ReturnsAsync(SomeBytes);
    }

    private ZoomTileBatchRegenerator CreateRegenerator(bool registerTileService = true)
    {
        var services = new ServiceCollection();
        if (registerTileService)
        {
            services.AddScoped(_ => _tiles.Object);
        }
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ZoomTileBatchRegenerator(_queue, scopeFactory, Mock.Of<ILogger<ZoomTileBatchRegenerator>>());
    }

    private static ZoomTileRequest R(int x, int y, int map = 1) => new("t", map, x, y);

    /// <summary>Enqueue and read back, as the processor does: the keys are pending.</summary>
    private ZoomTileBatchPlan Drain(params ZoomTileRequest[] requests)
    {
        foreach (var request in requests)
        {
            Assert.True(_queue.EnqueueZoomRegeneration(request));
        }
        var batch = new List<ZoomTileRequest>();
        while (_queue.Reader.TryRead(out var read))
        {
            batch.Add(read);
        }
        return ZoomTileBatchPlanner.Plan(batch);
    }

    private static ZoomTileRequest[] Grids3To5() =>
        (from y in Enumerable.Range(3, 3) from x in Enumerable.Range(3, 3) select R(x, y)).ToArray();

    [Fact]
    public async Task NineAdjacentGrids_RegenerateEachDistinctTileOnce_ZeroLevelFirst()
    {
        var requests = Grids3To5();
        var plan = Drain(requests);

        var result = await CreateRegenerator().RegenerateAsync(plan, CancellationToken.None);

        Assert.Equal(10, _calls.Count);                       // 4 cells + one ancestor per zoom (was 63)
        Assert.Equal(_calls.Count, _calls.Distinct().Count());
        Assert.All(_calls.Take(4), c => Assert.Equal(0, c.Zoom));
        Assert.Equal(_calls.Select(c => c.Zoom).OrderBy(z => z), _calls.Select(c => c.Zoom));
        Assert.Equal(9, result.Generated);
        Assert.Equal(0, result.Failed);
        Assert.Equal(4, result.Cells);
        Assert.Equal(10, result.Tiles);
        Assert.Equal(new[] { 1 }, result.AffectedMaps);
        Assert.Equal(0, _queue.PendingCount);
        Assert.All(requests, r => Assert.False(_queue.IsQueued(r)));
    }

    [Fact]
    public async Task NullZoom0_StillRegeneratesTheParents()
    {
        // null = the cell's sources are gone and its stale file was deleted: the parents must
        // recomposite without it or the old imagery ghosts on at far-out zooms
        _tiles
            .Setup(t => t.ForceRegenerateLargeTileAsync("t", 1, 0, It.IsAny<int>(), It.IsAny<int>()))
            .Callback<string, int, int, int, int>((_, _, z, x, y) => _calls.Add((z, x, y)))
            .ReturnsAsync((byte[]?)null);

        var result = await CreateRegenerator().RegenerateAsync(Drain(R(5, 5)), CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, _calls.Select(c => c.Zoom));
        Assert.Equal(0, result.Generated);
        Assert.Equal(1, result.Failed);
        Assert.Equal(new[] { 1 }, result.AffectedMaps);
        Assert.False(_queue.IsQueued(R(5, 5)));
    }

    [Fact]
    public async Task ReleasesEachCellsKeys_JustBeforeItsZoom0Read()
    {
        var inCell00 = R(1, 1);
        var inCell11 = R(5, 5);
        var plan = Drain(inCell00, inCell11);
        bool? acceptedForReadCell = null, acceptedForPendingCell = null, stillQueued = null;

        _tiles
            .Setup(t => t.ForceRegenerateLargeTileAsync("t", 1, 0, 0, 0))
            .Callback(() =>
            {
                _calls.Add((0, 0, 0));
                stillQueued = _queue.IsQueued(inCell00);
                // An upload into the cell being read now: queued again (next batch), not skipped
                acceptedForReadCell = _queue.EnqueueZoomRegeneration(inCell00);
                // An upload into a cell whose read is still ahead: covered by that read, skipped
                acceptedForPendingCell = _queue.EnqueueZoomRegeneration(inCell11);
            })
            .ReturnsAsync(SomeBytes);

        await CreateRegenerator().RegenerateAsync(plan, CancellationToken.None);

        Assert.True(stillQueued);           // in flight: the backstop must not count it as done yet
        Assert.True(acceptedForReadCell);
        Assert.False(acceptedForPendingCell);
        Assert.True(_queue.Reader.TryRead(out var requeued));
        Assert.Equal(inCell00, requeued);
        Assert.Equal(1, _queue.PendingCount); // the batch's end never releases the re-enqueued key
        Assert.True(_queue.IsQueued(inCell00));
        Assert.False(_queue.IsQueued(inCell11));
    }

    [Fact]
    public async Task UnexpectedThrow_ContinuesWithTheOtherTiles_AndReleasesAll()
    {
        _tiles
            .Setup(t => t.ForceRegenerateLargeTileAsync("t", 1, 0, 1, 1))
            .Callback(() => _calls.Add((0, 1, 1)))
            .ThrowsAsync(new IOException("disk"));

        var requests = Grids3To5();
        var result = await CreateRegenerator().RegenerateAsync(Drain(requests), CancellationToken.None);

        Assert.Equal(10, _calls.Count);     // the failing cell's ancestors and siblings still built
        Assert.Equal(4, result.Failed);     // bases (4..5, 4..5) are cell (1,1)
        Assert.Equal(5, result.Generated);
        Assert.Equal(0, _queue.PendingCount);
        Assert.All(requests, r => Assert.False(_queue.IsQueued(r)));
    }

    [Fact]
    public async Task ScopeResolutionFails_StillReleasesTheKeys()
    {
        var requests = Grids3To5();
        var plan = Drain(requests);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateRegenerator(registerTileService: false).RegenerateAsync(plan, CancellationToken.None));

        Assert.Equal(0, _queue.PendingCount);
        Assert.All(requests, r => Assert.False(_queue.IsQueued(r)));
    }

    [Fact]
    public async Task AlreadyCancelled_RegeneratesNothing_AndReleasesAll()
    {
        var requests = Grids3To5();
        var plan = Drain(requests);

        var result = await CreateRegenerator().RegenerateAsync(plan, new CancellationToken(canceled: true));

        Assert.Empty(_calls);
        Assert.Equal(0, result.Generated + result.Failed);
        Assert.Empty(result.AffectedMaps);
        Assert.Equal(0, _queue.PendingCount);
        Assert.All(requests, r => Assert.False(_queue.IsQueued(r)));
    }

    [Fact]
    public async Task CancelledMidBatch_ReleasesTheCellsNotYetReached()
    {
        using var cts = new CancellationTokenSource();
        _tiles
            .Setup(t => t.ForceRegenerateLargeTileAsync("t", 1, 0, 0, 0))
            .Callback(() =>
            {
                _calls.Add((0, 0, 0));
                cts.Cancel();
            })
            .ReturnsAsync(SomeBytes);

        var result = await CreateRegenerator().RegenerateAsync(Drain(R(1, 1), R(5, 5)), cts.Token);

        Assert.Equal(new[] { (0, 0, 0) }, _calls);
        Assert.Equal(1, result.Generated);
        Assert.Equal(0, _queue.PendingCount);
        Assert.False(_queue.IsQueued(R(1, 1)));
        Assert.False(_queue.IsQueued(R(5, 5)));
    }

    [Fact]
    public async Task TwoMaps_AreBothAffected()
    {
        var result = await CreateRegenerator().RegenerateAsync(Drain(R(1, 1, map: 1), R(1, 1, map: 2)), CancellationToken.None);

        Assert.Equal(new[] { 1, 2 }, result.AffectedMaps.OrderBy(m => m));
        Assert.Equal(14, _calls.Count);
    }
}
