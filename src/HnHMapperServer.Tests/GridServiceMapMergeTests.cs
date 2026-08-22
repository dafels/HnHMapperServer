using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using ITenantContextAccessor = HnHMapperServer.Core.Interfaces.ITenantContextAccessor;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HnHMapperServer.Tests;

/// <summary>
/// Integration tests for map merging functionality in GridService.
/// Verifies that when multiple maps are detected during gridUpdate:
/// 1. Source tiles are correctly looked up using pre-shift coordinates
/// 2. Tiles are saved to target map with shifted coordinates
/// 3. Zoom levels 1-6 are regenerated for all affected tiles
/// </summary>
public class GridServiceMapMergeTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly string _testGridStorage;
    private readonly GridService _gridService;
    private readonly TileService _tileService;
    private readonly IGridRepository _gridRepository;
    private readonly IMapRepository _mapRepository;
    private readonly ITileRepository _tileRepository;
    private readonly IConfigRepository _configRepository;
    private readonly Mock<IUpdateNotificationService> _mockNotificationService;
    private readonly ZoomTileQueueService _zoomTileQueue;

    private const string TestTenantId = "default-tenant-1";

    public GridServiceMapMergeTests()
    {
        // Create mock HttpContextAccessor to set tenant ID for EF Core query filters
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TestTenantId;
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        // Create in-memory database for testing
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new ApplicationDbContext(options, mockHttpContextAccessor.Object);

        // Seed default tenant for multi-tenancy support
        _dbContext.Tenants.Add(new HnHMapperServer.Core.Models.TenantEntity
        {
            Id = TestTenantId,
            Name = TestTenantId,
            StorageQuotaMB = 1024,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
        _dbContext.SaveChanges();

        // Setup test grid storage directory
        _testGridStorage = Path.Combine(Path.GetTempPath(), $"hnh-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testGridStorage);
        Directory.CreateDirectory(Path.Combine(_testGridStorage, "grids"));

        // Mock tenant context accessor
        var mockTenantContext = new Mock<ITenantContextAccessor>();
        mockTenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TestTenantId);
        mockTenantContext.Setup(x => x.GetRequiredTenantId()).Returns(TestTenantId);

        // Initialize repositories
        _gridRepository = new GridRepository(_dbContext, mockTenantContext.Object);
        _mapRepository = new MapRepository(_dbContext, mockTenantContext.Object);
        _tileRepository = new TileRepository(_dbContext, mockTenantContext.Object);
        _configRepository = new ConfigRepository(_dbContext, mockTenantContext.Object);

        // Initialize services with mocked loggers and notification service
        var tileLogger = new Mock<ILogger<TileService>>();
        var gridLogger = new Mock<ILogger<GridService>>();
        _mockNotificationService = new Mock<IUpdateNotificationService>();
        var mockNotificationService = _mockNotificationService;
        var mockQuotaService = new Mock<IStorageQuotaService>();
        var mockMapNameService = new Mock<IMapNameService>();
        mockMapNameService.Setup(x => x.GenerateUniqueIdentifierAsync(It.IsAny<string>()))
            .ReturnsAsync(() => $"test-map-{Guid.NewGuid():N}");
        var mockPendingMarkerService = new Mock<IPendingMarkerService>();

        _tileService = new TileService(
            _tileRepository,
            _gridRepository,
            mockNotificationService.Object,
            mockQuotaService.Object,
            tileLogger.Object,
            _dbContext);

        _zoomTileQueue = new ZoomTileQueueService(new Mock<ILogger<ZoomTileQueueService>>().Object);

        _gridService = new GridService(
            _gridRepository,
            _mapRepository,
            _tileService,
            _configRepository,
            mockNotificationService.Object,
            mockMapNameService.Object,
            mockPendingMarkerService.Object,
            mockTenantContext.Object,
            _zoomTileQueue,
            gridLogger.Object);

        // Seed default configuration
        _dbContext.Database.EnsureCreated();
        // Note: Config repository handles the Config entity internally
    }

    public void Dispose()
    {
        // Cleanup test database and files
        _dbContext.Dispose();
        if (Directory.Exists(_testGridStorage))
        {
            Directory.Delete(_testGridStorage, true);
        }
    }

    /// <summary>
    /// Create a test PNG tile image (100x100 with solid color)
    /// </summary>
    private async Task<string> CreateTestTileAsync(string gridId, byte r, byte g, byte b)
    {
        var filePath = Path.Combine(_testGridStorage, "grids", $"{gridId}.png");
        using var img = new Image<Rgba32>(100, 100);
        img.Mutate(ctx => ctx.BackgroundColor(new Rgba32(r, g, b)));
        await img.SaveAsPngAsync(filePath);
        return Path.Combine("grids", $"{gridId}.png");
    }

    [Fact]
    public async Task MergeMapsAsync_CorrectlyLookupsSourceTiles_AndRegeneratesZooms()
    {
        // Arrange: Create two separate maps with known grids and tiles
        // Map 1: Contains grid at (0,0) - will be the TARGET map (lower ID)
        var map1 = new MapInfo { Id = 1, Name = "Map1", Hidden = false, Priority = 0 };
        await _mapRepository.SaveMapAsync(map1);

        var grid1a = new GridData { Id = "grid1a", Map = 1, Coord = new Coord(0, 0), NextUpdate = DateTime.UtcNow.AddMinutes(-1) };
        var grid1b = new GridData { Id = "grid1b", Map = 1, Coord = new Coord(0, 1), NextUpdate = DateTime.UtcNow.AddMinutes(-1) };
        await _gridRepository.SaveGridAsync(grid1a);
        await _gridRepository.SaveGridAsync(grid1b);

        // Create test tile for map 1 (red color)
        var tile1aPath = await CreateTestTileAsync("grid1a", 255, 0, 0);
        await _tileService.SaveTileAsync(1, new Coord(0, 0), 0, tile1aPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TestTenantId, 0);

        // Map 2: Contains grids at (100,100) and (100,101) - will be MERGED into map 1
        // Using high coordinates to ensure no overlap after shift
        var map2 = new MapInfo { Id = 2, Name = "Map2", Hidden = false, Priority = 0 };
        await _mapRepository.SaveMapAsync(map2);

        var grid2a = new GridData { Id = "grid2a", Map = 2, Coord = new Coord(100, 100), NextUpdate = DateTime.UtcNow.AddMinutes(-1) };
        var grid2b = new GridData { Id = "grid2b", Map = 2, Coord = new Coord(100, 101), NextUpdate = DateTime.UtcNow.AddMinutes(-1) };
        await _gridRepository.SaveGridAsync(grid2a);
        await _gridRepository.SaveGridAsync(grid2b);

        // Create test tiles for map 2 (green color)
        var tile2aPath = await CreateTestTileAsync("grid2a", 0, 255, 0);
        var tile2bPath = await CreateTestTileAsync("grid2b", 0, 255, 0);
        await _tileService.SaveTileAsync(2, new Coord(100, 100), 0, tile2aPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TestTenantId, 0);
        await _tileService.SaveTileAsync(2, new Coord(100, 101), 0, tile2bPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TestTenantId, 0);

        // Act: Send gridUpdate that spans BOTH maps (this will trigger merge)
        // In the grid update layout (each map witnessed by TWO agreeing grids — merges only run
        // with >= MIN_MERGE_WITNESSES per map, and all witnesses of a map must agree on offset):
        // - grid1a at (x=1, y=1): coord (0,0), offset = (0-1, 0-1) = (-1, -1)
        // - grid1b at (x=1, y=2): coord (0,1), offset = (0-1, 1-2) = (-1, -1)  [agrees]
        // - grid2a at (x=2, y=1): coord (100,100), offset = (100-2, 100-1) = (98, 99)
        // - grid2b at (x=2, y=2): coord (100,101), offset = (100-2, 101-2) = (98, 99)  [agrees]
        // Map 1 (lower ID) is target. Shift = targetOffset - sourceOffset = (-1,-1) - (98,99) = (-99, -100)
        // grid2a moves to (100 + -99, 100 + -100) = (1, 0)
        // grid2b moves to (100 + -99, 101 + -100) = (1, 1)
        var gridUpdate = new GridUpdateDto
        {
            Grids = new List<List<string>>
            {
                new List<string> { "new1", "new2", "new3" },       // Row 0
                new List<string> { "new4", "grid1a", "grid1b" },   // Row 1: map 1 grids at (1,1) and (1,2)
                new List<string> { "new7", "grid2a", "grid2b" }    // Row 2: map 2 grids at (2,1) and (2,2) - triggers merge!
            }
        };

        var result = await _gridService.ProcessGridUpdateAsync(gridUpdate, _testGridStorage);

        // Assert: Verify that tiles were correctly copied and zooms generated

        // 1. Verify that map 1 still exists (it's the target map)
        var map1After = await _mapRepository.GetMapAsync(1);
        Assert.NotNull(map1After);

        // 2. Verify that map 2 was deleted (merged into map 1)
        var map2After = await _mapRepository.GetMapAsync(2);
        Assert.Null(map2After);

        // 3. Verify that base tile (zoom 0) exists for the original map 1 grid
        var baseTile1a = await _tileService.GetTileAsync(1, new Coord(0, 0), 0);
        Assert.NotNull(baseTile1a);
        Assert.Equal(tile1aPath, baseTile1a.File);

        // 4. Verify that map 2 grids were moved to map 1 with shifted coordinates
        var movedGrid2a = await _gridRepository.GetGridAsync("grid2a");
        var movedGrid2b = await _gridRepository.GetGridAsync("grid2b");
        Assert.NotNull(movedGrid2a);
        Assert.NotNull(movedGrid2b);
        Assert.Equal(1, movedGrid2a.Map); // Should now be in map 1
        Assert.Equal(1, movedGrid2b.Map); // Should now be in map 1

        // 5. Verify that tiles from map 2 were copied to map 1 at new coordinates
        var movedTile2a = await _tileService.GetTileAsync(1, movedGrid2a.Coord, 0);
        var movedTile2b = await _tileService.GetTileAsync(1, movedGrid2b.Coord, 0);
        Assert.NotNull(movedTile2a);
        Assert.NotNull(movedTile2b);
        Assert.Equal(tile2aPath, movedTile2a.File); // Should preserve original file path
        Assert.Equal(tile2bPath, movedTile2b.File);

        // 6. Verify that zoom level 1 was generated for the merged tiles
        // The parent of the moved tiles should have a zoom tile
        var zoom1Coord = movedGrid2a.Coord.Parent();
        var zoom1Tile = await _tileService.GetTileAsync(1, zoom1Coord, 1);
        Assert.NotNull(zoom1Tile);
        Assert.NotEmpty(zoom1Tile.File);

        // Verify the zoom 1 tile file actually exists
        var zoom1FilePath = Path.Combine(_testGridStorage, zoom1Tile.File);
        Assert.True(File.Exists(zoom1FilePath), $"Zoom 1 tile file should exist at {zoom1FilePath}");

        // 7. Verify higher zoom levels were generated (zoom 2-6)
        var currentCoord = zoom1Coord;
        for (int z = 2; z <= 6; z++)
        {
            currentCoord = currentCoord.Parent();
            var zoomTile = await _tileService.GetTileAsync(1, currentCoord, z);
            Assert.NotNull(zoomTile);
            Assert.NotEmpty(zoomTile.File);

            var zoomFilePath = Path.Combine(_testGridStorage, zoomTile.File);
            Assert.True(File.Exists(zoomFilePath), $"Zoom {z} tile file should exist at {zoomFilePath}");
        }

        // 8. REGRESSION: the retired source map must leave no tile rows behind. Merges used to
        // orphan the source map's whole tile set (10k+ rows per merge observed in prod).
        var orphanedSourceRows = await _dbContext.Tiles
            .IgnoreQueryFilters()
            .Where(t => t.MapId == 2)
            .CountAsync();
        Assert.Equal(0, orphanedSourceRows);

        // 9. The merged-in area's WebP cells must be queued for force-regeneration — the target
        // map's existing WebP files predate the merge and are only refreshed via this queue.
        var queued = new List<ZoomTileRequest>();
        while (_zoomTileQueue.Reader.TryRead(out var request))
        {
            queued.Add(request);
        }
        Assert.Contains(queued, r => r.MapId == 1 && r.TenantId == TestTenantId
            && r.BaseX == 0 && r.BaseY == 0); // cell (0,0) covers the moved grids at (1,0)/(1,1)
    }

    [Fact]
    public async Task MergeMapsAsync_PreservesSourceTileFiles_WhenShiftingCoordinates()
    {
        // Arrange: Create a source map with a single grid and tile
        var sourceMap = new MapInfo { Id = 10, Name = "SourceMap", Hidden = false, Priority = 0 };
        await _mapRepository.SaveMapAsync(sourceMap);

        var sourceGrid = new GridData
        {
            Id = "sourceGrid",
            Map = 10,
            Coord = new Coord(3, 4), // Original coordinate
            NextUpdate = DateTime.UtcNow.AddMinutes(-1)
        };
        await _gridRepository.SaveGridAsync(sourceGrid);
        // Second agreeing witness for map 10 — merges need >= MIN_MERGE_WITNESSES per map
        await _gridRepository.SaveGridAsync(new GridData
        {
            Id = "sourceGrid2",
            Map = 10,
            Coord = new Coord(3, 5),
            NextUpdate = DateTime.UtcNow.AddMinutes(-1)
        });

        // Create a blue test tile
        var sourceTilePath = await CreateTestTileAsync("sourceGrid", 0, 0, 255);
        await _tileService.SaveTileAsync(10, new Coord(3, 4), 0, sourceTilePath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TestTenantId, 0);

        // Create a target map
        var targetMap = new MapInfo { Id = 20, Name = "TargetMap", Hidden = false, Priority = 1 }; // Higher priority
        await _mapRepository.SaveMapAsync(targetMap);

        var targetGrid = new GridData
        {
            Id = "targetGrid",
            Map = 20,
            Coord = new Coord(0, 0),
            NextUpdate = DateTime.UtcNow.AddMinutes(-1)
        };
        await _gridRepository.SaveGridAsync(targetGrid);
        // Second agreeing witness for map 20
        await _gridRepository.SaveGridAsync(new GridData
        {
            Id = "targetGrid2",
            Map = 20,
            Coord = new Coord(0, 1),
            NextUpdate = DateTime.UtcNow.AddMinutes(-1)
        });

        // Act: Trigger merge by sending gridUpdate spanning both maps
        // targetGrid @(0,0) offset (0,0); targetGrid2 @(0,1) offset (0,0) — agree
        // sourceGrid @(1,0) offset (2,4); sourceGrid2 @(1,1) offset (2,4) — agree
        var gridUpdate = new GridUpdateDto
        {
            Grids = new List<List<string>>
            {
                new List<string> { "targetGrid", "targetGrid2", "new3" },
                new List<string> { "sourceGrid", "sourceGrid2", "new6" },  // This will cause sourceGrid to shift
                new List<string> { "new7", "new8", "new9" }
            }
        };

        await _gridService.ProcessGridUpdateAsync(gridUpdate, _testGridStorage);

        // Assert: Verify the tile was copied with correct file reference
        
        // The sourceGrid should now be on targetMap (map 20) at a shifted coordinate
        var movedGrid = await _gridRepository.GetGridAsync("sourceGrid");
        Assert.NotNull(movedGrid);
        Assert.Equal(20, movedGrid.Map); // Should be moved to target map

        // The tile should exist at the NEW coordinate on the target map
        var movedTile = await _tileService.GetTileAsync(20, movedGrid.Coord, 0);
        Assert.NotNull(movedTile);
        Assert.Equal(sourceTilePath, movedTile.File); // Should preserve original file path

        // The original file should still exist
        var originalFilePath = Path.Combine(_testGridStorage, sourceTilePath);
        Assert.True(File.Exists(originalFilePath), "Original tile file should still exist after merge");
    }

    [Fact]
    public async Task MergeMapsAsync_NotifiesMergeAndSourceDeletion_WithTenantId()
    {
        // Arrange: two maps with one grid each and NO tiles. The old tenant lookup only
        // yielded a tenant when a zoom-0 tile happened to be copied during the merge, so
        // the no-tile case used to broadcast the merge with tenantId = "" (which the SSE
        // tenant filter silently drops). This is the regression case.
        var map1 = new MapInfo { Id = 1, Name = "Target", Hidden = false, Priority = 0 };
        var map2 = new MapInfo { Id = 2, Name = "Source", Hidden = false, Priority = 0 };
        await _mapRepository.SaveMapAsync(map1);
        await _mapRepository.SaveMapAsync(map2);

        await _gridRepository.SaveGridAsync(new GridData { Id = "t1", Map = 1, Coord = new Coord(0, 0), NextUpdate = DateTime.UtcNow.AddMinutes(-1) });
        await _gridRepository.SaveGridAsync(new GridData { Id = "t2", Map = 1, Coord = new Coord(0, 1), NextUpdate = DateTime.UtcNow.AddMinutes(-1) });
        await _gridRepository.SaveGridAsync(new GridData { Id = "s1", Map = 2, Coord = new Coord(50, 50), NextUpdate = DateTime.UtcNow.AddMinutes(-1) });
        await _gridRepository.SaveGridAsync(new GridData { Id = "s2", Map = 2, Coord = new Coord(50, 51), NextUpdate = DateTime.UtcNow.AddMinutes(-1) });

        // Act: gridUpdate spanning both maps triggers the merge (target = lower id).
        // Two agreeing witnesses per map: t1@(0,0)/t2@(0,1) offset (0,0); s1@(1,0)/s2@(1,1) offset (49,50).
        var gridUpdate = new GridUpdateDto
        {
            Grids = new List<List<string>>
            {
                new List<string> { "t1", "t2", "x3" },
                new List<string> { "s1", "s2", "x6" },
                new List<string> { "x7", "x8", "x9" }
            }
        };

        await _gridService.ProcessGridUpdateAsync(gridUpdate, _testGridStorage);

        // Assert: merge broadcast carries the real tenant, and the deleted source map is
        // announced so viewers drop it from their map selectors
        _mockNotificationService.Verify(x => x.NotifyMapMerge(2, 1, It.IsAny<Coord>(), TestTenantId), Times.Once);
        _mockNotificationService.Verify(x => x.NotifyMapDeleted(2), Times.Once);
        Assert.Null(await _mapRepository.GetMapAsync(2));
        Assert.NotNull(await _mapRepository.GetMapAsync(1));
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_NewMap_NotifiesMapUpdatedWithTenantId()
    {
        // Act: all-unknown grids -> new-map branch
        var gridUpdate = new GridUpdateDto
        {
            Grids = new List<List<string>>
            {
                new List<string> { "n1", "n2", "n3" },
                new List<string> { "n4", "n5", "n6" },
                new List<string> { "n7", "n8", "n9" }
            }
        };

        var result = await _gridService.ProcessGridUpdateAsync(gridUpdate, _testGridStorage);

        // Assert: viewers are told about the new map, with the generated id and the
        // tenant set (the SSE loop filters events by TenantId, so "" would never deliver)
        Assert.True(result.Map > 0);
        _mockNotificationService.Verify(x => x.NotifyMapUpdated(It.Is<MapInfo>(m =>
            m.Id == result.Map && m.TenantId == TestTenantId && m.Priority == -1)), Times.Once);
        _mockNotificationService.Verify(x => x.NotifyMapMerge(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Coord>(), It.IsAny<string>()), Times.Never);
    }
}

