using System.Data.Common;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// GET /map/api/v1/markers runs on every /map marker poll. It used to load the markers and then look
/// up each marker's grid separately (thousands of queries per call, past the Web's 10 s timeout under
/// load). GetFrontendMarkersAsync must answer in ONE query with the same result.
/// Real SQLite, ambient tenant set through HttpContext.Items like a real request.
/// </summary>
public class MarkerRepositoryFrontendMarkersTests : IDisposable
{
    private const string TenantId = "marker-tenant";
    private const string OtherTenantId = "other-tenant";

    private readonly string _dbPath;
    private readonly CommandCounter _counter = new();
    private readonly ApplicationDbContext _context;
    private readonly MarkerRepository _repository;

    public MarkerRepositoryFrontendMarkersTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-markerrepo-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .AddInterceptors(_counter)
            .Options;

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TenantId;
        _context = new ApplicationDbContext(options, new HttpContextAccessor { HttpContext = httpContext });
        _context.Database.EnsureCreated();

        foreach (var id in new[] { TenantId, OtherTenantId })
        {
            _context.Tenants.Add(new TenantEntity
            {
                Id = id, Name = id, StorageQuotaMB = 1024, CreatedAt = DateTime.UtcNow, IsActive = true
            });
        }

        // The same grid id exists in both tenants at different coordinates (ids are client hashes).
        _context.Grids.Add(new GridDataEntity { Id = "g1", TenantId = TenantId, Map = 7, CoordX = 3, CoordY = -2 });
        _context.Grids.Add(new GridDataEntity { Id = "g2", TenantId = TenantId, Map = 8, CoordX = 0, CoordY = 0 });
        _context.Grids.Add(new GridDataEntity { Id = "g1", TenantId = OtherTenantId, Map = 99, CoordX = 50, CoordY = 50 });

        _context.Markers.Add(Marker("g1_10_20", "g1", TenantId, 10, 20, "Cave"));
        _context.Markers.Add(Marker("g2_-5_99", "g2", TenantId, -5, 99, "Thingwall"));
        _context.Markers.Add(Marker("gone_1_1", "gone", TenantId, 1, 1, "Orphan"));
        _context.Markers.Add(Marker("g1_1_1", "g1", OtherTenantId, 1, 1, "Foreign"));
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        var tenantContext = new Mock<ITenantContextAccessor>();
        tenantContext.Setup(t => t.GetCurrentTenantId()).Returns(TenantId);
        _repository = new MarkerRepository(_context, tenantContext.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private static MarkerEntity Marker(string key, string gridId, string tenantId, int x, int y, string name) => new()
    {
        Key = key, GridId = gridId, TenantId = tenantId, PositionX = x, PositionY = y, Name = name,
        Image = "gfx/terobjs/mm/" + name.ToLowerInvariant(), MaxReady = -1, MinReady = -1
    };

    [Fact]
    public async Task ResolvesMapAndWorldPosition_SkipsOrphans_StaysInsideTheTenant()
    {
        var (markers, orphaned) = await _repository.GetFrontendMarkersAsync();

        Assert.Equal(1, orphaned);
        Assert.Equal(new[] { "Cave", "Thingwall" }, markers.Select(m => m.Name).ToArray());

        var cave = markers[0];
        Assert.Equal(7, cave.Map);
        Assert.Equal(10 + 3 * 100, cave.Position.X);
        Assert.Equal(20 + -2 * 100, cave.Position.Y);
        Assert.Equal("gfx/terobjs/mm/cave", cave.Image);
        Assert.Equal(-1, cave.MaxReady);

        var thingwall = markers[1];
        Assert.Equal(8, thingwall.Map);
        Assert.Equal(-5, thingwall.Position.X);
        Assert.Equal(99, thingwall.Position.Y);
    }

    [Fact]
    public async Task UsesOneQuery_HoweverManyMarkers()
    {
        for (var i = 0; i < 200; i++)
        {
            _context.Markers.Add(Marker($"g1_bulk_{i}", "g1", TenantId, i % 100, i % 100, $"Bulk{i}"));
        }
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        _counter.Reset();
        var (markers, _) = await _repository.GetFrontendMarkersAsync();

        Assert.Equal(202, markers.Count);
        Assert.Equal(1, _counter.Count);
    }

    [Fact]
    public async Task CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _repository.GetFrontendMarkersAsync(cts.Token));
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;
        public void Reset() => _count = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
