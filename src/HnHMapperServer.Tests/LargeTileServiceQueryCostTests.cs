using System.Data.Common;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace HnHMapperServer.Tests;

/// <summary>
/// Pins what a WebP tile request costs in database round trips — without the footprint check an
/// empty zoom-6 tile recursed into 4,096 zoom-0 queries — and the pyramid-bounds
/// guard: a zoom outside 0..6, or x/y whose footprint overflows int, must cost nothing and write
/// nothing (the footprint maths wraps there: zoom -4 spans a whole quadrant, z6 x=2^24 aliases
/// the data at x=0 and writes a duplicate pyramid).
///
/// Real SQLite on a kept-open ":memory:" connection with a command-counting interceptor; a unique
/// tenant per test because LargeTileService's caches are process-static.
/// </summary>
public class LargeTileServiceQueryCostTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly LargeTileService _service;
    private readonly QueryCounter _queries = new();
    private readonly string _gridStorage;
    private readonly string _tenantId = $"cost-{Guid.NewGuid():N}";

    public LargeTileServiceQueryCostTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_queries)
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        _db.Tenants.Add(new TenantEntity
        {
            Id = _tenantId, Name = _tenantId, StorageQuotaMB = 1024, CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow, IsActive = true
        });
        _db.SaveChanges();

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-largetile-cost-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GridStorage"] = _gridStorage })
            .Build();

        _service = new LargeTileService(_db, configuration, Mock.Of<ILogger<LargeTileService>>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    /// <summary>Counts every command EF sends (seeding included, so tests Reset() first).</summary>
    private sealed class QueryCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private void SeedZoom0RowWithPng(int mapId, int x, int y)
    {
        var relative = Path.Combine("tenants", _tenantId, "grids", $"g{mapId}_{x}_{y}.png");
        var full = Path.Combine(_gridStorage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var img = new Image<Rgba32>(100, 100, new Rgba32(200, 30, 30, 255)))
        {
            img.SaveAsPng(full);
        }

        _db.Tiles.Add(new TileDataEntity
        {
            MapId = mapId, CoordX = x, CoordY = y, Zoom = 0,
            File = relative, Cache = 1, TenantId = _tenantId, FileSizeBytes = 1
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private static byte[] TinyWebp()
    {
        using var img = new Image<Rgba32>(4, 4, new Rgba32(10, 120, 200, 255));
        using var ms = new MemoryStream();
        img.SaveAsWebp(ms, new WebpEncoder());
        return ms.ToArray();
    }

    private string LargeDir(int mapId) => Path.Combine(_gridStorage, "tenants", _tenantId, "large", mapId.ToString());

    [Fact]
    public async Task EmptyZoom6Tile_CostsOneQuery_ThenNone()
    {
        _queries.Reset();
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(_tenantId, 1, 6, 0, 0));
        Assert.Equal(1, _queries.Count); // the footprint EXISTS (without it: 4,096 zoom-0 queries)

        _queries.Reset();
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(_tenantId, 1, 6, 0, 0));
        Assert.Equal(0, _queries.Count); // negative cache
    }

    [Fact]
    public async Task Zoom6TileOnDisk_CostsNoQuery()
    {
        var bytes = TinyWebp();
        var path = _service.GetLargeTilePath(_tenantId, 2, 6, 0, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        _queries.Reset();
        Assert.Equal(bytes, await _service.GetOrGenerateLargeTileAsync(_tenantId, 2, 6, 0, 0));
        Assert.Equal(0, _queries.Count);
    }

    [Fact]
    public async Task SparseZoom6Tile_ColdBuild_QueriesOnlyAlongPopulatedBranch()
    {
        const int mapId = 3;
        SeedZoom0RowWithPng(mapId, 0, 0);

        // z6 footprint (1) + at each of z5..z1 one populated child and three empty footprints
        // (5 x 4) + the four zoom-0 cells under z1 (0,0) (4): 1 + 20 + 4 = 25.
        _queries.Reset();
        Assert.NotNull(await _service.GetOrGenerateLargeTileAsync(_tenantId, mapId, 6, 0, 0));
        Assert.Equal(25, _queries.Count);
        for (var z = 0; z <= 6; z++)
        {
            Assert.True(File.Exists(_service.GetLargeTilePath(_tenantId, mapId, z, 0, 0)), $"z{z} (0,0) not written");
        }

        // Built tiles come back from memory, or from disk if a parallel test class evicted them.
        _queries.Reset();
        Assert.NotNull(await _service.GetOrGenerateLargeTileAsync(_tenantId, mapId, 5, 0, 0));
        Assert.Equal(0, _queries.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-4)]  // without the guard: 1 + 4 x 29 populated levels = 117 queries
    [InlineData(7)]   // without the guard: writes large/{map}/7/0_0.webp
    [InlineData(36)]  // `4 << 36` == `4 << 4`: a zoom-4 alias that bypasses the negative cache
    public async Task ZoomOutsidePyramid_ReturnsNull_WithoutQueriesOrFiles(int zoom)
    {
        const int mapId = 4;
        SeedZoom0RowWithPng(mapId, 0, 0);

        _queries.Reset();
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(_tenantId, mapId, zoom, 0, 0));
        Assert.Equal(0, _queries.Count);
        Assert.False(Directory.Exists(LargeDir(mapId)));
    }

    [Theory]
    [InlineData(6, 1 << 24, 0)] // 2^24 x 256 wraps to 0: would render (and copy) the data at x=0
    [InlineData(6, 0, 1 << 24)]
    [InlineData(0, 1 << 30, 0)]
    public async Task CoordinatesOverflowingTheFootprint_DoNotAliasRealData(int zoom, int x, int y)
    {
        const int mapId = 5;
        SeedZoom0RowWithPng(mapId, 0, 0);

        _queries.Reset();
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(_tenantId, mapId, zoom, x, y));
        Assert.Equal(0, _queries.Count);
        Assert.False(Directory.Exists(LargeDir(mapId)));
    }

    [Fact]
    public async Task ForceRegenerate_OutsidePyramid_ThrowsAndDeletesNothing()
    {
        // Falling through would take the "no sources" branch, which deletes the file at the path.
        var path = _service.GetLargeTilePath(_tenantId, 6, -1, 0, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TinyWebp());

        _queries.Reset();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.ForceRegenerateLargeTileAsync(_tenantId, 6, -1, 0, 0));
        Assert.True(File.Exists(path));
        Assert.Equal(0, _queries.Count);
    }

    [Fact]
    public async Task LargestInRangeCoordinates_AreServedNormally()
    {
        // The guard's edge: the last z6 column whose footprint still fits in int is a tile like
        // any other (empty here: one footprint query, no throw, no overflow).
        var lastX = int.MaxValue / 256 - 1; // (x + 1) * 256 <= int.MaxValue
        _queries.Reset();
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(_tenantId, 7, 6, lastX, -(1 << 23)));
        Assert.Equal(1, _queries.Count);
    }
}
