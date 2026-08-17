using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HnHMapperServer.Tests;

/// <summary>
/// Occupied-cell guard tests for the PUBLIC-map-to-tenant import: a grid row is never planted on
/// a cell already owned by a different grid id (the tile is still written — tiles are
/// coord-keyed). Uses a kept-open SQLite ":memory:" connection and a real on-disk PUBLIC
/// snapshot (400x400 WebP + PublicMapGridIndex rows); zoom generation and the global import lock
/// come from a mocked IHmapImportService.
/// </summary>
public class PublicMapTenantImportOccupiedCellTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly string _gridStorage;
    private readonly PublicMapTenantImportService _service;
    private readonly string _publicMapId = IPublicMapTenantImportService.PreferredPublicMapId;

    private const string TestTenantId = "default-tenant-1";

    public PublicMapTenantImportOccupiedCellTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TestTenantId;
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new ApplicationDbContext(options, accessor.Object);
        _db.Database.EnsureCreated();

        _db.Tenants.Add(new HnHMapperServer.Core.Models.TenantEntity
        {
            Id = TestTenantId,
            Name = TestTenantId,
            StorageQuotaMB = 1024,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
        _db.PublicMaps.Add(new PublicMapEntity
        {
            Id = _publicMapId,
            Name = "Public",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-pubimport-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        var mockQuota = new Mock<IStorageQuotaService>();
        mockQuota.Setup(x => x.CheckQuotaAsync(It.IsAny<string>(), It.IsAny<double>())).ReturnsAsync(true);

        var mockMapName = new Mock<IMapNameService>();
        mockMapName.Setup(x => x.GenerateUniqueIdentifierAsync(It.IsAny<string>()))
            .ReturnsAsync(() => $"public-import-{Guid.NewGuid():N}");

        // Real zoom generation is not under test here; the lock must simply be grantable.
        var mockHmapImport = new Mock<IHmapImportService>();
        mockHmapImport.Setup(x => x.TryAcquireGlobalImportLockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _service = new PublicMapTenantImportService(
            _db,
            mockMapName.Object,
            mockQuota.Object,
            mockHmapImport.Object,
            Mock.Of<IUpdateNotificationService>(),
            new TenantFilePathService(),
            Mock.Of<ILogger<PublicMapTenantImportService>>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_gridStorage)) Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    /// <summary>Writes an opaque 400x400 WebP at {gridStorage}/public/{id}/0/{px}_{py}.webp.</summary>
    private async Task WritePublicWebpAsync(int px, int py)
    {
        var dir = Path.Combine(_gridStorage, "public", _publicMapId, "0");
        Directory.CreateDirectory(dir);
        using var img = new Image<Rgba32>(400, 400);
        img.Mutate(ctx => ctx.BackgroundColor(new Rgba32(120, 160, 90, 255)));
        await img.SaveAsWebpAsync(Path.Combine(dir, $"{px}_{py}.webp"));
    }

    private void SeedIndex(params (int X, int Y, string GridId)[] entries)
    {
        foreach (var (x, y, gridId) in entries)
        {
            _db.PublicMapGridIndex.Add(new PublicMapGridIndexEntity
            {
                PublicMapId = _publicMapId,
                UnifiedX = x,
                UnifiedY = y,
                GridId = gridId,
                SnapshotCache = 1,
                IndexedAt = DateTime.UtcNow
            });
        }
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task ImportAsync_DestinationCellOwnedByExistingGrid_SkipsGridRowButWritesTile()
    {
        // Target map 1: five alignment grids at coords identical to the unified coords
        // (delta (0,0)) plus OWNER at (5,0) — a different id than the snapshot's grid for that
        // cell, and deliberately WITHOUT a tile row (the tile-level skip must not mask the guard).
        _db.Maps.Add(new MapInfoEntity { Id = 1, Name = "Target", TenantId = TestTenantId, CreatedAt = DateTime.UtcNow });
        for (int i = 0; i < 5; i++)
        {
            _db.Grids.Add(new GridDataEntity
            {
                Id = $"A{i + 1}",
                Map = 1,
                CoordX = i,
                CoordY = 0,
                NextUpdate = DateTime.UtcNow,
                TenantId = TestTenantId
            });
        }
        _db.Grids.Add(new GridDataEntity
        {
            Id = "OWNER",
            Map = 1,
            CoordX = 5,
            CoordY = 0,
            NextUpdate = DateTime.UtcNow,
            TenantId = TestTenantId
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        SeedIndex((0, 0, "A1"), (1, 0, "A2"), (2, 0, "A3"), (3, 0, "A4"), (4, 0, "A5"), (5, 0, "SNAP_NEW"));
        await WritePublicWebpAsync(0, 0); // covers unified (0..3, 0..3)
        await WritePublicWebpAsync(1, 0); // covers unified (4..7, 0..3)

        var result = await _service.ImportAsync(TestTenantId, targetMapId: 1, _gridStorage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.GridRowsSkippedOccupiedCell);
        Assert.Empty(result.CreatedGridIds);

        // The snapshot's grid id was NOT planted; the owner is untouched
        Assert.Null(await _db.Grids.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == "SNAP_NEW" && g.TenantId == TestTenantId));
        var owner = await _db.Grids.IgnoreQueryFilters()
            .SingleAsync(g => g.Id == "OWNER" && g.TenantId == TestTenantId);
        Assert.Equal(1, owner.Map);
        Assert.Equal(5, owner.CoordX);
        Assert.Equal(0, owner.CoordY);

        // The tile for the contested cell WAS written (tiles are coord-keyed, visually complete)
        Assert.NotNull(await _db.Tiles.IgnoreQueryFilters().FirstOrDefaultAsync(t =>
            t.TenantId == TestTenantId && t.MapId == 1 && t.Zoom == 0 && t.CoordX == 5 && t.CoordY == 0));
    }

    [Fact]
    public async Task ImportAsync_NoCellConflicts_InsertsGridRows()
    {
        SeedIndex((0, 0, "N1"), (1, 0, "N2"));
        await WritePublicWebpAsync(0, 0);

        var result = await _service.ImportAsync(TestTenantId, targetMapId: null, _gridStorage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.CreatedNewMap);
        Assert.Equal(0, result.GridRowsSkippedOccupiedCell);
        Assert.Equal(2, result.CreatedGridIds.Count);

        var n1 = await _db.Grids.IgnoreQueryFilters()
            .SingleAsync(g => g.Id == "N1" && g.TenantId == TestTenantId);
        Assert.Equal(result.TargetMapId, n1.Map);
        Assert.Equal(0, n1.CoordX);
        Assert.Equal(0, n1.CoordY);
    }
}
