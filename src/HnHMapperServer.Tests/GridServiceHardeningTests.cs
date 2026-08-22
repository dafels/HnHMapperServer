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

namespace HnHMapperServer.Tests;

/// <summary>
/// Guard-rail tests for ProcessGridUpdateAsync hardening (the 2026-08 map-corruption incident):
/// placeholder grid ids ("0"/empty) are holes and never persist or anchor; matrices whose known
/// grids disagree on the implied offset are rejected with zero writes; inserts refuse cells
/// already owned by a different grid id; merges (irreversible) require two agreeing witnesses
/// per map. Uses the EF InMemory provider like GridServiceMapMergeTests — the paths under test
/// are plain repository CRUD, no ExecuteDelete/raw SQL.
/// </summary>
public class GridServiceHardeningTests : IDisposable
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

    private const string TestTenantId = "default-tenant-1";

    public GridServiceHardeningTests()
    {
        // Create mock HttpContextAccessor to set tenant ID for EF Core query filters
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TestTenantId;
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

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

        _testGridStorage = Path.Combine(Path.GetTempPath(), $"hnh-hardening-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testGridStorage);
        Directory.CreateDirectory(Path.Combine(_testGridStorage, "grids"));

        var mockTenantContext = new Mock<ITenantContextAccessor>();
        mockTenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TestTenantId);
        mockTenantContext.Setup(x => x.GetRequiredTenantId()).Returns(TestTenantId);

        _gridRepository = new GridRepository(_dbContext, mockTenantContext.Object);
        _mapRepository = new MapRepository(_dbContext, mockTenantContext.Object);
        _tileRepository = new TileRepository(_dbContext, mockTenantContext.Object);
        _configRepository = new ConfigRepository(_dbContext, mockTenantContext.Object);

        var tileLogger = new Mock<ILogger<TileService>>();
        var gridLogger = new Mock<ILogger<GridService>>();
        _mockNotificationService = new Mock<IUpdateNotificationService>();
        var mockQuotaService = new Mock<IStorageQuotaService>();
        var mockMapNameService = new Mock<IMapNameService>();
        mockMapNameService.Setup(x => x.GenerateUniqueIdentifierAsync(It.IsAny<string>()))
            .ReturnsAsync(() => $"test-map-{Guid.NewGuid():N}");
        var mockPendingMarkerService = new Mock<IPendingMarkerService>();

        _tileService = new TileService(
            _tileRepository,
            _gridRepository,
            _mockNotificationService.Object,
            mockQuotaService.Object,
            tileLogger.Object,
            _dbContext);

        _gridService = new GridService(
            _gridRepository,
            _mapRepository,
            _tileService,
            _configRepository,
            _mockNotificationService.Object,
            mockMapNameService.Object,
            mockPendingMarkerService.Object,
            mockTenantContext.Object,
            new ZoomTileQueueService(new Mock<ILogger<ZoomTileQueueService>>().Object),
            gridLogger.Object);

        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (Directory.Exists(_testGridStorage))
        {
            Directory.Delete(_testGridStorage, true);
        }
    }

    private async Task SeedMapWithGridsAsync(int mapId, params (string Id, int X, int Y)[] grids)
    {
        await _mapRepository.SaveMapAsync(new MapInfo { Id = mapId, Name = $"Map{mapId}", Hidden = false, Priority = 0 });
        foreach (var (id, x, y) in grids)
        {
            await _gridRepository.SaveGridAsync(new GridData
            {
                Id = id,
                Map = mapId,
                Coord = new Coord(x, y),
                NextUpdate = DateTime.UtcNow.AddMinutes(-1)
            });
        }
    }

    private static GridUpdateDto Matrix(params string[] rows)
    {
        // Each row is a comma-separated triple; "~" stands for a placeholder "0" cell.
        var dto = new GridUpdateDto();
        foreach (var row in rows)
        {
            dto.Grids.Add(row.Split(',').Select(c => c == "~" ? "0" : c).ToList());
        }
        return dto;
    }

    // ---------- D1: placeholder cells ----------

    [Fact]
    public async Task ProcessGridUpdateAsync_PlaceholderZeroCells_AreNotPersisted()
    {
        await SeedMapWithGridsAsync(1, ("a1", 5, 5));

        // a1 at matrix (1,1) -> offset (4,4); "0" cells are holes; r1/r2 are new territory
        var result = await _gridService.ProcessGridUpdateAsync(
            Matrix("~,r1,~", "~,a1,~", "~,r2,~"), _testGridStorage);

        Assert.Null(await _gridRepository.GetGridAsync("0"));
        var r1 = await _gridRepository.GetGridAsync("r1");
        var r2 = await _gridRepository.GetGridAsync("r2");
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal(new Coord(4, 5), r1.Coord); // (0+4, 1+4)
        Assert.Equal(new Coord(6, 5), r2.Coord); // (2+4, 1+4)
        Assert.Contains("r1", result.GridRequests);
        Assert.Contains("r2", result.GridRequests);
        Assert.DoesNotContain("0", result.GridRequests);
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_AllPlaceholderMatrix_NoMapCreatedAndEmptyResponse()
    {
        var result = await _gridService.ProcessGridUpdateAsync(
            new GridUpdateDto
            {
                Grids = new List<List<string>>
                {
                    new() { "0", "", "0" },
                    new() { "   ", "0", "" },
                    new() { "0", "0", "0" }
                }
            }, _testGridStorage);

        Assert.Empty(result.GridRequests);
        Assert.Equal(0, result.Map);
        Assert.False(await _gridRepository.AnyGridsExistAsync());
        _mockNotificationService.Verify(x => x.NotifyMapUpdated(It.IsAny<MapInfo>()), Times.Never);
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_StoredPlaceholderGrid_DoesNotActAsAnchor()
    {
        // A legacy "0" row exists in the DB (pre-hardening corruption). It must never hijack a
        // matrix as an anchor: a matrix of fresh grids containing a "0" cell goes to a NEW map.
        await SeedMapWithGridsAsync(1, ("0", 18, 23));

        var result = await _gridService.ProcessGridUpdateAsync(
            Matrix("f1,f2,f3", "f4,~,f6", "f7,f8,f9"), _testGridStorage);

        Assert.True(result.Map > 1); // new map, not the legacy row's map
        var f1 = await _gridRepository.GetGridAsync("f1");
        Assert.NotNull(f1);
        Assert.Equal(result.Map, f1.Map);

        // The legacy row itself is untouched (cleanup is the migration's job)
        var legacy = await _gridRepository.GetGridAsync("0");
        Assert.NotNull(legacy);
        Assert.Equal(1, legacy.Map);
        Assert.Equal(new Coord(18, 23), legacy.Coord);
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_NewMapBranch_SkipsPlaceholderCells()
    {
        var result = await _gridService.ProcessGridUpdateAsync(
            Matrix("n1,~,n3", "~,n5,~", "n7,~,n9"), _testGridStorage);

        Assert.True(result.Map > 0);
        Assert.Null(await _gridRepository.GetGridAsync("0"));
        var n5 = await _gridRepository.GetGridAsync("n5");
        Assert.NotNull(n5);
        Assert.Equal(new Coord(0, 0), n5.Coord); // center cell (1,1) -> (x-1, y-1)
        Assert.Equal(new[] { "n1", "n3", "n5", "n7", "n9" }.OrderBy(s => s),
            result.GridRequests.OrderBy(s => s));
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_NullAndWhitespaceCells_TreatedAsPlaceholders()
    {
        var result = await _gridService.ProcessGridUpdateAsync(
            new GridUpdateDto
            {
                Grids = new List<List<string>>
                {
                    new() { null!, "", "   " },
                    new() { "0", "real1", null! },
                    new() { "", "   ", "0" }
                }
            }, _testGridStorage);

        Assert.True(result.Map > 0);
        var real = await _gridRepository.GetGridAsync("real1");
        Assert.NotNull(real);
        Assert.Equal(new Coord(0, 0), real.Coord);
        Assert.Equal(new[] { "real1" }, result.GridRequests);
    }

    // ---------- D2: offset-consistency rejection ----------

    [Fact]
    public async Task ProcessGridUpdateAsync_ConflictingOffsetsForSameMap_RejectsEntireUpdate()
    {
        // c1 at matrix (0,0) implies offset (0,0); c2 at matrix (0,1) implies (10,9) — a
        // contradiction: this matrix mixes coordinate frames and must not write anything.
        await SeedMapWithGridsAsync(1, ("c1", 0, 0), ("c2", 10, 10));

        var result = await _gridService.ProcessGridUpdateAsync(
            Matrix("c1,c2,u1", "u2,u3,u4", "u5,u6,u7"), _testGridStorage);

        Assert.Empty(result.GridRequests);
        Assert.Equal(0, result.Map);
        Assert.Null(await _gridRepository.GetGridAsync("u1"));
        Assert.Null(await _gridRepository.GetGridAsync("u5"));
        _mockNotificationService.Verify(x => x.NotifyMapUpdated(It.IsAny<MapInfo>()), Times.Never);

        // Stored grids untouched
        var c2 = await _gridRepository.GetGridAsync("c2");
        Assert.NotNull(c2);
        Assert.Equal(new Coord(10, 10), c2.Coord);
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_ConflictingOffsets_DoesNotMergeOrNotify()
    {
        // Map 1's witnesses disagree with each other; map 2's agree. The whole update is
        // rejected — no merge may run off a matrix that is provably frame-mixed.
        await SeedMapWithGridsAsync(1, ("m1a", 0, 0), ("m1b", 5, 5));
        await SeedMapWithGridsAsync(2, ("m2a", 50, 50), ("m2b", 50, 51));

        await _gridService.ProcessGridUpdateAsync(
            Matrix("m1a,m1b,u1", "u2,u3,u4", "m2a,m2b,u5"), _testGridStorage);

        Assert.NotNull(await _mapRepository.GetMapAsync(1));
        Assert.NotNull(await _mapRepository.GetMapAsync(2));
        Assert.Null(await _gridRepository.GetGridAsync("u1"));
        _mockNotificationService.Verify(x => x.NotifyMapMerge(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Coord>(), It.IsAny<string>()), Times.Never);
        _mockNotificationService.Verify(x => x.NotifyMapDeleted(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_MultipleWitnessesAgreeingAtDifferentPositions_NotRejected()
    {
        // Same map witnessed at two matrix corners with consistent stored coords — the offset
        // set collapses to one entry and processing proceeds normally.
        await SeedMapWithGridsAsync(1, ("w1", 0, 0), ("w2", 2, 2));

        var result = await _gridService.ProcessGridUpdateAsync(
            Matrix("w1,u1,u2", "u3,u4,u5", "u6,u7,w2"), _testGridStorage);

        Assert.Equal(1, result.Map);
        var u4 = await _gridRepository.GetGridAsync("u4");
        Assert.NotNull(u4);
        Assert.Equal(new Coord(1, 1), u4.Coord);
        Assert.Contains("u4", result.GridRequests);
    }

    // ---------- D3: occupied-cell guard ----------

    [Fact]
    public async Task ProcessGridUpdateAsync_DestinationCellOwnedByOtherGrid_SkipsThatCellOnly()
    {
        // "owner" holds cell (1,1) but is NOT in the matrix; the matrix wants to put "intruder"
        // there. The intruder is skipped; every other cell processes normally.
        await SeedMapWithGridsAsync(1, ("a1", 0, 0), ("owner", 1, 1));

        var result = await _gridService.ProcessGridUpdateAsync(
            Matrix("a1,u1,u2", "u3,intruder,u4", "u5,u6,u7"), _testGridStorage);

        Assert.Null(await _gridRepository.GetGridAsync("intruder"));
        var owner = await _gridRepository.GetGridAsync("owner");
        Assert.NotNull(owner);
        Assert.Equal(new Coord(1, 1), owner.Coord);
        Assert.Equal(1, owner.Map);

        var u1 = await _gridRepository.GetGridAsync("u1");
        Assert.NotNull(u1);
        Assert.Equal(new Coord(0, 1), u1.Coord);
        Assert.DoesNotContain("intruder", result.GridRequests);
        Assert.Contains("u1", result.GridRequests);
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_OccupiedCellSkip_StillReturnsKnownDueGrids()
    {
        // A skipped intruder must not suppress refresh requests for known grids past NextUpdate.
        await SeedMapWithGridsAsync(1, ("a1", 0, 0), ("owner", 1, 1));

        var result = await _gridService.ProcessGridUpdateAsync(
            Matrix("a1,u1,u2", "u3,intruder,u4", "u5,u6,u7"), _testGridStorage);

        Assert.Contains("a1", result.GridRequests); // seeded with past NextUpdate -> due
    }

    // ---------- D4: two-witness merge gating ----------

    [Fact]
    public async Task ProcessGridUpdateAsync_SourceMapWithSingleWitness_MergeDeferred()
    {
        await SeedMapWithGridsAsync(1, ("t1", 0, 0), ("t2", 0, 1));
        await SeedMapWithGridsAsync(2, ("s1", 50, 50));

        var result = await _gridService.ProcessGridUpdateAsync(
            Matrix("t1,t2,u1", "u2,u3,u4", "s1,u5,u6"), _testGridStorage);

        // Both maps survive; the lone-witness source stays where it was
        Assert.NotNull(await _mapRepository.GetMapAsync(1));
        Assert.NotNull(await _mapRepository.GetMapAsync(2));
        var s1 = await _gridRepository.GetGridAsync("s1");
        Assert.NotNull(s1);
        Assert.Equal(2, s1.Map);
        Assert.Equal(new Coord(50, 50), s1.Coord);
        _mockNotificationService.Verify(x => x.NotifyMapMerge(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Coord>(), It.IsAny<string>()), Times.Never);

        // Frontier inserts against the target still happened
        var u3 = await _gridRepository.GetGridAsync("u3");
        Assert.NotNull(u3);
        Assert.Equal(1, u3.Map);
        Assert.Equal(new Coord(1, 1), u3.Coord);
        Assert.Equal(1, result.Map);
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_TargetMapWithSingleWitness_MergeDeferred()
    {
        // Target (lowest id) has one witness; source has two. The destination frame is
        // single-anchored, so no merge runs at all this round.
        await SeedMapWithGridsAsync(1, ("t1", 0, 0));
        await SeedMapWithGridsAsync(2, ("s1", 50, 50), ("s2", 50, 51));

        await _gridService.ProcessGridUpdateAsync(
            Matrix("t1,u1,u2", "s1,s2,u3", "u4,u5,u6"), _testGridStorage);

        Assert.NotNull(await _mapRepository.GetMapAsync(1));
        Assert.NotNull(await _mapRepository.GetMapAsync(2));
        _mockNotificationService.Verify(x => x.NotifyMapMerge(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Coord>(), It.IsAny<string>()), Times.Never);
        _mockNotificationService.Verify(x => x.NotifyMapDeleted(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_BothMapsTwoAgreeingWitnesses_MergesAsBefore()
    {
        await SeedMapWithGridsAsync(1, ("t1", 0, 0), ("t2", 0, 1));
        await SeedMapWithGridsAsync(2, ("s1", 50, 50), ("s2", 50, 51));

        await _gridService.ProcessGridUpdateAsync(
            Matrix("t1,t2,u1", "s1,s2,u2", "u3,u4,u5"), _testGridStorage);

        _mockNotificationService.Verify(x => x.NotifyMapMerge(2, 1, It.IsAny<Coord>(), TestTenantId), Times.Once);
        Assert.Null(await _mapRepository.GetMapAsync(2));
        var s1 = await _gridRepository.GetGridAsync("s1");
        Assert.NotNull(s1);
        Assert.Equal(1, s1.Map);
    }

    [Fact]
    public async Task ProcessGridUpdateAsync_ThreeMaps_MergesOnlyTwoWitnessEligibleSources()
    {
        await SeedMapWithGridsAsync(1, ("t1", 0, 0), ("t2", 0, 1));
        await SeedMapWithGridsAsync(2, ("a1", 50, 50), ("a2", 50, 51));
        await SeedMapWithGridsAsync(3, ("b1", 90, 90));

        await _gridService.ProcessGridUpdateAsync(
            Matrix("t1,t2,u1", "a1,a2,u2", "b1,u3,u4"), _testGridStorage);

        // Eligible source (map 2) merged; single-witness map 3 untouched
        Assert.Null(await _mapRepository.GetMapAsync(2));
        Assert.NotNull(await _mapRepository.GetMapAsync(3));
        _mockNotificationService.Verify(x => x.NotifyMapMerge(2, 1, It.IsAny<Coord>(), TestTenantId), Times.Once);
        _mockNotificationService.Verify(x => x.NotifyMapMerge(3, It.IsAny<int>(), It.IsAny<Coord>(), It.IsAny<string>()), Times.Never);
        var b1 = await _gridRepository.GetGridAsync("b1");
        Assert.NotNull(b1);
        Assert.Equal(3, b1.Map);
        Assert.Equal(new Coord(90, 90), b1.Coord);
    }

    // ---------- Frontier regression ----------

    [Fact]
    public async Task ProcessGridUpdateAsync_SingleKnownAnchor_FrontierInsertsStillWork()
    {
        // The canonical walking case: one known grid, eight fresh neighbors. Witness gating
        // applies only to merges — a single anchor must keep registering new territory.
        await SeedMapWithGridsAsync(1, ("k", 10, 10));

        var result = await _gridService.ProcessGridUpdateAsync(
            Matrix("f1,f2,f3", "f4,k,f5", "f6,f7,f8"), _testGridStorage);

        Assert.Equal(1, result.Map);
        Assert.Equal(new Coord(10, 10), result.Coords);

        var expected = new Dictionary<string, Coord>
        {
            ["f1"] = new(9, 9),
            ["f2"] = new(9, 10),
            ["f3"] = new(9, 11),
            ["f4"] = new(10, 9),
            ["f5"] = new(10, 11),
            ["f6"] = new(11, 9),
            ["f7"] = new(11, 10),
            ["f8"] = new(11, 11)
        };
        foreach (var (id, coord) in expected)
        {
            var grid = await _gridRepository.GetGridAsync(id);
            Assert.NotNull(grid);
            Assert.Equal(1, grid.Map);
            Assert.Equal(coord, grid.Coord);
            Assert.Contains(id, result.GridRequests);
        }
    }
}
