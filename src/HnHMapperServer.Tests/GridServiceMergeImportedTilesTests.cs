using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
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
/// Merges of maps whose zoom-0 PNGs live in the map's own directory (.hmap / public-map imports
/// write tenants/{t}/{mapId}/0/{x}_{y}.png; client uploads use the grid pool). The merge used to
/// copy those rows to the target with File unchanged and then delete tenants/{t}/{src}/ — the very
/// files the copied rows pointed at, so their WebP cells rendered dark. Now each such row is
/// re-homed into the grid pool (copy, pool wins, missing source keeps the row as restore key and
/// re-arms the grid), and the per-map directory is deleted only when no LIVE-map row references it.
///
/// Real SQLite on a kept-open ":memory:" connection: the directory guard is a StartsWith + subquery
/// translation that must run as SQL. A unique tenant per test because LargeTileService's caches are
/// process-static.
/// </summary>
public class GridServiceMergeImportedTilesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly string _gridStorage;
    private readonly GridService _gridService;
    private readonly IGridRepository _gridRepository;
    private readonly TileService _tileService;
    private readonly ZoomTileQueueService _zoomTileQueue;
    private readonly TenantFilePathService _filePaths = new();
    private readonly Mock<ILogger<GridService>> _gridLogger = new();
    // The retired-directory guard lives in TileService.DeleteRetiredMapDirectoriesAsync.
    private readonly Mock<ILogger<TileService>> _tileLogger = new();
    private readonly string _tenantId = $"merge-{Guid.NewGuid():N}";

    private static readonly Rgba32 Green = new(0, 255, 0, 255);
    private static readonly Rgba32 Red = new(255, 0, 0, 255);

    public GridServiceMergeImportedTilesTests()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = _tenantId;
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new ApplicationDbContext(options, httpContextAccessor.Object);
        _db.Database.EnsureCreated();

        _db.Tenants.Add(new TenantEntity
        {
            Id = _tenantId, Name = _tenantId, StorageQuotaMB = 1024, CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow, IsActive = true
        });
        _db.SaveChanges();

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-merge-imported-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        var tenantContext = new Mock<ITenantContextAccessor>();
        tenantContext.Setup(x => x.GetCurrentTenantId()).Returns(_tenantId);
        tenantContext.Setup(x => x.GetRequiredTenantId()).Returns(_tenantId);

        _gridRepository = new GridRepository(_db, tenantContext.Object);
        var mapRepository = new MapRepository(_db, tenantContext.Object);
        var tileRepository = new TileRepository(_db, tenantContext.Object);
        var configRepository = new ConfigRepository(_db, tenantContext.Object);
        var notifications = new Mock<IUpdateNotificationService>();
        var mapNames = new Mock<IMapNameService>();
        mapNames.Setup(x => x.GenerateUniqueIdentifierAsync(It.IsAny<string>()))
            .ReturnsAsync(() => $"test-map-{Guid.NewGuid():N}");

        _tileService = new TileService(
            tileRepository,
            _gridRepository,
            notifications.Object,
            new Mock<IStorageQuotaService>().Object,
            _tileLogger.Object,
            _db);

        _zoomTileQueue = new ZoomTileQueueService(new Mock<ILogger<ZoomTileQueueService>>().Object);

        _gridService = new GridService(
            _gridRepository,
            mapRepository,
            _tileService,
            configRepository,
            notifications.Object,
            mapNames.Object,
            new Mock<IPendingMarkerService>().Object,
            tenantContext.Object,
            _zoomTileQueue,
            _filePaths,
            new MergeRejectionCache(),
            new TileCacheService(Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), Mock.Of<ILogger<TileCacheService>>()),
            _gridLogger.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    // ---- seeding helpers -------------------------------------------------------------------

    private void SeedMap(int mapId)
    {
        _db.Maps.Add(new MapInfoEntity { Id = mapId, Name = $"Map{mapId}", TenantId = _tenantId, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    private void SeedGrid(string id, int mapId, int x, int y, DateTime? nextUpdate = null)
    {
        _db.Grids.Add(new GridDataEntity
        {
            Id = id, Map = mapId, CoordX = x, CoordY = y,
            NextUpdate = nextUpdate ?? DateTime.UtcNow.AddMinutes(-1), TenantId = _tenantId
        });
        _db.SaveChanges();
    }

    private void SeedZoom0Row(int mapId, int x, int y, string file, int size)
    {
        _db.Tiles.Add(new TileDataEntity
        {
            MapId = mapId, CoordX = x, CoordY = y, Zoom = 0,
            File = file, Cache = 1789043688, TenantId = _tenantId, FileSizeBytes = size
        });
        _db.SaveChanges();
    }

    /// <summary>Writes a solid 100x100 PNG at a path relative to GridStorage; returns its size.</summary>
    private int WritePng(string relativePath, Rgba32 color)
    {
        var full = Path.Combine(_gridStorage, relativePath.Replace('\\', '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var img = new Image<Rgba32>(100, 100, color))
        {
            img.SaveAsPng(full);
        }
        return (int)new FileInfo(full).Length;
    }

    private string Full(string relativePath) => Path.Combine(_gridStorage, relativePath.Replace('\\', '/'));

    private string PerMapDir(int mapId) => Path.Combine(_gridStorage, "tenants", _tenantId, mapId.ToString());

    private string LargeDir(int mapId) => Path.Combine(_gridStorage, "tenants", _tenantId, "large", mapId.ToString());

    /// <summary>Target map 1 (t1@(0,0), t2@(0,1), pool PNGs) and source map 2 (s1@(50,50), s2@(50,51)).</summary>
    private void SeedTargetAndSourceMaps(DateTime? sourceNextUpdate = null)
    {
        SeedMap(1);
        SeedGrid("t1", 1, 0, 0);
        SeedGrid("t2", 1, 0, 1);
        foreach (var (id, y) in new[] { ("t1", 0), ("t2", 1) })
        {
            var pool = _filePaths.GetGridRelativePath(_tenantId, id);
            SeedZoom0Row(1, 0, y, pool, WritePng(pool, Red));
        }

        SeedMap(2);
        SeedGrid("s1", 2, 50, 50, sourceNextUpdate);
        SeedGrid("s2", 2, 50, 51, sourceNextUpdate);
    }

    /// <summary>
    /// Two agreeing witnesses per map: t1@(0,0)/t2@(0,1) offset (0,0); s1@(1,0)/s2@(1,1) offset
    /// (49,50). Target = map 1 (lower id); shift (-49,-50) moves s1 to (1,0) and s2 to (1,1).
    /// </summary>
    private Task<GridRequestDto> TriggerMergeAsync() => _gridService.ProcessGridUpdateAsync(new GridUpdateDto
    {
        Grids = new List<List<string>>
        {
            new() { "t1", "t2", "x3" },
            new() { "s1", "s2", "x6" },
            new() { "x7", "x8", "x9" }
        }
    }, _gridStorage);

    private void VerifyLogged(LogLevel level, string fragment, Times times) =>
        VerifyLogged(_gridLogger, level, fragment, times);

    private static void VerifyLogged<T>(Mock<ILogger<T>> logger, LogLevel level, string fragment, Times times) =>
        logger.Verify(x => x.Log(
            level,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(fragment)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);

    // ---- tests ----------------------------------------------------------------------------

    [Fact]
    public async Task Merge_RehomesImportedPerMapTiles_IntoGridPool_AndTargetWebpGenerates()
    {
        SeedTargetAndSourceMaps();
        // Importer layout, both separators (stored paths carry the writing OS's separator).
        var s1File = $"tenants/{_tenantId}/2/0/50_50.png";
        var s2File = $"tenants\\{_tenantId}\\2\\0\\50_51.png";
        SeedZoom0Row(2, 50, 50, s1File, WritePng(s1File, Green));
        SeedZoom0Row(2, 50, 51, s2File, WritePng(s2File, Green));
        // The source's own pyramids, as an import + viewers would have left them.
        WritePng(Path.Combine("tenants", _tenantId, "2", "1", "25_25.png"), Green);
        Directory.CreateDirectory(Path.Combine(LargeDir(2), "0"));
        File.WriteAllBytes(Path.Combine(LargeDir(2), "0", "12_12.webp"), new byte[] { 1, 2, 3 });

        await TriggerMergeAsync();

        Assert.False(await _db.Maps.IgnoreQueryFilters().AnyAsync(m => m.Id == 2), "source map is retired");
        Assert.False(Directory.Exists(PerMapDir(2)), "nothing references the source directory any more");
        Assert.False(Directory.Exists(LargeDir(2)));

        foreach (var id in new[] { "s1", "s2" })
        {
            var grid = await _gridRepository.GetGridAsync(id);
            Assert.NotNull(grid);
            Assert.Equal(1, grid.Map);

            var row = await _tileService.GetTileAsync(1, grid.Coord, 0);
            Assert.NotNull(row);
            Assert.Equal(_filePaths.GetGridRelativePath(_tenantId, id), row.File);
            Assert.True(File.Exists(Full(row.File)), $"{id}'s re-homed PNG must exist");
            Assert.Equal((int)new FileInfo(Full(row.File)).Length, row.FileSizeBytes);
            using var img = Image.Load<Rgba32>(Full(row.File));
            Assert.Equal(Green, img[50, 50]);
        }

        // No zoom-0 row of the tenant may dangle.
        var rows = await _db.Tiles.IgnoreQueryFilters()
            .Where(t => t.TenantId == _tenantId && t.Zoom == 0)
            .Select(t => t.File)
            .ToListAsync();
        Assert.All(rows, f => Assert.True(File.Exists(Full(f)), $"dangling zoom-0 row: {f}"));

        // The legacy z1 pass (runs after the directory delete) now finds its inputs.
        var movedS1 = (await _gridRepository.GetGridAsync("s1"))!;
        var zoom1 = await _tileService.GetTileAsync(1, movedS1.Coord.Parent(), 1);
        Assert.NotNull(zoom1);
        Assert.True(File.Exists(Full(zoom1.File)));

        // Every queued WebP cell of the target can be built, and shows the merged-in imagery.
        var queued = new List<ZoomTileRequest>();
        while (_zoomTileQueue.Reader.TryRead(out var request))
            queued.Add(request);
        Assert.NotEmpty(queued);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GridStorage"] = _gridStorage })
            .Build();
        var largeTiles = new LargeTileService(_db, configuration, Mock.Of<ILogger<LargeTileService>>());
        foreach (var request in queued)
        {
            Assert.Equal(_tenantId, request.TenantId);
            Assert.Equal(1, request.MapId);
            var cx = (int)Math.Floor(request.BaseX / 4.0);
            var cy = (int)Math.Floor(request.BaseY / 4.0);

            var bytes = await largeTiles.ForceRegenerateLargeTileAsync(_tenantId, 1, 0, cx, cy);
            Assert.NotNull(bytes);
            Assert.True(File.Exists(largeTiles.GetLargeTilePath(_tenantId, 1, 0, cx, cy)));
        }

        // s1 sits at (1,0) of cell (0,0): its 100x100 block starts at pixel (100,0).
        var cell = await largeTiles.ForceRegenerateLargeTileAsync(_tenantId, 1, 0, 0, 0);
        using var webp = Image.Load<Rgba32>(cell!);
        var px = webp[150, 50];
        Assert.True(px.G > 200 && px.R < 60, $"merged-in imagery expected green, got {px}");

        VerifyLogged(LogLevel.Information, "2 imported zoom-0 tile(s) re-homed", Times.Once());
        VerifyLogged(_tileLogger, LogLevel.Warning, "Kept directory of merged map", Times.Never());
    }

    [Fact]
    public async Task Merge_ExistingPoolPng_Wins_AndIsNotOverwritten()
    {
        SeedTargetAndSourceMaps();
        var s1File = Path.Combine("tenants", _tenantId, "2", "0", "50_50.png");
        var s2File = Path.Combine("tenants", _tenantId, "2", "0", "50_51.png");
        SeedZoom0Row(2, 50, 50, s1File, WritePng(s1File, Green));
        SeedZoom0Row(2, 50, 51, s2File, WritePng(s2File, Green));
        // s1 was already uploaded by a client: its pool PNG is newer than the import render.
        var s1Pool = _filePaths.GetGridRelativePath(_tenantId, "s1");
        var poolSize = WritePng(s1Pool, Red);

        await TriggerMergeAsync();

        var movedS1 = (await _gridRepository.GetGridAsync("s1"))!;
        var row = await _tileService.GetTileAsync(1, movedS1.Coord, 0);
        Assert.NotNull(row);
        Assert.Equal(s1Pool, row.File);
        Assert.Equal(poolSize, row.FileSizeBytes);
        using (var img = Image.Load<Rgba32>(Full(s1Pool)))
        {
            Assert.Equal(Red, img[50, 50]); // never overwritten by the import render
        }

        Assert.False(Directory.Exists(PerMapDir(2)));
        VerifyLogged(LogLevel.Information, "1 imported zoom-0 tile(s) re-homed into the grid pool, 1 already pooled", Times.Once());
    }

    [Fact]
    public async Task Merge_KeepsSourceDirectory_WhenAnotherLiveMapRowReferencesIt()
    {
        SeedTargetAndSourceMaps();
        var s1File = Path.Combine("tenants", _tenantId, "2", "0", "50_50.png");
        var s2File = Path.Combine("tenants", _tenantId, "2", "0", "50_51.png");
        SeedZoom0Row(2, 50, 50, s1File, WritePng(s1File, Green));
        SeedZoom0Row(2, 50, 51, s2File, WritePng(s2File, Green));
        // Legacy cross-map layout: live map 3 shows a file inside map 2's directory
        // (backslash-separated, as a Windows-hosted server would have stored it).
        SeedMap(3);
        var crossFile = $"tenants\\{_tenantId}\\2\\0\\99_99.png";
        SeedZoom0Row(3, 7, 7, crossFile, WritePng(crossFile, Green));
        Directory.CreateDirectory(Path.Combine(LargeDir(2), "0"));
        File.WriteAllBytes(Path.Combine(LargeDir(2), "0", "12_12.webp"), new byte[] { 1, 2, 3 });

        await TriggerMergeAsync();

        Assert.True(File.Exists(Full(crossFile)), "a file a live map still shows must survive the merge");
        Assert.False(Directory.Exists(LargeDir(2)), "the source's WebP pyramid always goes");
        Assert.False(await _db.Maps.IgnoreQueryFilters().AnyAsync(m => m.Id == 2));
        VerifyLogged(_tileLogger, LogLevel.Warning, "Kept directory of merged map 2", Times.Once());

        // The merged rows themselves were still re-homed.
        var movedS2 = (await _gridRepository.GetGridAsync("s2"))!;
        var row = await _tileService.GetTileAsync(1, movedS2.Coord, 0);
        Assert.Equal(_filePaths.GetGridRelativePath(_tenantId, "s2"), row!.File);
    }

    [Fact]
    public async Task Merge_DeletesSourceDirectory_WhenOnlyDeadMapRowsReferenceIt()
    {
        SeedTargetAndSourceMaps();
        var s1File = Path.Combine("tenants", _tenantId, "2", "0", "50_50.png");
        var s2File = Path.Combine("tenants", _tenantId, "2", "0", "50_51.png");
        SeedZoom0Row(2, 50, 50, s1File, WritePng(s1File, Green));
        SeedZoom0Row(2, 50, 51, s2File, WritePng(s2File, Green));
        // Map 99 has no Maps row: its rows are orphans and must not protect the directory —
        // the integrity scan/purge count LIVE-map rows only, and the merge agrees with them.
        var deadFile = Path.Combine("tenants", _tenantId, "2", "0", "99_99.png");
        SeedZoom0Row(99, 7, 7, deadFile, WritePng(deadFile, Green));

        await TriggerMergeAsync();

        Assert.False(Directory.Exists(PerMapDir(2)));
        VerifyLogged(_tileLogger, LogLevel.Warning, "Kept directory of merged map", Times.Never());
    }

    [Fact]
    public async Task Merge_SourceFileAlreadyMissing_KeepsRowAndRearmsGrid()
    {
        // Grids not requestable before the merge, so the re-arm is observable.
        SeedTargetAndSourceMaps(sourceNextUpdate: DateTime.UtcNow.AddMinutes(30));
        var s1File = Path.Combine("tenants", _tenantId, "2", "0", "50_50.png"); // never written
        var s2File = Path.Combine("tenants", _tenantId, "2", "0", "50_51.png");
        SeedZoom0Row(2, 50, 50, s1File, 4242);
        SeedZoom0Row(2, 50, 51, s2File, WritePng(s2File, Green));

        await TriggerMergeAsync();

        Assert.False(await _db.Maps.IgnoreQueryFilters().AnyAsync(m => m.Id == 2));

        // s1: the row keeps its exact path and size — the only key a backup restore can use.
        var movedS1 = (await _gridRepository.GetGridAsync("s1"))!;
        Assert.Equal(1, movedS1.Map);
        var s1Row = await _tileService.GetTileAsync(1, movedS1.Coord, 0);
        Assert.NotNull(s1Row);
        Assert.Equal(s1File, s1Row.File);
        Assert.Equal(4242, s1Row.FileSizeBytes);
        Assert.True(movedS1.NextUpdate <= DateTime.UtcNow, "the grid must be re-requested from clients");
        Assert.False(File.Exists(_filePaths.GetGridFilePath(_tenantId, "s1", _gridStorage)));

        // s2 re-homed normally and NOT re-armed.
        var movedS2 = (await _gridRepository.GetGridAsync("s2"))!;
        var s2Row = await _tileService.GetTileAsync(1, movedS2.Coord, 0);
        Assert.Equal(_filePaths.GetGridRelativePath(_tenantId, "s2"), s2Row!.File);
        Assert.True(movedS2.NextUpdate > DateTime.UtcNow);

        // s1's row still points into map 2's directory, so the guard keeps it.
        Assert.True(Directory.Exists(PerMapDir(2)));
        VerifyLogged(LogLevel.Warning, "1 without a source file", Times.Once());
        VerifyLogged(_tileLogger, LogLevel.Warning, "Kept directory of merged map 2", Times.Once());
    }

    [Fact]
    public async Task Merge_ContestedSourceCell_CopiesIntoNoPoolFile_AndKeepsRow()
    {
        SeedTargetAndSourceMaps();
        // A corrupted DB: s1b claims s1's cell. The one coord-keyed per-map PNG there cannot be
        // attributed to either grid, so it must not become grids/s1.png or grids/s1b.png.
        SeedGrid("s1b", 2, 50, 50);
        var s1File = Path.Combine("tenants", _tenantId, "2", "0", "50_50.png");
        var s2File = Path.Combine("tenants", _tenantId, "2", "0", "50_51.png");
        var s1Size = WritePng(s1File, Green);
        SeedZoom0Row(2, 50, 50, s1File, s1Size);
        SeedZoom0Row(2, 50, 51, s2File, WritePng(s2File, Green));

        await TriggerMergeAsync();

        Assert.False(await _db.Maps.IgnoreQueryFilters().AnyAsync(m => m.Id == 2));
        Assert.False(File.Exists(_filePaths.GetGridFilePath(_tenantId, "s1", _gridStorage)));
        Assert.False(File.Exists(_filePaths.GetGridFilePath(_tenantId, "s1b", _gridStorage)));

        // Both grids moved onto the same target cell, whose row is exactly what it was.
        var movedS1 = (await _gridRepository.GetGridAsync("s1"))!;
        var movedS1b = (await _gridRepository.GetGridAsync("s1b"))!;
        Assert.Equal(1, movedS1b.Map);
        Assert.Equal(movedS1.Coord, movedS1b.Coord);
        var row = await _tileService.GetTileAsync(1, movedS1.Coord, 0);
        Assert.NotNull(row);
        Assert.Equal(s1File, row.File);
        Assert.Equal(s1Size, row.FileSizeBytes);
        Assert.True(File.Exists(Full(row.File)), "the guard keeps the file the row still shows");

        // The uncontested cell is re-homed as usual.
        var movedS2 = (await _gridRepository.GetGridAsync("s2"))!;
        var s2Row = await _tileService.GetTileAsync(1, movedS2.Coord, 0);
        Assert.Equal(_filePaths.GetGridRelativePath(_tenantId, "s2"), s2Row!.File);

        VerifyLogged(LogLevel.Warning, "1 imported zoom-0 tile(s) re-homed", Times.Once());
        VerifyLogged(LogLevel.Warning, "2 on contested cell(s)", Times.Once());
        VerifyLogged(_tileLogger, LogLevel.Warning, "Kept directory of merged map 2", Times.Once());
    }
}
