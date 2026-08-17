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
/// Thin integration slice through HmapImportService.ImportAsync in Merge mode, proving the
/// HmapMergePlanner decisions drive real imports end-to-end (and that deleting the old
/// zero-validation fallback broke nothing on the happy path). Uses a kept-open SQLite
/// ":memory:" connection (real SQL semantics) and synthetic binary .hmap files — Version-1
/// grid records carry no tilesets, so the tile prefetch gets an empty list and the renderer
/// paints gray: fully offline.
/// </summary>
public class HmapImportServiceMergeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly string _gridStorage;
    private readonly HmapImportService _importService;
    private readonly IGridRepository _gridRepository;

    private const string TestTenantId = "default-tenant-1";

    public HmapImportServiceMergeTests()
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

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-hmapimport-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        var mockTenantContext = new Mock<ITenantContextAccessor>();
        mockTenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TestTenantId);
        mockTenantContext.Setup(x => x.GetRequiredTenantId()).Returns(TestTenantId);

        _gridRepository = new GridRepository(_dbContext, mockTenantContext.Object);
        var mapRepository = new MapRepository(_dbContext, mockTenantContext.Object);
        var tileRepository = new TileRepository(_dbContext, mockTenantContext.Object);

        var mockNotificationService = new Mock<IUpdateNotificationService>();
        var mockQuotaService = new Mock<IStorageQuotaService>();
        var mockMapNameService = new Mock<IMapNameService>();
        mockMapNameService.Setup(x => x.GenerateUniqueIdentifierAsync(It.IsAny<string>()))
            .ReturnsAsync(() => $"import-map-{Guid.NewGuid():N}");

        var tileService = new TileService(
            tileRepository,
            _gridRepository,
            mockNotificationService.Object,
            mockQuotaService.Object,
            Mock.Of<ILogger<TileService>>(),
            _dbContext);

        _importService = new HmapImportService(
            _gridRepository,
            mapRepository,
            tileService,
            tileRepository,
            Mock.Of<IOverlayDataRepository>(),
            mockQuotaService.Object,
            mockMapNameService.Object,
            Mock.Of<IMarkerService>(),
            mockNotificationService.Object,
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
    /// Builds a minimal valid .hmap in memory (same format as PublicMapAnalysisServiceTests'
    /// WriteHmap): "Haven Mapfile 1" signature, 2-byte zlib header the reader skips, then
    /// deflate-compressed "grid" records (Version 1 = identity only, no tilesets).
    /// </summary>
    private static MemoryStream BuildHmap(params (long gridId, int x, int y)[] grids)
    {
        var ms = new MemoryStream();
        ms.Write(System.Text.Encoding.ASCII.GetBytes("Haven Mapfile 1"));
        ms.WriteByte(0x78); ms.WriteByte(0xDA);
        using (var deflate = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            foreach (var (gridId, x, y) in grids)
            {
                var type = System.Text.Encoding.UTF8.GetBytes("grid");
                deflate.Write(type, 0, type.Length);
                deflate.WriteByte(0);

                using var body = new MemoryStream();
                using (var b = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    b.Write((byte)1);      // Version 1 (<4 -> only identity is parsed)
                    b.Write(gridId);       // GridId (int64)
                    b.Write(0L);           // SegmentId (single segment)
                    b.Write(123456L);      // ModifiedTime
                    b.Write(x);            // TileX
                    b.Write(y);            // TileY
                }
                var bytes = body.ToArray();
                var len = BitConverter.GetBytes(bytes.Length);
                deflate.Write(len, 0, len.Length);
                deflate.Write(bytes, 0, bytes.Length);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private async Task SeedMapWithGridsAsync(int mapId, params (long Id, int X, int Y)[] grids)
    {
        _dbContext.Maps.Add(new MapInfoEntity
        {
            Id = mapId,
            Name = $"Map{mapId}",
            TenantId = TestTenantId,
            CreatedAt = DateTime.UtcNow
        });
        foreach (var (id, x, y) in grids)
        {
            _dbContext.Grids.Add(new GridDataEntity
            {
                Id = id.ToString(),
                Map = mapId,
                CoordX = x,
                CoordY = y,
                NextUpdate = DateTime.UtcNow.AddMinutes(-1),
                TenantId = TestTenantId
            });
        }
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    [Fact]
    public async Task ImportAsync_MergeMode_SegmentBelowMinMatches_CreatesNewMapInsteadOfMerging()
    {
        // Only 4 of the file's grids match the DB — below MIN_MERGE_MATCHES. Before the fix
        // the dominant segment merged unconditionally on any match count.
        await SeedMapWithGridsAsync(1, (1, 0, 0), (2, 1, 0), (3, 2, 0), (4, 3, 0));

        using var hmap = BuildHmap((1, 0, 0), (2, 1, 0), (3, 2, 0), (4, 3, 0), (10, 4, 0), (11, 5, 0), (12, 6, 0));
        var result = await _importService.ImportAsync(hmap, TestTenantId, HmapImportMode.Merge, _gridStorage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.BelowMinMatchesAsNewMaps);
        Assert.Equal(1, result.MapsCreated);
        Assert.Single(result.CreatedMapIds);
        Assert.Equal(0, result.GridsMerged);

        // The new grids landed on the NEW map, not on map 1
        var g10 = await _gridRepository.GetGridAsync("10");
        Assert.NotNull(g10);
        Assert.Equal(result.CreatedMapIds[0], g10.Map);
        Assert.NotEqual(1, g10.Map);

        // The matched grids stayed on map 1, untouched
        var g1 = await _gridRepository.GetGridAsync("1");
        Assert.NotNull(g1);
        Assert.Equal(1, g1.Map);
    }

    [Fact]
    public async Task ImportAsync_MergeMode_ConflictingDestinationCell_CreatesNewMap()
    {
        // 5 clean matches, but one to-be-planted grid would land on a cell owned by a different
        // grid id — the segment must fall back to a new map and leave the owner untouched.
        await SeedMapWithGridsAsync(1, (1, 0, 0), (2, 1, 0), (3, 2, 0), (4, 3, 0), (5, 4, 0), (777, 5, 0));

        using var hmap = BuildHmap((1, 0, 0), (2, 1, 0), (3, 2, 0), (4, 3, 0), (5, 4, 0), (999, 5, 0), (888, 6, 0));
        var result = await _importService.ImportAsync(hmap, TestTenantId, HmapImportMode.Merge, _gridStorage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.CoordConflictsAsNewMaps);
        Assert.Equal(1, result.MapsCreated);
        Assert.Equal(0, result.GridsMerged);

        // Owner of the contested cell unchanged
        var owner = await _gridRepository.GetGridAsync("777");
        Assert.NotNull(owner);
        Assert.Equal(1, owner.Map);
        Assert.Equal(new Coord(5, 0), owner.Coord);

        // The would-be intruder went to the new map at its file coords
        var intruder = await _gridRepository.GetGridAsync("999");
        Assert.NotNull(intruder);
        Assert.Equal(result.CreatedMapIds[0], intruder.Map);
    }

    [Fact]
    public async Task ImportAsync_MergeMode_CleanOverlap_MergesIntoVotedTargetMap()
    {
        // 5 agreeing matches, conflict-free plants — the happy path must still merge (this also
        // proves removing the old fallback broke nothing).
        await SeedMapWithGridsAsync(1, (1, 0, 0), (2, 1, 0), (3, 2, 0), (4, 3, 0), (5, 4, 0));

        using var hmap = BuildHmap((1, 0, 0), (2, 1, 0), (3, 2, 0), (4, 3, 0), (5, 4, 0), (6, 5, 0), (7, 6, 0));
        var result = await _importService.ImportAsync(hmap, TestTenantId, HmapImportMode.Merge, _gridStorage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, result.MapsCreated);
        Assert.Empty(result.CreatedMapIds);
        Assert.Equal(2, result.GridsMerged);
        Assert.Equal(5, result.GridsSkipped);
        Assert.Equal(0, result.BelowMinMatchesAsNewMaps);
        Assert.Equal(0, result.CoordConflictsAsNewMaps);

        var g6 = await _gridRepository.GetGridAsync("6");
        var g7 = await _gridRepository.GetGridAsync("7");
        Assert.NotNull(g6);
        Assert.NotNull(g7);
        Assert.Equal(1, g6.Map);
        Assert.Equal(1, g7.Map);
        Assert.Equal(new Coord(5, 0), g6.Coord);
        Assert.Equal(new Coord(6, 0), g7.Coord);
    }
}
