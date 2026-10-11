using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using ITenantContextAccessor = HnHMapperServer.Core.Interfaces.ITenantContextAccessor;

namespace HnHMapperServer.Tests;

/// <summary>
/// The shared map-delete teardown (tenant-admin and superadmin). Before it, the tenant-admin
/// delete never removed the WebP pyramid (large/{id}) and deleted every file its rows named even
/// when another map's rows still used it; the superadmin delete removed only the Maps row, leaving
/// grids and tiles on the dead id for the next gridUpdate to re-anchor to.
///
/// Real SQLite on a kept-open ":memory:" connection (ExecuteDelete, foreign keys), a temp
/// GridStorage, and a DbContext WITHOUT an IHttpContextAccessor (no ambient tenant, like a
/// superadmin request) to prove every query carries its explicit tenant.
/// </summary>
public class MapDeletionServiceTests : IDisposable
{
    private const string UserId = "user-1";
    private const int Doomed = 1;
    private const int Survivor = 2;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly string _gridStorage;
    private readonly string _tenantId = $"delete-{Guid.NewGuid():N}";
    private readonly IConfiguration _configuration;
    private readonly MapDeletionService _service;

    public MapDeletionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-mapdelete-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GridStorage"] = _gridStorage })
            .Build();

        var tenantContext = new Mock<ITenantContextAccessor>();
        tenantContext.Setup(x => x.GetCurrentTenantId()).Returns(_tenantId);
        var tileService = new TileService(
            new TileRepository(_db, tenantContext.Object),
            new GridRepository(_db, tenantContext.Object),
            Mock.Of<IUpdateNotificationService>(),
            Mock.Of<IStorageQuotaService>(),
            Mock.Of<ILogger<TileService>>(),
            _db);

        _service = new MapDeletionService(
            _db,
            tileService,
            new LargeTileService(_db, _configuration, Mock.Of<ILogger<LargeTileService>>()),
            new StorageQuotaService(_db, Mock.Of<ILogger<StorageQuotaService>>()),
            _configuration,
            Mock.Of<ILogger<MapDeletionService>>());

        _db.Users.Add(new ApplicationUser { Id = UserId, UserName = "mapper", NormalizedUserName = "MAPPER" });
        _db.Tenants.Add(new TenantEntity
        {
            Id = _tenantId, Name = _tenantId, StorageQuotaMB = 1024, CurrentStorageMB = 999,
            CreatedAt = DateTime.UtcNow, IsActive = true
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private string Rel(params string[] segments) => Path.Combine(new[] { "tenants", _tenantId }.Concat(segments).ToArray());

    private string Full(string relative) => Path.Combine(_gridStorage, relative);

    private string WriteFile(string relative, int size = 100)
    {
        var full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
        return full;
    }

    private void SeedMap(int mapId)
    {
        _db.Maps.Add(new MapInfoEntity { Id = mapId, Name = $"map-{mapId}", TenantId = _tenantId, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    private void SeedGrid(string id, int mapId, int x, int y)
    {
        _db.Grids.Add(new GridDataEntity
        {
            Id = id, Map = mapId, CoordX = x, CoordY = y, NextUpdate = DateTime.UtcNow, TenantId = _tenantId
        });
        _db.SaveChanges();
    }

    private void SeedTile(int mapId, int x, int y, int zoom, string file)
    {
        _db.Tiles.Add(new TileDataEntity
        {
            MapId = mapId, CoordX = x, CoordY = y, Zoom = zoom, File = file, Cache = 1,
            TenantId = _tenantId, FileSizeBytes = 100
        });
        _db.SaveChanges();
    }

    /// <summary>
    /// Doomed map 1: two grids (g1 with a pool PNG, g2 imported into the per-map dir), a legacy
    /// zoom-1 tile, a row sharing survivor map 2's pool PNG, every kind of map content, dirty
    /// marks, and a WebP pyramid. Survivor map 2: one grid whose pool PNG map 1 also uses.
    /// </summary>
    private void SeedDoomedAndSurvivor()
    {
        var now = DateTime.UtcNow;
        SeedMap(Doomed);
        SeedMap(Survivor);

        SeedGrid("g1", Doomed, 0, 0);
        SeedGrid("g2", Doomed, 1, 0);
        SeedTile(Doomed, 0, 0, 0, Rel("grids", "g1.png"));
        WriteFile(Rel("grids", "g1.png"));
        SeedTile(Doomed, 1, 0, 0, Rel("1", "0", "1_0.png"));
        WriteFile(Rel("1", "0", "1_0.png"));
        SeedTile(Doomed, 0, 0, 1, Rel("1", "1", "0_0.png"));
        WriteFile(Rel("1", "1", "0_0.png"));
        SeedTile(Doomed, 2, 0, 0, Rel("grids", "h1.png")); // merge twin of the survivor's row
        WriteFile(Rel("large", "1", "0", "0_0.webp"));
        WriteFile(Rel("large", "1", "6", "0_0.webp"));

        SeedGrid("h1", Survivor, 0, 0);
        SeedTile(Survivor, 0, 0, 0, Rel("grids", "h1.png"));
        WriteFile(Rel("grids", "h1.png"));
        WriteFile(Rel("large", "2", "0", "0_0.webp"));

        _db.Markers.Add(new MarkerEntity { Key = "k-g1", Name = "marker", GridId = "g1", Image = "gfx/m", TenantId = _tenantId });
        _db.Markers.Add(new MarkerEntity { Key = "k-h1", Name = "keep", GridId = "h1", Image = "gfx/m", TenantId = _tenantId });
        _db.CustomMarkers.Add(new CustomMarkerEntity
        {
            MapId = Doomed, GridId = "g1", CoordX = 0, CoordY = 0, X = 1, Y = 1, Title = "c", Icon = "gfx/i",
            CreatedBy = UserId, PlacedAt = now, UpdatedAt = now, TenantId = _tenantId
        });
        _db.Roads.Add(new RoadEntity
        {
            MapId = Doomed, Name = "road", Waypoints = "[]", CreatedBy = UserId, CreatedAt = now, UpdatedAt = now,
            TenantId = _tenantId
        });
        _db.Pings.Add(new PingEntity
        {
            MapId = Doomed, CoordX = 0, CoordY = 0, X = 1, Y = 1, CreatedBy = UserId, CreatedAt = now,
            ExpiresAt = now.AddMinutes(1), TenantId = _tenantId
        });
        _db.OverlayData.Add(new OverlayDataEntity
        {
            MapId = Doomed, CoordX = 0, CoordY = 0, OverlayType = "cave", Data = new byte[] { 1 },
            TenantId = _tenantId, UpdatedAt = now
        });
        _db.OverlayOffsets.Add(new OverlayOffsetEntity
        {
            TenantId = _tenantId, CurrentMapId = Survivor, OverlayMapId = Doomed, OffsetX = 0, OffsetY = 0, UpdatedAt = now
        });
        _db.DirtyZoomTiles.Add(new DirtyZoomTileEntity
        {
            TenantId = _tenantId, MapId = Doomed, CoordX = 0, CoordY = 0, Zoom = 1, CreatedAt = now
        });
        _db.DirtyWebpCells.Add(new DirtyWebpCellEntity
        {
            TenantId = _tenantId, MapId = Doomed, CellX = 0, CellY = 0, MarkedAt = now
        });
        _db.DirtyWebpCells.Add(new DirtyWebpCellEntity
        {
            TenantId = _tenantId, MapId = Survivor, CellX = 0, CellY = 0, MarkedAt = now
        });
        _db.SaveChanges();
    }

    // ---- tests -----------------------------------------------------------------------------

    [Fact]
    public async Task DeleteMap_RemovesRowsMarksFilesAndBothDirectories_RecalculatesQuota()
    {
        SeedDoomedAndSurvivor();

        var result = await _service.DeleteMapAsync(_tenantId, Doomed);

        Assert.Equal("map-1", result.MapName);
        Assert.Equal(2, result.Grids);
        Assert.Equal(4, result.Tiles);
        Assert.Equal(1, result.Markers);
        Assert.Equal(1, result.CustomMarkers);
        Assert.Equal(1, result.Roads);
        Assert.Equal(1, result.Pings);
        Assert.Equal(2, result.Overlays);
        Assert.Equal(2, result.DirtyMarks);
        Assert.Equal(2, result.WebpFilesDeleted);
        Assert.Equal(1, result.FilesKeptShared); // h1.png, still the survivor's tile

        Assert.False(await _db.Maps.IgnoreQueryFilters().AnyAsync(m => m.Id == Doomed));
        Assert.False(await _db.Grids.IgnoreQueryFilters().AnyAsync(g => g.Map == Doomed));
        Assert.False(await _db.Tiles.IgnoreQueryFilters().AnyAsync(t => t.MapId == Doomed));
        Assert.False(await _db.DirtyWebpCells.AnyAsync(d => d.MapId == Doomed));
        Assert.False(await _db.DirtyZoomTiles.AnyAsync(d => d.MapId == Doomed));
        Assert.False(await _db.OverlayOffsets.IgnoreQueryFilters().AnyAsync(o => o.OverlayMapId == Doomed));

        Assert.False(File.Exists(Full(Rel("grids", "g1.png"))));
        Assert.False(Directory.Exists(Full(Rel("1"))));
        Assert.False(Directory.Exists(Full(Rel("large", "1"))));

        // The survivor is untouched.
        Assert.True(File.Exists(Full(Rel("grids", "h1.png"))));
        Assert.True(File.Exists(Full(Rel("large", "2", "0", "0_0.webp"))));
        Assert.True(await _db.Markers.IgnoreQueryFilters().AnyAsync(m => m.GridId == "h1"));
        Assert.True(await _db.DirtyWebpCells.AnyAsync(d => d.MapId == Survivor));

        // Quota rewritten from what is left: h1.png (100 B) + the survivor's webp (100 B).
        var tenant = await _db.Tenants.AsNoTracking().SingleAsync(t => t.Id == _tenantId);
        Assert.True(tenant.CurrentStorageMB < 0.001, $"quota not recalculated: {tenant.CurrentStorageMB} MB");
    }

    [Fact]
    public async Task DeleteMap_KeepsPerMapDirectoryAndFilesAnotherLiveMapStillReferences()
    {
        // A merge before the re-home fix left the survivor pointing into map 1's directory (stored
        // with the other separator: rows carry the separator of the OS that wrote them).
        SeedMap(Doomed);
        SeedMap(Survivor);
        SeedTile(Doomed, 0, 0, 0, Rel("1", "0", "0_0.png"));
        WriteFile(Rel("1", "0", "0_0.png"));
        var otherSeparator = Path.DirectorySeparatorChar == '\\' ? '/' : '\\';
        SeedTile(Survivor, 5, 5, 0, Rel("1", "0", "5_5.png").Replace(Path.DirectorySeparatorChar, otherSeparator));
        WriteFile(Rel("1", "0", "5_5.png"));

        var result = await _service.DeleteMapAsync(_tenantId, Doomed);

        Assert.False(File.Exists(Full(Rel("1", "0", "0_0.png"))), "the doomed map's own file goes");
        Assert.True(File.Exists(Full(Rel("1", "0", "5_5.png"))), "a live row's file must survive");
        Assert.Contains(result.Warnings, w => w.Contains("Kept directory of map 1"));
    }

    [Fact]
    public async Task DeleteMap_LeavesNoOrphansForTheIntegrityScan()
    {
        SeedDoomedAndSurvivor();

        await _service.DeleteMapAsync(_tenantId, Doomed);

        var report = await new MapIntegrityService(
                _db,
                new StorageQuotaService(_db, Mock.Of<ILogger<StorageQuotaService>>()),
                _configuration,
                Mock.Of<ILogger<MapIntegrityService>>())
            .ScanAsync();

        Assert.DoesNotContain(report.OrphanStorage, o => o.TenantId == _tenantId);
    }

    [Fact]
    public async Task DeleteMap_UnknownMapOrOtherTenantsMap_Throws()
    {
        SeedMap(Doomed);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.DeleteMapAsync(_tenantId, 404));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.DeleteMapAsync("someone-else", Doomed));
        Assert.True(await _db.Maps.IgnoreQueryFilters().AnyAsync(m => m.Id == Doomed));
    }

    [Fact]
    public async Task DeleteMap_PathDetours_NeverReachAFileALiveMapStillUses()
    {
        // A legacy path-like grid id (gridUpdate refuses them now) and a row whose stored path
        // takes the same detour: both name the survivor's imported file tenants/t/2/0/0_0.png,
        // which a raw string compare never matched against the survivor's row.
        SeedMap(Doomed);
        SeedMap(Survivor);
        SeedGrid("../2/0/0_0", Doomed, 0, 0);
        SeedTile(Doomed, 1, 0, 0, Rel("grids", "..", "2", "0", "0_0.png"));
        SeedTile(Survivor, 0, 0, 0, Rel("2", "0", "0_0.png"));
        var shared = WriteFile(Rel("2", "0", "0_0.png"));

        var result = await _service.DeleteMapAsync(_tenantId, Doomed);

        Assert.True(File.Exists(shared));
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(1, result.FilesKeptShared);
    }
}
