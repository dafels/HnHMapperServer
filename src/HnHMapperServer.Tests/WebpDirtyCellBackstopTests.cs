using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ITenantContextAccessor = HnHMapperServer.Core.Interfaces.ITenantContextAccessor;

namespace HnHMapperServer.Tests;

/// <summary>
/// The durable WebP backstop over DirtyWebpCells, on real SQLite (conditional ExecuteDelete, the
/// raw-SQL legacy seed with SQLite's arithmetic shift, and the Maps foreign key all need it) with a
/// temp GridStorage whose file mtimes are set explicitly. The DbContext has no
/// IHttpContextAccessor, like the background service the backstop runs in.
///
/// What it replaces: the old backstop read zoom-1 DirtyZoomTiles rows, which the legacy PNG rebuild
/// deletes on its own schedule (it saw 0-100% of the marks depending on timer phase), whose
/// CreatedAt never moved forward, and judged freshness on the z0 file only.
/// </summary>
public class WebpDirtyCellBackstopTests : IDisposable
{
    private const int MapId = 1;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly string _gridStorage;
    private readonly string _tenantId = $"backstop-{Guid.NewGuid():N}";
    private readonly ZoomTileQueueService _queue = new(Mock.Of<ILogger<ZoomTileQueueService>>());
    private readonly LargeTileService _largeTileService;
    private readonly TileService _tileService;
    private readonly WebpDirtyCellBackstop _backstop;
    private readonly Mock<ILogger<WebpDirtyCellBackstop>> _logger = new();

    public WebpDirtyCellBackstopTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        SeedTenant(_tenantId);
        SeedMap(_tenantId, MapId);

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-backstop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GridStorage"] = _gridStorage })
            .Build();

        var tenantContext = new Mock<ITenantContextAccessor>();
        tenantContext.Setup(x => x.GetCurrentTenantId()).Returns(_tenantId);
        tenantContext.Setup(x => x.GetRequiredTenantId()).Returns(_tenantId);

        _largeTileService = new LargeTileService(_db, configuration, Mock.Of<ILogger<LargeTileService>>());
        _tileService = new TileService(
            new TileRepository(_db, tenantContext.Object),
            new GridRepository(_db, tenantContext.Object),
            Mock.Of<IUpdateNotificationService>(),
            Mock.Of<IStorageQuotaService>(),
            Mock.Of<ILogger<TileService>>(),
            _db);
        _backstop = new WebpDirtyCellBackstop(_db, _largeTileService, _queue, configuration, _logger.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private void SeedTenant(string tenantId)
    {
        _db.Tenants.Add(new TenantEntity
        {
            Id = tenantId, Name = tenantId, StorageQuotaMB = 1024, CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow, IsActive = true
        });
        _db.SaveChanges();
    }

    private void SeedMap(string tenantId, int mapId)
    {
        _db.Maps.Add(new MapInfoEntity { Id = mapId, Name = $"Map{mapId}", TenantId = tenantId, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    /// <summary>A zoom-0 row whose source PNG exists (or not, for a dark cell).</summary>
    private void SeedZoom0Row(int x, int y, bool sourceExists = true)
    {
        var relative = Path.Combine("tenants", _tenantId, "grids", $"g{x}_{y}.png");
        if (sourceExists)
        {
            var full = Path.Combine(_gridStorage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var img = new Image<Rgba32>(100, 100, new Rgba32(0, 200, 0, 255));
            img.SaveAsPng(full);
        }

        _db.Tiles.Add(new TileDataEntity
        {
            MapId = MapId, CoordX = x, CoordY = y, Zoom = 0,
            File = relative, Cache = 1, TenantId = _tenantId, FileSizeBytes = 100
        });
        _db.SaveChanges();
    }

    /// <summary>A pyramid file of plausible length; the backstop only reads length and mtime.</summary>
    private string WriteWebp(int zoom, int x, int y, DateTime mtimeUtc)
    {
        var path = _largeTileService.GetLargeTilePath(_tenantId, MapId, zoom, x, y);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[64]);
        File.SetLastWriteTimeUtc(path, mtimeUtc);
        return path;
    }

    private Task MarkAsync(params Coord[] baseCoords) =>
        _tileService.MarkParentTilesDirtyBatchAsync(_tenantId, MapId, baseCoords);

    private Task SetMarkedAtAsync(DateTime markedAt) =>
        _db.DirtyWebpCells.Where(d => d.TenantId == _tenantId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.MarkedAt, markedAt));

    private Task<List<DirtyWebpCellEntity>> MarksAsync() =>
        _db.DirtyWebpCells.AsNoTracking().Where(d => d.TenantId == _tenantId).ToListAsync();

    /// <summary>What the ZoomQ processor would take (and mark completed).</summary>
    private List<ZoomTileRequest> DrainQueue()
    {
        var drained = new List<ZoomTileRequest>();
        while (_queue.Reader.TryRead(out var request))
        {
            _queue.MarkCompleted(request);
            drained.Add(request);
        }
        return drained;
    }

    // ---- tests -----------------------------------------------------------------------------

    [Fact]
    public async Task LegacyConsumerRunsFirst_CellStillEnqueued_ThenClearedOnceFresh()
    {
        // The race that starved the old backstop: the legacy PNG rebuild consumes the zoom-1
        // dirty rows before the WebP scan reads them. The WebP mark is its own row now.
        SeedZoom0Row(1, 1);
        WriteWebp(0, 0, 0, DateTime.UtcNow.AddDays(-1));
        await MarkAsync(new Coord(1, 1));

        await _tileService.RebuildIncompleteZoomTilesAsync(_tenantId, _gridStorage, 500);
        Assert.False(await _db.DirtyZoomTiles.AnyAsync(d => d.TenantId == _tenantId));
        await SetMarkedAtAsync(DateTime.UtcNow.AddMinutes(-10));

        var first = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(1, first.Enqueued);
        Assert.Equal(new ZoomTileRequest(_tenantId, MapId, 0, 0), Assert.Single(DrainQueue()));
        Assert.Single(await MarksAsync()); // kept until the chain is proven fresh

        // The processor regenerated the chain.
        WriteWebp(0, 0, 0, DateTime.UtcNow);
        WriteWebp(1, 0, 0, DateTime.UtcNow);

        var second = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(0, second.Enqueued);
        Assert.Equal(1, second.Cleared);
        Assert.Empty(await MarksAsync());
        Assert.Empty(DrainQueue());
    }

    [Fact]
    public async Task MultiFlushImport_SecondFlushReenqueuesTheRegeneratedCell()
    {
        SeedZoom0Row(1, 1);
        await MarkAsync(new Coord(1, 1));                    // flush 1
        await SetMarkedAtAsync(DateTime.UtcNow.AddMinutes(-20));
        WriteWebp(0, 0, 0, DateTime.UtcNow.AddMinutes(-19)); // regenerated between the flushes

        var beforeSecondFlush = await _backstop.PlanAsync(_tenantId, DateTime.UtcNow);
        Assert.Single(beforeSecondFlush.Settled);

        await MarkAsync(new Coord(2, 2));                    // flush 2, same cell: the mark moves

        var result = await _backstop.RunAsync(_tenantId, DateTime.UtcNow.AddMinutes(6));

        Assert.Equal(1, result.Enqueued);
        Assert.Single(await MarksAsync());
    }

    [Fact]
    public async Task ReMarkAfterPlan_SurvivesTheConditionalDelete()
    {
        SeedZoom0Row(1, 1);
        await MarkAsync(new Coord(1, 1));
        await SetMarkedAtAsync(DateTime.UtcNow.AddMinutes(-20));
        WriteWebp(0, 0, 0, DateTime.UtcNow.AddMinutes(-10));

        var plan = await _backstop.PlanAsync(_tenantId, DateTime.UtcNow);
        Assert.Single(plan.Settled);

        await MarkAsync(new Coord(1, 2)); // an upload lands while the run is evaluating

        var result = await _backstop.ApplyAsync(plan);

        Assert.Equal(0, result.Cleared);
        var mark = Assert.Single(await MarksAsync());
        Assert.True(mark.MarkedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task MarkInsideSettleWindow_IsLeftToTheFastPath_OtherTenantsUntouched()
    {
        SeedZoom0Row(1, 1);
        await MarkAsync(new Coord(1, 1)); // fresh mark: the fast path is on it

        var otherTenant = $"other-{Guid.NewGuid():N}";
        SeedTenant(otherTenant);
        SeedMap(otherTenant, 2);
        _db.DirtyWebpCells.Add(new DirtyWebpCellEntity
        {
            TenantId = otherTenant, MapId = 2, CellX = 0, CellY = 0, MarkedAt = DateTime.UtcNow.AddHours(-1)
        });
        await _db.SaveChangesAsync();

        var result = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(new WebpDirtyCellBackstop.BackstopResult(0, 0, 0, 0), result);
        Assert.Single(await MarksAsync());
        Assert.True(await _db.DirtyWebpCells.AnyAsync(d => d.TenantId == otherTenant));
        Assert.Empty(DrainQueue());
    }

    [Fact]
    public async Task DarkCell_IsNotRegenerated_AndKeepsItsLastWebp()
    {
        // Rows, but their PNGs are gone (merge damage): a regeneration finds no sources and
        // deletes the z0 file — the last copy of that imagery. Nothing new can be drawn anyway.
        SeedZoom0Row(1, 1, sourceExists: false);
        var z0 = WriteWebp(0, 0, 0, DateTime.UtcNow.AddDays(-1));
        await MarkAsync(new Coord(1, 1));
        await SetMarkedAtAsync(DateTime.UtcNow.AddMinutes(-10));

        var result = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(1, result.Cleared);
        Assert.True(File.Exists(z0));
        Assert.Empty(DrainQueue());
    }

    [Fact]
    public async Task PartlyDarkCell_IsNotRegenerated_AndKeepsItsLastWebp()
    {
        // One row's PNG exists, the other's is gone. The zoom-0 compose skips missing sources,
        // so a regeneration would redraw the cell without the missing row — over the z0 that may
        // be the last copy of it. Classified per row, not "any source exists".
        SeedZoom0Row(1, 1);
        SeedZoom0Row(2, 2, sourceExists: false);
        var z0 = WriteWebp(0, 0, 0, DateTime.UtcNow.AddDays(-1));
        var before = File.GetLastWriteTimeUtc(z0);
        await MarkAsync(new Coord(1, 1));
        await SetMarkedAtAsync(DateTime.UtcNow.AddMinutes(-10));

        var result = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(1, result.Cleared);
        Assert.Equal(before, File.GetLastWriteTimeUtc(z0));
        Assert.Empty(DrainQueue());
    }

    [Fact]
    public async Task RegenerationEvictedByDropOldest_IsNotAnAttempt()
    {
        // The queue overflows and DropOldest evicts the backstop's request before the processor
        // reaches it. Counting it as an attempt gave up on cells that were never regenerated.
        SeedZoom0Row(1, 1);
        await MarkAsync(new Coord(1, 1));
        await SetMarkedAtAsync(DateTime.UtcNow.AddMinutes(-10));

        for (var run = 0; run < WebpDirtyCellBackstop.MaxAttempts; run++)
        {
            Assert.Equal(1, (await _backstop.RunAsync(_tenantId, DateTime.UtcNow)).Enqueued);

            var droppedBefore = _queue.DroppedCount;
            for (var i = 0; i < 4096; i++)
            {
                _queue.EnqueueZoomRegeneration(new ZoomTileRequest("flood", 99, run * 10_000 + i, 0));
            }
            Assert.True(_queue.DroppedCount > droppedBefore);
            Assert.DoesNotContain(new ZoomTileRequest(_tenantId, MapId, 0, 0), DrainQueue());
        }

        var next = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(0, next.GaveUp);
        Assert.Equal(1, next.Enqueued);
        Assert.Single(await MarksAsync());
    }

    [Fact]
    public async Task CellThatNeverTurnsFresh_GivesUpAfterMaxAttempts()
    {
        // A drawable cell whose regeneration never writes anything (e.g. an undecodable source).
        SeedZoom0Row(1, 1);
        await MarkAsync(new Coord(1, 1));
        await SetMarkedAtAsync(DateTime.UtcNow.AddMinutes(-10));

        for (var attempt = 1; attempt <= WebpDirtyCellBackstop.MaxAttempts; attempt++)
        {
            var run = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);
            Assert.Equal(1, run.Enqueued);
            Assert.Single(DrainQueue()); // processed, but nothing was written
        }

        var last = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(new WebpDirtyCellBackstop.BackstopResult(0, 1, 0, 1), last);
        Assert.Empty(await MarksAsync());
        _logger.Verify(x => x.Log(
            LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("gave up on 1 cell")),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task CellBeingProcessed_IsNotEnqueuedAgain_NorCountedAsAnAttempt()
    {
        // Between BeginProcessing and EndProcessing the request holds no dedup key, so the enqueue
        // alone would accept a duplicate of it (and overwrite the cell's attempt state). IsQueued
        // still reports it in flight; that is what stops the backstop.
        SeedZoom0Row(1, 1);
        await MarkAsync(new Coord(1, 1));
        await SetMarkedAtAsync(DateTime.UtcNow.AddMinutes(-10));

        var request = new ZoomTileRequest(_tenantId, MapId, 0, 0);
        Assert.True(_queue.EnqueueZoomRegeneration(request)); // the upload's own fast-path request
        Assert.True(_queue.Reader.TryRead(out var taken));
        _queue.BeginProcessing(taken);                         // the processor has started it

        var during = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(0, during.Enqueued);
        Assert.Equal(0, _queue.PendingCount);

        _queue.EndProcessing(taken); // that batch wrote nothing: the chain is still stale

        var after = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(1, after.Enqueued);
        Assert.Equal(request, Assert.Single(DrainQueue())); // attempt 1 processed

        // The in-flight regeneration was not the backstop's: it gives up only after MaxAttempts
        // of its own.
        for (var attempt = 2; attempt <= WebpDirtyCellBackstop.MaxAttempts; attempt++)
        {
            Assert.Equal(1, (await _backstop.RunAsync(_tenantId, DateTime.UtcNow)).Enqueued);
            Assert.Single(DrainQueue());
        }

        var last = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);
        Assert.Equal(1, last.GaveUp);
        Assert.Equal(0, last.Enqueued);
    }

    [Fact]
    public async Task QueueAtHeadroom_MarkIsDeferredAndKept_EnqueuedOnTheNextRun()
    {
        for (var i = 0; i < ZoomTileQueueService.BulkEnqueueDepthLimit; i++)
        {
            _queue.EnqueueZoomRegeneration(new ZoomTileRequest("filler", 99, i, 0));
        }

        SeedZoom0Row(1, 1);
        await MarkAsync(new Coord(1, 1));
        await SetMarkedAtAsync(DateTime.UtcNow.AddMinutes(-10));

        var deferredRun = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(1, deferredRun.Deferred);
        Assert.Equal(0, deferredRun.Enqueued);
        Assert.Single(await MarksAsync());

        DrainQueue();
        var nextRun = await _backstop.RunAsync(_tenantId, DateTime.UtcNow);

        Assert.Equal(1, nextRun.Enqueued);
    }

    [Fact]
    public async Task SeedFromLegacyDirtyRows_FloorsNegatives_TakesNewestMark_IsIdempotent()
    {
        var t1 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(1);
        var t3 = t1.AddHours(2);

        void Legacy(int zoom, int x, int y, DateTime createdAt) => _db.DirtyZoomTiles.Add(new DirtyZoomTileEntity
        {
            TenantId = _tenantId, MapId = MapId, Zoom = zoom, CoordX = x, CoordY = y, CreatedAt = createdAt
        });

        Legacy(1, -1, -1, t1);  // cell (-1,-1)
        Legacy(1, -3, 2, t1);   // cell (-2, 1) — already marked below, must not move
        Legacy(1, 2, 3, t1);    // cell (1, 1) ...
        Legacy(1, 3, 3, t2);    // ... newest mark wins
        Legacy(2, 9, 9, t1);    // zoom 2: not a source
        _db.DirtyWebpCells.Add(new DirtyWebpCellEntity
        {
            TenantId = _tenantId, MapId = MapId, CellX = -2, CellY = 1, MarkedAt = t3
        });
        await _db.SaveChangesAsync();

        var inserted = await _backstop.SeedFromLegacyDirtyRowsAsync();
        var again = await _backstop.SeedFromLegacyDirtyRowsAsync();

        Assert.Equal(2, inserted);
        Assert.Equal(0, again);
        var marks = (await MarksAsync()).ToDictionary(m => (m.CellX, m.CellY), m => m.MarkedAt);
        Assert.Equal(3, marks.Count);
        Assert.Equal(t1, marks[(-1, -1)]);
        Assert.Equal(t3, marks[(-2, 1)]);
        Assert.Equal(t2, marks[(1, 1)]);
    }

    [Fact]
    public async Task DeletingTheMap_CascadesItsMarks()
    {
        await MarkAsync(new Coord(1, 1), new Coord(40, 40));
        Assert.Equal(2, (await MarksAsync()).Count);

        await _db.Maps.IgnoreQueryFilters().Where(m => m.Id == MapId).ExecuteDeleteAsync();

        Assert.Empty(await MarksAsync());
    }
}
