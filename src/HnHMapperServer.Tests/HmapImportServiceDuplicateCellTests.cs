using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using ITenantContextAccessor = HnHMapperServer.Core.Interfaces.ITenantContextAccessor;

namespace HnHMapperServer.Tests;

/// <summary>
/// Regression tests for the 2026-08-24 prod incident: an .hmap whose client cache went
/// through cell flip-flop corruption carries two DIFFERENT grid ids claiming the same
/// segment cell. Pre-fix, both rendered a zoom-0 tile at the same coordinate and the batch
/// flush died on the Tiles unique index (MapId, Zoom, CoordX, CoordY); the marker phase
/// would separately have thrown in its ToDictionary over the same duplicate cells; and the
/// failed SaveChanges left its entities tracked, so CleanupFailedImportAsync — sharing the
/// scoped DbContext — rethrew the same error on every delete and rolled back nothing.
/// Same offline harness as HmapImportServiceMergeTests (kept-open SQLite ":memory:",
/// synthetic Version-1 .hmap records that render gray).
/// The collection serializes all classes that call ImportAsync: its STATIC global import
/// lock is try-acquire (TimeSpan.Zero), so parallel test classes would reject each other
/// with "Another import is already in progress".
/// </summary>
[Collection("HmapImportGlobalLock")]
public class HmapImportServiceDuplicateCellTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly string _gridStorage;
    private readonly HmapImportService _importService;
    private readonly IGridRepository _gridRepository;
    private readonly Mock<IStorageQuotaService> _quotaServiceMock;
    private readonly List<(string GridId, int X, int Y, string Name, string Image)> _capturedMarkers = new();

    private const string TestTenantId = "default-tenant-1";

    public HmapImportServiceDuplicateCellTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TestTenantId;
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        _dbContext = new ApplicationDbContext(options, mockHttpContextAccessor.Object);
        _dbContext.Database.EnsureCreated();

        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = TestTenantId,
            Name = TestTenantId,
            StorageQuotaMB = 1024,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
        _dbContext.SaveChanges();

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-hmapdup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        var mockTenantContext = new Mock<ITenantContextAccessor>();
        mockTenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TestTenantId);
        mockTenantContext.Setup(x => x.GetRequiredTenantId()).Returns(TestTenantId);

        _gridRepository = new GridRepository(_dbContext, mockTenantContext.Object);
        var mapRepository = new MapRepository(_dbContext, mockTenantContext.Object);
        var tileRepository = new TileRepository(_dbContext, mockTenantContext.Object);

        var mockNotificationService = new Mock<IUpdateNotificationService>();
        _quotaServiceMock = new Mock<IStorageQuotaService>();
        var mockMapNameService = new Mock<IMapNameService>();
        mockMapNameService.Setup(x => x.GenerateUniqueIdentifierAsync(It.IsAny<string>()))
            .ReturnsAsync(() => $"import-map-{Guid.NewGuid():N}");

        var markerServiceMock = new Mock<IMarkerService>();
        markerServiceMock
            .Setup(m => m.BulkUploadMarkersAsync(It.IsAny<List<(string GridId, int X, int Y, string Name, string Image)>>()))
            .Returns(Task.CompletedTask)
            .Callback<List<(string GridId, int X, int Y, string Name, string Image)>>(b => _capturedMarkers.AddRange(b));

        var tileService = new TileService(
            tileRepository,
            _gridRepository,
            mockNotificationService.Object,
            _quotaServiceMock.Object,
            Mock.Of<ILogger<TileService>>(),
            _dbContext);

        _importService = new HmapImportService(
            _gridRepository,
            mapRepository,
            tileService,
            tileRepository,
            Mock.Of<IOverlayDataRepository>(),
            _quotaServiceMock.Object,
            mockMapNameService.Object,
            markerServiceMock.Object,
            mockNotificationService.Object,
            _dbContext,
            Mock.Of<ILogger<HmapImportService>>());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_gridStorage)) Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    /// <summary>
    /// Same builder as HmapImportServiceMergeTests.BuildHmap, extended with a per-grid
    /// ModifiedTime (the fix keeps the newest grid per cell) and optional "mark" records
    /// (player markers, type 'p') so the marker phase's grid lookup can be exercised.
    /// </summary>
    private static MemoryStream BuildHmap(
        (long gridId, int x, int y, long mtime)[] grids,
        (long segmentId, int tileX, int tileY, string name)[]? markers = null)
    {
        var ms = new MemoryStream();
        ms.Write(System.Text.Encoding.ASCII.GetBytes("Haven Mapfile 1"));
        ms.WriteByte(0x78); ms.WriteByte(0xDA);
        using (var deflate = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            void WriteRecord(string type, byte[] bytes)
            {
                var typeBytes = System.Text.Encoding.UTF8.GetBytes(type);
                deflate.Write(typeBytes, 0, typeBytes.Length);
                deflate.WriteByte(0);
                var len = BitConverter.GetBytes(bytes.Length);
                deflate.Write(len, 0, len.Length);
                deflate.Write(bytes, 0, bytes.Length);
            }

            foreach (var (gridId, x, y, mtime) in grids)
            {
                using var body = new MemoryStream();
                using (var b = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    b.Write((byte)1);      // Version 1 (<4 -> only identity is parsed)
                    b.Write(gridId);       // GridId (int64)
                    b.Write(0L);           // SegmentId (single segment)
                    b.Write(mtime);        // ModifiedTime
                    b.Write(x);            // TileX
                    b.Write(y);            // TileY
                }
                WriteRecord("grid", body.ToArray());
            }

            foreach (var (segmentId, tileX, tileY, name) in markers ?? Array.Empty<(long, int, int, string)>())
            {
                using var body = new MemoryStream();
                using (var b = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    b.Write((byte)1);      // Marker version 1
                    b.Write(segmentId);    // SegmentId (int64)
                    b.Write(tileX);        // Absolute tile X
                    b.Write(tileY);        // Absolute tile Y
                    b.Write(System.Text.Encoding.UTF8.GetBytes(name));
                    b.Write((byte)0);      // Null terminator
                    b.Write((byte)'p');    // PMarker
                    b.Write((byte)255); b.Write((byte)0); b.Write((byte)0); b.Write((byte)255); // RGBA
                }
                WriteRecord("mark", body.ToArray());
            }
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task ImportAsync_CreateNew_TwoGridsClaimSameCell_KeepsNewestAndSucceeds()
    {
        // Grid 2 is the newer claimant of cell (0,0). Pre-fix this import died on the Tiles
        // unique index when both landed in the same batch.
        using var hmap = BuildHmap(new[] { (1L, 0, 0, 100L), (2L, 0, 0, 200L), (3L, 1, 0, 100L) });
        var result = await _importService.ImportAsync(hmap, TestTenantId, HmapImportMode.CreateNew, _gridStorage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.GridsImported);
        Assert.Equal(1, result.GridsSkipped);

        var mapId = Assert.Single(result.CreatedMapIds);

        // The newest grid owns the cell; the loser was never imported
        var winner = await _gridRepository.GetGridAsync("2");
        Assert.NotNull(winner);
        Assert.Equal(new Coord(0, 0), winner.Coord);
        Assert.Equal(mapId, winner.Map);
        Assert.Null(await _gridRepository.GetGridAsync("1"));

        // Exactly one zoom-0 tile at the contested cell
        var cellTiles = await _dbContext.Tiles.AsNoTracking()
            .Where(t => t.MapId == mapId && t.Zoom == 0 && t.CoordX == 0 && t.CoordY == 0)
            .ToListAsync();
        Assert.Single(cellTiles);
    }

    [Fact]
    public async Task ImportAsync_CreateNew_NewestGridFirstInFile_StillWinsByModifiedTime()
    {
        // File order reversed relative to the sibling test — the winner is picked by
        // ModifiedTime, not by position in the file.
        using var hmap = BuildHmap(new[] { (2L, 0, 0, 200L), (1L, 0, 0, 100L) });
        var result = await _importService.ImportAsync(hmap, TestTenantId, HmapImportMode.CreateNew, _gridStorage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.GridsImported);
        Assert.Equal(1, result.GridsSkipped);
        Assert.NotNull(await _gridRepository.GetGridAsync("2"));
        Assert.Null(await _gridRepository.GetGridAsync("1"));
    }

    [Fact]
    public async Task ImportAsync_MergeMode_DuplicateCellInSegment_ImportsWithoutTileConflict()
    {
        // Empty DB -> the merge planner falls back to CreateNew, exercising the merge
        // branch's filter chain followed by the shared per-cell dedup.
        using var hmap = BuildHmap(new[] { (1L, 0, 0, 100L), (2L, 0, 0, 200L), (3L, 1, 0, 100L) });
        var result = await _importService.ImportAsync(hmap, TestTenantId, HmapImportMode.Merge, _gridStorage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.GridsImported);
        Assert.Equal(1, result.GridsSkipped);
        Assert.NotNull(await _gridRepository.GetGridAsync("2"));
        Assert.Null(await _gridRepository.GetGridAsync("1"));
    }

    [Fact]
    public async Task ImportAsync_DuplicateCellWithMarker_MarkerPhaseSurvivesAndAttachesToWinner()
    {
        // Pre-fix the marker phase built its (TileX, TileY) -> GridId lookup with a plain
        // ToDictionary over the raw file grids — the duplicate cell threw ArgumentException
        // and failed the whole import AFTER all terrain had been committed. The marker on
        // the contested cell must land on the same winner the grid import kept.
        using var hmap = BuildHmap(
            new[] { (1L, 0, 0, 100L), (2L, 0, 0, 200L) },
            new[] { (0L, 5, 5, "Quest Stone") });
        var result = await _importService.ImportAsync(hmap, TestTenantId, HmapImportMode.CreateNew, _gridStorage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.MarkersImported);
        var marker = Assert.Single(_capturedMarkers);
        Assert.Equal("2", marker.GridId);
        Assert.Equal("Quest Stone", marker.Name);
        Assert.Equal(5, marker.X);
        Assert.Equal(5, marker.Y);
    }

    [Fact]
    public async Task CleanupFailedImportAsync_PoisonedContext_StillDeletesEverything()
    {
        // Reproduces the incident's second stage: the failed batch's entities are still
        // tracked in Added state when cleanup runs on the same scoped context. Pre-fix,
        // every SaveChanges in cleanup rethrew the original UNIQUE violation and nothing
        // was deleted ("Failed to delete map 2247 during cleanup").
        const int mapId = 5;
        _dbContext.Maps.Add(new MapInfoEntity
        {
            Id = mapId,
            Name = "Broken import",
            TenantId = TestTenantId,
            CreatedAt = DateTime.UtcNow
        });
        _dbContext.Grids.Add(new GridDataEntity
        {
            Id = "10",
            Map = mapId,
            CoordX = 0,
            CoordY = 0,
            NextUpdate = DateTime.UtcNow.AddMinutes(-1),
            TenantId = TestTenantId
        });
        _dbContext.Tiles.Add(new TileDataEntity
        {
            MapId = mapId,
            CoordX = 0,
            CoordY = 0,
            Zoom = 0,
            File = $"tenants/{TestTenantId}/{mapId}/0/0_0.png",
            Cache = 1,
            TenantId = TestTenantId,
            FileSizeBytes = 10
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // On-disk directory so the quota refund path runs too
        var mapDir = Path.Combine(_gridStorage, "tenants", TestTenantId, mapId.ToString(), "0");
        Directory.CreateDirectory(mapDir);
        await File.WriteAllBytesAsync(Path.Combine(mapDir, "0_0.png"), new byte[2048]);

        // Poison: a duplicate of the committed tile row, tracked in Added state — any
        // SaveChanges on this context now fails with the Tiles UNIQUE violation.
        _dbContext.Tiles.Add(new TileDataEntity
        {
            MapId = mapId,
            CoordX = 0,
            CoordY = 0,
            Zoom = 0,
            File = "duplicate.png",
            Cache = 2,
            TenantId = TestTenantId,
            FileSizeBytes = 10
        });

        await _importService.CleanupFailedImportAsync(
            new[] { mapId }, new[] { "10" }, TestTenantId, _gridStorage);

        Assert.Empty(await _dbContext.Grids.AsNoTracking().Where(g => g.Map == mapId).ToListAsync());
        Assert.Empty(await _dbContext.Tiles.AsNoTracking().Where(t => t.MapId == mapId).ToListAsync());
        Assert.Empty(await _dbContext.Maps.AsNoTracking().Where(m => m.Id == mapId).ToListAsync());
        Assert.False(Directory.Exists(Path.Combine(_gridStorage, "tenants", TestTenantId, mapId.ToString())));
        _quotaServiceMock.Verify(
            q => q.IncrementStorageUsageAsync(TestTenantId, It.Is<double>(d => d < 0)), Times.Once);
    }
}
