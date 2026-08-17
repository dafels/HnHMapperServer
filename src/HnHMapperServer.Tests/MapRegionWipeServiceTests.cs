using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Tests for the superadmin map-region wipe (the repair tool for coordinate-corrupted areas).
/// Runs against real SQLite (temp file) because the service uses ExecuteDelete and relies on
/// FK cascade (TimerWarnings from Timers) — neither works on the EF InMemory provider. The
/// DbContext is deliberately built WITHOUT an IHttpContextAccessor, so the ambient tenant filter
/// resolves to null exactly like a superadmin request: every query must carry its own explicit
/// TenantId, which is what these tests prove.
/// </summary>
public class MapRegionWipeServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _gridStorage;
    private readonly ApplicationDbContext _db;
    private readonly MapRegionWipeService _service;

    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string TimerUserId = "timer-user-1";

    public MapRegionWipeServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-wipe-test-{Guid.NewGuid():N}.db");
        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-wipe-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        _db.Tenants.AddRange(
            new HnHMapperServer.Core.Models.TenantEntity
            {
                Id = TenantA, Name = TenantA, StorageQuotaMB = 1024, CurrentStorageMB = 999,
                CreatedAt = DateTime.UtcNow, IsActive = true
            },
            new HnHMapperServer.Core.Models.TenantEntity
            {
                Id = TenantB, Name = TenantB, StorageQuotaMB = 1024, CurrentStorageMB = 0,
                CreatedAt = DateTime.UtcNow, IsActive = true
            });
        // Timers carry an FK to AspNetUsers — seed the owning user once.
        _db.Users.Add(new HnHMapperServer.Infrastructure.Identity.ApplicationUser
        {
            Id = TimerUserId,
            UserName = "wiper",
            NormalizedUserName = "WIPER"
        });
        _db.SaveChanges();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GridStorage"] = _gridStorage })
            .Build();

        _service = new MapRegionWipeService(
            _db,
            new StorageQuotaService(_db, Mock.Of<ILogger<StorageQuotaService>>()),
            configuration,
            Mock.Of<ILogger<MapRegionWipeService>>());
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
        try { Directory.Delete(_gridStorage, recursive: true); }
        catch (Exception e) when (e is IOException or DirectoryNotFoundException) { }
    }

    // ---------- seeding helpers ----------

    private void SeedMap(int mapId, string tenantId)
    {
        _db.Maps.Add(new MapInfoEntity { Id = mapId, Name = $"Map{mapId}", TenantId = tenantId, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    private void SeedGrid(string id, int mapId, string tenantId, int x, int y)
    {
        _db.Grids.Add(new GridDataEntity
        {
            Id = id, Map = mapId, CoordX = x, CoordY = y,
            NextUpdate = DateTime.UtcNow, TenantId = tenantId
        });
        _db.SaveChanges();
    }

    private int SeedMarker(string gridId, string tenantId, string name = "marker")
    {
        var marker = new MarkerEntity
        {
            Key = Guid.NewGuid().ToString("N"), Name = name, GridId = gridId,
            PositionX = 50, PositionY = 50, Image = "gfx/terobjs/mm/custom",
            TenantId = tenantId
        };
        _db.Markers.Add(marker);
        _db.SaveChanges();
        return marker.Id;
    }

    private int SeedMarkerTimer(int markerId, string tenantId, bool withWarning = false)
    {
        var timer = new TimerEntity
        {
            TenantId = tenantId, UserId = TimerUserId, Type = "Marker", MarkerId = markerId,
            Title = "timer", ReadyAt = DateTime.UtcNow.AddHours(1), CreatedAt = DateTime.UtcNow
        };
        _db.Timers.Add(timer);
        _db.SaveChanges();
        if (withWarning)
        {
            _db.TimerWarnings.Add(new TimerWarningEntity { TimerId = timer.Id, WarningMinutes = 60 });
            _db.SaveChanges();
        }
        return timer.Id;
    }

    private string SeedTile(int mapId, string tenantId, int x, int y, int zoom, int fileBytes = 0)
    {
        var relative = Path.Combine("tenants", tenantId, mapId.ToString(), zoom.ToString(), $"{x}_{y}.png");
        if (fileBytes > 0)
        {
            var full = Path.Combine(_gridStorage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[fileBytes]);
        }
        _db.Tiles.Add(new TileDataEntity
        {
            MapId = mapId, CoordX = x, CoordY = y, Zoom = zoom,
            File = relative, Cache = 1, TenantId = tenantId, FileSizeBytes = fileBytes
        });
        _db.SaveChanges();
        return relative;
    }

    private void SeedOverlay(int mapId, string tenantId, int x, int y)
    {
        _db.OverlayData.Add(new OverlayDataEntity
        {
            MapId = mapId, CoordX = x, CoordY = y, OverlayType = "ClaimFloor",
            Data = new byte[] { 1 }, TenantId = tenantId, UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private void ClearTracker() => _db.ChangeTracker.Clear();

    // ---------- tests ----------

    [Fact]
    public async Task WipeAsync_DeletesGridsMarkersTilesOverlaysInsideBoxOnly()
    {
        SeedMap(1, TenantA);
        SeedGrid("in1", 1, TenantA, 10, 10);
        SeedGrid("in2", 1, TenantA, 11, 10);
        SeedGrid("in3", 1, TenantA, 10, 11);
        SeedGrid("out1", 1, TenantA, 50, 50);
        SeedMarker("in1", TenantA, "inside");
        SeedMarker("out1", TenantA, "outside");
        SeedTile(1, TenantA, 10, 10, zoom: 0);
        SeedTile(1, TenantA, 50, 50, zoom: 0);
        SeedOverlay(1, TenantA, 10, 10);
        SeedOverlay(1, TenantA, 50, 50);
        ClearTracker();

        var result = await _service.WipeAsync(TenantA, 1, 10, 10, 11, 11);

        Assert.Equal(3, result.Grids);
        Assert.Equal(1, result.Markers);
        Assert.Equal(1, result.Zoom0Tiles);
        Assert.Equal(1, result.Overlays);

        var remainingGrids = await _db.Grids.IgnoreQueryFilters()
            .Where(g => g.TenantId == TenantA).Select(g => g.Id).ToListAsync();
        Assert.Equal(new[] { "out1" }, remainingGrids);

        var remainingMarkers = await _db.Markers.IgnoreQueryFilters()
            .Where(m => m.TenantId == TenantA).Select(m => m.Name).ToListAsync();
        Assert.Equal(new[] { "outside" }, remainingMarkers);

        Assert.Single(await _db.Tiles.IgnoreQueryFilters()
            .Where(t => t.TenantId == TenantA && t.Zoom == 0).ToListAsync());
        Assert.Single(await _db.OverlayData.IgnoreQueryFilters()
            .Where(o => o.TenantId == TenantA).ToListAsync());
    }

    [Fact]
    public async Task WipeAsync_LeavesZoom1To6TileRowsInPlace()
    {
        SeedMap(1, TenantA);
        SeedGrid("in1", 1, TenantA, 10, 10);
        SeedTile(1, TenantA, 10, 10, zoom: 0);
        SeedTile(1, TenantA, 10, 10, zoom: 3); // same coords, higher zoom — must survive
        ClearTracker();

        var result = await _service.WipeAsync(TenantA, 1, 10, 10, 11, 11);

        Assert.Equal(1, result.Zoom0Tiles);
        var zoom3 = await _db.Tiles.IgnoreQueryFilters()
            .SingleAsync(t => t.TenantId == TenantA && t.MapId == 1 && t.Zoom == 3);
        Assert.Equal(10, zoom3.CoordX);
    }

    [Fact]
    public async Task WipeAsync_DeletesZoom0TileFilesBestEffort_AndReportsCounts()
    {
        SeedMap(1, TenantA);
        SeedGrid("in1", 1, TenantA, 10, 10);
        SeedGrid("in2", 1, TenantA, 11, 10);
        var present = SeedTile(1, TenantA, 10, 10, zoom: 0, fileBytes: 2048);
        var missing = SeedTile(1, TenantA, 11, 10, zoom: 0, fileBytes: 1024);
        File.Delete(Path.Combine(_gridStorage, missing)); // simulate an already-gone file
        ClearTracker();

        var result = await _service.WipeAsync(TenantA, 1, 10, 10, 11, 11);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(2048, result.BytesFreed);
        Assert.False(File.Exists(Path.Combine(_gridStorage, present)));
        Assert.Single(result.Warnings); // the missing file is a warning, never an exception
        Assert.Contains("already missing", result.Warnings[0]);
    }

    [Fact]
    public async Task WipeAsync_LeavesOtherTenantsAndOtherMapsUntouched()
    {
        SeedMap(1, TenantA);
        SeedMap(2, TenantA);
        SeedMap(3, TenantB);
        SeedGrid("target", 1, TenantA, 10, 10);
        SeedGrid("othermap", 2, TenantA, 10, 10);  // same coords, different map
        SeedGrid("othertenant", 3, TenantB, 10, 10); // same coords, different tenant
        SeedOverlay(3, TenantB, 10, 10);
        ClearTracker();

        var result = await _service.WipeAsync(TenantA, 1, 10, 10, 11, 11);

        Assert.Equal(1, result.Grids);
        Assert.NotNull(await _db.Grids.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == "othermap" && g.TenantId == TenantA));
        Assert.NotNull(await _db.Grids.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == "othertenant" && g.TenantId == TenantB));
        Assert.Single(await _db.OverlayData.IgnoreQueryFilters()
            .Where(o => o.TenantId == TenantB).ToListAsync());
    }

    [Fact]
    public async Task WipeAsync_DeletesTimersAttachedToDeletedMarkers()
    {
        SeedMap(1, TenantA);
        SeedGrid("in1", 1, TenantA, 10, 10);
        SeedGrid("out1", 1, TenantA, 50, 50);
        var inMarker = SeedMarker("in1", TenantA);
        var outMarker = SeedMarker("out1", TenantA);
        var inTimer = SeedMarkerTimer(inMarker, TenantA, withWarning: true);
        var outTimer = SeedMarkerTimer(outMarker, TenantA);
        ClearTracker();

        var result = await _service.WipeAsync(TenantA, 1, 10, 10, 11, 11);

        Assert.Equal(1, result.Timers);
        Assert.Null(await _db.Timers.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == inTimer));
        Assert.NotNull(await _db.Timers.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == outTimer));
        // TimerWarnings cascade from the deleted timer
        Assert.Empty(await _db.TimerWarnings.Where(w => w.TimerId == inTimer).ToListAsync());
    }

    [Fact]
    public async Task WipeAsync_RecalculatesStorageUsage()
    {
        SeedMap(1, TenantA);
        SeedGrid("in1", 1, TenantA, 10, 10);
        SeedTile(1, TenantA, 10, 10, zoom: 0, fileBytes: 4096);
        ClearTracker();

        await _service.WipeAsync(TenantA, 1, 10, 10, 11, 11);

        // Seeded at a fake 999 MB; the recalc rewrites it from what is actually left on disk.
        var tenant = await _db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == TenantA);
        Assert.True(tenant.CurrentStorageMB < 999,
            $"CurrentStorageMB should have been recalculated, was {tenant.CurrentStorageMB}");
    }

    [Fact]
    public async Task PreviewAsync_ReturnsCountsExtentAndPercentWithoutDeleting()
    {
        SeedMap(1, TenantA);
        SeedGrid("in1", 1, TenantA, 10, 10);
        SeedGrid("in2", 1, TenantA, 11, 11);
        SeedGrid("out1", 1, TenantA, 50, 50);
        SeedGrid("out2", 1, TenantA, -5, -5);
        SeedMarker("in1", TenantA);
        SeedTile(1, TenantA, 10, 10, zoom: 0);
        SeedOverlay(1, TenantA, 11, 11);
        ClearTracker();

        var preview = await _service.PreviewAsync(TenantA, 1, 10, 10, 11, 11);

        Assert.Equal(2, preview.Grids);
        Assert.Equal(1, preview.Markers);
        Assert.Equal(1, preview.Zoom0Tiles);
        Assert.Equal(1, preview.Overlays);
        Assert.Equal(4, preview.MapTotalGrids);
        Assert.Equal(50.0, preview.PercentOfMap);
        Assert.Equal(-5, preview.MapExtentMinX);
        Assert.Equal(50, preview.MapExtentMaxX);
        Assert.Equal(-5, preview.MapExtentMinY);
        Assert.Equal(50, preview.MapExtentMaxY);

        // Nothing was deleted
        Assert.Equal(4, await _db.Grids.IgnoreQueryFilters().CountAsync(g => g.TenantId == TenantA));
        Assert.Equal(1, await _db.Markers.IgnoreQueryFilters().CountAsync(m => m.TenantId == TenantA));
    }

    [Fact]
    public async Task PreviewAsync_NormalizesReversedCoordinates()
    {
        SeedMap(1, TenantA);
        SeedGrid("in1", 1, TenantA, 10, 10);
        SeedGrid("in2", 1, TenantA, 11, 11);
        ClearTracker();

        var preview = await _service.PreviewAsync(TenantA, 1, 11, 11, 10, 10); // reversed corners

        Assert.Equal(2, preview.Grids);
        Assert.Equal(10, preview.X1);
        Assert.Equal(11, preview.X2);
        Assert.Equal(10, preview.Y1);
        Assert.Equal(11, preview.Y2);
    }

    [Fact]
    public async Task WipeAsync_ThrowsForUnknownMapOrTenant()
    {
        SeedMap(1, TenantA);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.WipeAsync("no-such-tenant", 1, 0, 0, 1, 1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.WipeAsync(TenantA, 999, 0, 0, 1, 1));
        // Map 1 belongs to TenantA — TenantB must not be able to wipe it
        SeedMap(3, TenantB);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.WipeAsync(TenantB, 1, 0, 0, 1, 1));
    }

    [Fact]
    public async Task WipeAsync_ChunksMarkerDeletionOverManyGrids()
    {
        // >500 in-box grids exercises the 500-per-IN-clause chunking for marker lookup/deletion.
        SeedMap(1, TenantA);
        for (var i = 0; i < 501; i++)
        {
            var x = i % 30;
            var y = i / 30;
            _db.Grids.Add(new GridDataEntity
            {
                Id = $"g{i}", Map = 1, CoordX = x, CoordY = y,
                NextUpdate = DateTime.UtcNow, TenantId = TenantA
            });
            _db.Markers.Add(new MarkerEntity
            {
                Key = $"k{i}", Name = $"m{i}", GridId = $"g{i}",
                PositionX = 1, PositionY = 1, Image = "gfx/terobjs/mm/custom", TenantId = TenantA
            });
        }
        _db.SaveChanges();
        ClearTracker();

        var result = await _service.WipeAsync(TenantA, 1, 0, 0, 29, 29);

        Assert.Equal(501, result.Grids);
        Assert.Equal(501, result.Markers);
        Assert.Equal(0, await _db.Markers.IgnoreQueryFilters().CountAsync(m => m.TenantId == TenantA));
    }
}
