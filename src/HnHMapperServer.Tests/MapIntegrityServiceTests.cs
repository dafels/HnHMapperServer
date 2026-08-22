using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Tests for the superadmin cross-tenant integrity scan (contested cells + placeholder rows).
/// Runs on a kept-open SQLite ":memory:" connection so the GroupBy/HAVING aggregate translates
/// through real SQL; the DbContext is built WITHOUT an IHttpContextAccessor (ambient tenant null,
/// exactly like a superadmin request) to prove every query carries IgnoreQueryFilters.
/// </summary>
public class MapIntegrityServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly MapIntegrityService _service;
    private readonly string _gridStorage;

    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    public MapIntegrityServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-integrity-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        _db.Tenants.AddRange(
            new HnHMapperServer.Core.Models.TenantEntity
            {
                Id = TenantA, Name = "Tenant Alpha", StorageQuotaMB = 1024, CurrentStorageMB = 0,
                CreatedAt = DateTime.UtcNow, IsActive = true
            },
            new HnHMapperServer.Core.Models.TenantEntity
            {
                Id = TenantB, Name = "Tenant Beta", StorageQuotaMB = 1024, CurrentStorageMB = 0,
                CreatedAt = DateTime.UtcNow, IsActive = true
            });
        _db.SaveChanges();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GridStorage"] = _gridStorage })
            .Build();

        _service = new MapIntegrityService(
            _db,
            new StorageQuotaService(_db, Mock.Of<ILogger<StorageQuotaService>>()),
            configuration,
            Mock.Of<ILogger<MapIntegrityService>>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    private void SeedMap(int mapId, string tenantId, string name)
    {
        _db.Maps.Add(new MapInfoEntity { Id = mapId, Name = name, TenantId = tenantId, CreatedAt = DateTime.UtcNow });
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

    [Fact]
    public async Task ScanAsync_FindsContestedCellsWithBboxAndOwners()
    {
        SeedMap(1, TenantA, "Alpha World");
        // Two contested cells (two owners each) + two unique cells
        SeedGrid("a1", 1, TenantA, 10, 10);
        SeedGrid("a2", 1, TenantA, 10, 10); // fights a1
        SeedGrid("b1", 1, TenantA, 15, 12);
        SeedGrid("b2", 1, TenantA, 15, 12); // fights b1
        SeedGrid("clean1", 1, TenantA, 0, 0);
        SeedGrid("clean2", 1, TenantA, 1, 0);

        var report = await _service.ScanAsync();

        var issue = Assert.Single(report.ContestedMaps);
        Assert.Equal(TenantA, issue.TenantId);
        Assert.Equal("Tenant Alpha", issue.TenantName);
        Assert.Equal(1, issue.MapId);
        Assert.Equal("Alpha World", issue.MapName);
        Assert.Equal(2, issue.ContestedCellCount);
        Assert.Equal(10, issue.MinX);
        Assert.Equal(15, issue.MaxX);
        Assert.Equal(10, issue.MinY);
        Assert.Equal(12, issue.MaxY);

        Assert.Equal(2, issue.SampleCells.Count);
        var cell = issue.SampleCells.Single(c => c.X == 10 && c.Y == 10);
        Assert.Equal(new[] { "a1", "a2" }.OrderBy(s => s), cell.GridIds.OrderBy(s => s));
        Assert.False(report.IsClean);
    }

    [Fact]
    public async Task ScanAsync_AttributesIssuesPerTenantAndMap()
    {
        SeedMap(1, TenantA, "Alpha World");
        SeedMap(2, TenantB, "Beta World");
        // Same coordinates contested in both tenants — must be two separate issues
        SeedGrid("a1", 1, TenantA, 5, 5);
        SeedGrid("a2", 1, TenantA, 5, 5);
        SeedGrid("b1", 2, TenantB, 5, 5);
        SeedGrid("b2", 2, TenantB, 5, 5);

        var report = await _service.ScanAsync();

        Assert.Equal(2, report.ContestedMaps.Count);
        Assert.Contains(report.ContestedMaps, i => i.TenantId == TenantA && i.MapId == 1);
        Assert.Contains(report.ContestedMaps, i => i.TenantId == TenantB && i.MapId == 2);
        // Owners never leak across tenants
        var alpha = report.ContestedMaps.Single(i => i.TenantId == TenantA);
        Assert.All(alpha.SampleCells.Single().GridIds, id => Assert.StartsWith("a", id));
    }

    [Fact]
    public async Task ScanAsync_ListsPlaceholderRowsPerTenant()
    {
        SeedMap(1, TenantA, "Alpha World");
        SeedMap(2, TenantB, "Beta World");
        SeedGrid("0", 1, TenantA, 18, 23);
        SeedGrid("0", 2, TenantB, 1, -4); // same id, different tenant — PK is (Id, TenantId)
        SeedGrid("real", 1, TenantA, 0, 0);

        var report = await _service.ScanAsync();

        Assert.Equal(2, report.PlaceholderRows.Count);
        var a = report.PlaceholderRows.Single(p => p.TenantId == TenantA);
        Assert.Equal("Tenant Alpha", a.TenantName);
        Assert.Equal(1, a.MapId);
        Assert.Equal(18, a.X);
        Assert.Equal(23, a.Y);
        Assert.False(report.IsClean);
        Assert.Empty(report.ContestedMaps); // placeholders alone are not contested cells
    }

    [Fact]
    public async Task ScanAsync_CleanDatabase_ReportsClean()
    {
        SeedMap(1, TenantA, "Alpha World");
        SeedGrid("g1", 1, TenantA, 0, 0);
        SeedGrid("g2", 1, TenantA, 1, 0);

        var report = await _service.ScanAsync();

        Assert.True(report.IsClean);
        Assert.Empty(report.ContestedMaps);
        Assert.Empty(report.PlaceholderRows);
        Assert.Equal(2, report.TotalGrids);
        Assert.Equal(2, report.TenantsScanned);
    }

    [Fact]
    public async Task ScanAsync_SampleCellsAreCapped_CountIsNot()
    {
        SeedMap(1, TenantA, "Alpha World");
        var total = MapIntegrityService.SampleCellCap + 3;
        for (var i = 0; i < total; i++)
        {
            SeedGrid($"x{i}", 1, TenantA, i, 0);
            SeedGrid($"y{i}", 1, TenantA, i, 0); // every cell contested
        }

        var report = await _service.ScanAsync();

        var issue = Assert.Single(report.ContestedMaps);
        Assert.Equal(total, issue.ContestedCellCount);
        Assert.Equal(MapIntegrityService.SampleCellCap, issue.SampleCells.Count);
    }

    // ---------- orphaned storage ----------

    private void SeedTileRow(string tenantId, int mapId, int x, int y, int zoom, string file, long cache = 1)
    {
        _db.Tiles.Add(new TileDataEntity
        {
            MapId = mapId, CoordX = x, CoordY = y, Zoom = zoom,
            File = file, Cache = cache, TenantId = tenantId, FileSizeBytes = 10
        });
        _db.SaveChanges();
    }

    private string WriteFile(params string[] segments)
    {
        var path = Path.Combine(new[] { _gridStorage }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[100]);
        return path;
    }

    /// <summary>
    /// One tenant with the full orphan zoo: a live map (rows, dirs, referenced pool PNG, a
    /// grid-guarded pool PNG), a dead map (rows, legacy + webp dirs), and one unreferenced pool
    /// PNG. Used by both the scan and the purge tests.
    /// </summary>
    private (string liveDir, string deadLegacyDir, string deadWebpDir, string referencedPng, string guardedPng, string orphanPng) SeedOrphanZoo()
    {
        SeedMap(100, TenantA, "Alive");
        SeedGrid("live-grid", 100, TenantA, 0, 0);
        SeedTileRow(TenantA, 100, 0, 0, 0, $"tenants/{TenantA}/grids/live-grid.png");
        SeedTileRow(TenantA, 100, 0, 0, 1, $"tenants/{TenantA}/100/1/0_0.png");

        // Dead map 200: rows at all zoom levels + directories in both trees.
        SeedTileRow(TenantA, 200, 5, 5, 0, $"tenants/{TenantA}/grids/live-grid.png"); // twin of live row
        SeedTileRow(TenantA, 200, 2, 2, 1, $"tenants/{TenantA}/200/1/2_2.png");

        var liveDir = Path.GetDirectoryName(WriteFile("tenants", TenantA, "100", "1", "0_0.png"))!;
        var deadLegacyDir = Path.GetDirectoryName(Path.GetDirectoryName(WriteFile("tenants", TenantA, "200", "1", "2_2.png"))!)!;
        var deadWebpDir = Path.GetDirectoryName(Path.GetDirectoryName(WriteFile("tenants", TenantA, "large", "200", "0", "1_1.webp"))!)!;

        var referencedPng = WriteFile("tenants", TenantA, "grids", "live-grid.png");
        SeedGrid("guarded-grid", 100, TenantA, 9, 9); // grid row, no tile row yet
        var guardedPng = WriteFile("tenants", TenantA, "grids", "guarded-grid.png");
        var orphanPng = WriteFile("tenants", TenantA, "grids", "nobody-references-me.png");

        return (liveDir, deadLegacyDir, deadWebpDir, referencedPng, guardedPng, orphanPng);
    }

    [Fact]
    public async Task ScanAsync_ReportsOrphanedStorage()
    {
        SeedOrphanZoo();

        var report = await _service.ScanAsync();

        Assert.False(report.IsClean);
        var entry = Assert.Single(report.OrphanStorage);
        Assert.Equal(TenantA, entry.TenantId);
        Assert.Equal(new List<int> { 200 }, entry.DeadMapIds);
        Assert.Equal(2, entry.OrphanedTileRows);          // zoom-0 twin + zoom-1 row on map 200
        Assert.Equal(2, entry.DeadMapDirectories);        // legacy 200/ + large/200/
        Assert.Equal(200, entry.DeadMapDirectoryBytes);   // two 100-byte files
        Assert.Equal(1, entry.UnreferencedGridFiles);     // only nobody-references-me.png
        Assert.Equal(100, entry.UnreferencedGridFileBytes);
    }

    [Fact]
    public async Task PurgeOrphanedMapData_DeletesOnlyDeadData()
    {
        var (liveDir, deadLegacyDir, deadWebpDir, referencedPng, guardedPng, orphanPng) = SeedOrphanZoo();

        var result = await _service.PurgeOrphanedMapDataAsync(TenantA);

        Assert.Equal(2, result.TileRowsDeleted);
        Assert.Equal(2, result.DirectoriesDeleted);
        Assert.Equal(1, result.GridFilesDeleted);

        // Dead data gone.
        Assert.False(Directory.Exists(deadLegacyDir));
        Assert.False(Directory.Exists(deadWebpDir));
        Assert.False(File.Exists(orphanPng));
        Assert.Equal(0, await _db.Tiles.IgnoreQueryFilters().CountAsync(t => t.MapId == 200));

        // Live data untouched.
        Assert.True(Directory.Exists(liveDir));
        Assert.True(File.Exists(referencedPng), "pool PNG referenced by a live tile row must survive");
        Assert.True(File.Exists(guardedPng), "pool PNG whose grid row exists must survive");
        Assert.Equal(2, await _db.Tiles.IgnoreQueryFilters().CountAsync(t => t.MapId == 100));

        // Quota was recalculated from what is actually left on disk (3 x 100-byte files).
        var tenant = await _db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == TenantA);
        Assert.Equal(300 / 1024.0 / 1024.0, tenant.CurrentStorageMB, precision: 6);

        // A rescan is clean.
        var report = await _service.ScanAsync();
        Assert.Empty(report.OrphanStorage);
    }

    // ---------- WebP pyramid drift ----------

    private string WriteWebp(int mapId, int zoom, int x, int y, DateTime? mtimeUtc = null)
    {
        var path = WriteFile("tenants", TenantA, "large", mapId.ToString(), zoom.ToString(), $"{x}_{y}.webp");
        if (mtimeUtc.HasValue)
        {
            File.SetLastWriteTimeUtc(path, mtimeUtc.Value);
        }
        return path;
    }

    [Fact]
    public async Task ScanAsync_DetectsWebpPyramidDrift()
    {
        SeedMap(400, TenantA, "Drifty");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Three cells with data: (0,0) has a STALE file, (10,0) a FRESH file, (20,0) NO file.
        SeedTileRow(TenantA, 400, 0, 0, 0, $"tenants/{TenantA}/grids/a.png", nowMs);
        SeedTileRow(TenantA, 400, 40, 0, 0, $"tenants/{TenantA}/grids/b.png", nowMs);
        SeedTileRow(TenantA, 400, 80, 0, 0, $"tenants/{TenantA}/grids/c.png", nowMs);

        WriteWebp(400, 0, 0, 0, DateTime.UtcNow.AddHours(-2));   // stale: data is newer than file + slack
        WriteWebp(400, 0, 10, 0);                                 // fresh: mtime now, not flagged
        WriteWebp(400, 0, 50, 50);                                // ghost: no rows anywhere near
        WriteWebp(400, 6, 5, 5);                                  // ghost at z6: footprint empty too

        var report = await _service.ScanAsync();

        var drift = Assert.Single(report.WebpDrift);
        Assert.Equal(400, drift.MapId);
        Assert.Equal(2, drift.GhostFiles);
        Assert.Equal(1, drift.StaleFiles);
        Assert.Equal(1, drift.MissingZoom0Cells); // cell (20,0)
        Assert.Equal(4, drift.FilesChecked);
        Assert.False(report.IsClean);
    }

    [Fact]
    public async Task ScanAsync_WebpDrift_NormalizesSecondsCacheAndSkipsCleanMaps()
    {
        // Map 500: row Cache written in SECONDS (the hmap-import unit) with an old file — only
        // correct unit normalization detects it as stale (misread as ms it would be 1970).
        SeedMap(500, TenantA, "SecondsUnits");
        _db.Tiles.Add(new TileDataEntity
        {
            MapId = 500, CoordX = 0, CoordY = 0, Zoom = 0,
            File = $"tenants/{TenantA}/grids/s.png", Cache = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TenantId = TenantA, FileSizeBytes = 10
        });
        _db.SaveChanges();
        WriteWebp(500, 0, 0, 0, DateTime.UtcNow.AddHours(-2));

        // Map 600: perfectly in sync — must not appear at all.
        SeedMap(600, TenantA, "Clean");
        SeedTileRow(TenantA, 600, 0, 0, 0, $"tenants/{TenantA}/grids/d.png");
        WriteWebp(600, 0, 0, 0);

        var report = await _service.ScanAsync();

        var drift = Assert.Single(report.WebpDrift);
        Assert.Equal(500, drift.MapId);
        Assert.Equal(1, drift.StaleFiles);
        Assert.DoesNotContain(report.WebpDrift, d => d.MapId == 600);
    }

    [Fact]
    public async Task ScanAsync_WebpDrift_FlagsParentOlderThanChild()
    {
        // The laundering case: a parent regenerated during the broken era has ghost content baked
        // in from stale siblings but a plausible mtime — only comparing against its own child
        // files exposes it. Data here is old (Cache=1), so the raw data check stays silent.
        SeedMap(700, TenantA, "Launderer");
        SeedTileRow(TenantA, 700, 0, 0, 0, $"tenants/{TenantA}/grids/e.png");

        WriteWebp(700, 0, 0, 0);                                  // child: fresh
        WriteWebp(700, 1, 0, 0, DateTime.UtcNow.AddHours(-2));    // parent: older than its child

        var report = await _service.ScanAsync();

        var drift = Assert.Single(report.WebpDrift);
        Assert.Equal(700, drift.MapId);
        Assert.Equal(0, drift.GhostFiles);
        Assert.Equal(1, drift.StaleFiles);
        Assert.Contains(drift.SampleDriftTiles, s => s.Contains("older than child"));
    }

    [Fact]
    public async Task PurgeOrphanedMapData_KeepsDeadDirectoryReferencedByLiveRows()
    {
        // Public-map imports write zoom-0 tiles into per-map dirs; a merge copies the rows (File
        // path unchanged) to the target map. The dead source dir then still backs LIVE imagery.
        SeedMap(300, TenantA, "Target");
        SeedGrid("g300", 300, TenantA, 0, 0);
        SeedTileRow(TenantA, 300, 7, 7, 0, $"tenants/{TenantA}/301/0/7_7.png"); // live row, dead map's dir
        var deadDirFile = WriteFile("tenants", TenantA, "301", "0", "7_7.png");

        var result = await _service.PurgeOrphanedMapDataAsync(TenantA);

        Assert.True(File.Exists(deadDirFile), "a dead map's directory referenced by live rows must survive");
        Assert.Equal(0, result.DirectoriesDeleted);
        Assert.Contains(result.Warnings, w => w.Contains("live tile rows still reference"));
    }
}
