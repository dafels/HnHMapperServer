using System.Text;
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
/// End-to-end cover for the marker phase of an .hmap import, against the current game
/// client's tagged (version 4) "mark" records — the layout that used to be rejected by the
/// reader, so an import of a modern export placed zero markers on the map.
///
/// Also pins the grid lookup for negative tile coordinates. Truncating division is the
/// primary lookup because the game client's own marker uploads decompose that way (which
/// is why ~45% of live marker rows carry a negative in-grid offset, and why re-importing
/// must produce the same Key to dedupe). It names the cell one short of the containing
/// grid, so when the export has no grid there the marker used to be dropped even though
/// its own grid was in the file — that is the floor fallback.
///
/// Same offline harness as HmapImportServiceDuplicateCellTests (kept-open SQLite
/// ":memory:", synthetic grid records that render gray) and the same collection, because
/// ImportAsync's global import lock is try-acquire and parallel classes reject each other.
/// </summary>
[Collection("HmapImportGlobalLock")]
public class HmapImportServiceMarkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly string _gridStorage;
    private readonly HmapImportService _importService;
    private readonly List<(string GridId, int X, int Y, string Name, string Image)> _capturedMarkers = new();

    private const string TestTenantId = "default-tenant-1";
    private const long SegmentId = -1866084793332318016L;

    public HmapImportServiceMarkerTests()
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

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-hmapmark-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        var mockTenantContext = new Mock<ITenantContextAccessor>();
        mockTenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TestTenantId);
        mockTenantContext.Setup(x => x.GetRequiredTenantId()).Returns(TestTenantId);

        var gridRepository = new GridRepository(_dbContext, mockTenantContext.Object);
        var mapRepository = new MapRepository(_dbContext, mockTenantContext.Object);
        var tileRepository = new TileRepository(_dbContext, mockTenantContext.Object);

        var mockNotificationService = new Mock<IUpdateNotificationService>();
        var quotaServiceMock = new Mock<IStorageQuotaService>();
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
            gridRepository,
            mockNotificationService.Object,
            quotaServiceMock.Object,
            Mock.Of<ILogger<TileService>>(),
            _dbContext);

        _importService = new HmapImportService(
            gridRepository,
            mapRepository,
            tileService,
            tileRepository,
            Mock.Of<IOverlayDataRepository>(),
            quotaServiceMock.Object,
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
        try { if (Directory.Exists(_gridStorage)) Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    [Fact]
    public async Task ImportAsync_TaggedMarkerRecords_AreImportedAtTheCorrectWorldTile()
    {
        // Pre-fix this asserted nothing: the reader dropped every version-4 record, so the
        // marker phase never ran and the result reported 0 imported / 0 skipped.
        // Grid cell (-9,-5) contains tile (-821,-435); cell (-8,-4) is the truncating one.
        using var hmap = BuildHmap(
            grids: new[] { (1L, -9, -5, 100L), (2L, -8, -4, 100L) },
            markers: new[] { (-821, -435, "Fairy Stone", "gfx/terobjs/mm/fairystone") });

        var result = await _importService.ImportAsync(
            hmap, TestTenantId, HmapImportMode.CreateNew, _gridStorage);

        Assert.True(result.Success);
        Assert.Equal(1, result.MarkersImported);
        Assert.Equal(0, result.MarkersSkipped);

        var marker = Assert.Single(_capturedMarkers);
        Assert.Equal("Fairy Stone", marker.Name);
        Assert.Equal("gfx/terobjs/mm/fairystone", marker.Image);

        // Whichever cell it attached to, cell * 100 + position must be the original tile —
        // that sum is exactly what FrontendMarker hands the map viewer.
        var grid = await _dbContext.Grids.AsNoTracking().FirstAsync(g => g.Id == marker.GridId);
        Assert.Equal(-821, grid.CoordX * 100 + marker.X);
        Assert.Equal(-435, grid.CoordY * 100 + marker.Y);
    }

    [Fact]
    public async Task ImportAsync_NegativeCoordMarker_FallsBackToTheContainingGrid()
    {
        // Only the grid that actually contains the marker is exported. The truncating cell
        // (0,0) has no grid, which used to skip the marker outright.
        using var hmap = BuildHmap(
            grids: new[] { (1L, -1, -1, 100L) },
            markers: new[] { (-50, -50, "Burrow", "gfx/terobjs/mm/burrow") });

        var result = await _importService.ImportAsync(
            hmap, TestTenantId, HmapImportMode.CreateNew, _gridStorage);

        Assert.True(result.Success);
        Assert.Equal(1, result.MarkersImported);
        Assert.Equal(0, result.MarkersSkipped);

        var marker = Assert.Single(_capturedMarkers);
        Assert.Equal("1", marker.GridId);
        Assert.Equal(50, marker.X);
        Assert.Equal(50, marker.Y);
    }

    [Fact]
    public async Task ImportAsync_UndecodableMarkerRecords_AreReportedAsSkipped()
    {
        // A future layout change must surface as "N markers skipped" plus a warning,
        // instead of the silent zero that hid this bug.
        using var hmap = BuildHmap(
            grids: new[] { (1L, 0, 0, 100L) },
            markers: Array.Empty<(int, int, string, string)>(),
            corruptMarkerRecords: 2);

        var result = await _importService.ImportAsync(
            hmap, TestTenantId, HmapImportMode.CreateNew, _gridStorage);

        Assert.True(result.Success);
        Assert.Equal(0, result.MarkersImported);
        Assert.Equal(2, result.MarkersSkipped);
        Assert.Empty(_capturedMarkers);
    }

    /// <summary>
    /// Builds a synthetic .hmap: version-1 grid records (identity only, rendered gray) plus
    /// version-4 tagged SMarker records in the layout current game clients export.
    /// </summary>
    private static MemoryStream BuildHmap(
        (long gridId, int x, int y, long mtime)[] grids,
        (int tileX, int tileY, string name, string resource)[] markers,
        int corruptMarkerRecords = 0)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("Haven Mapfile 1"));
        ms.WriteByte(0x78);
        ms.WriteByte(0xDA);
        using (var deflate = new System.IO.Compression.DeflateStream(
                   ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            void WriteRecord(string type, byte[] bytes)
            {
                deflate.Write(Encoding.UTF8.GetBytes(type));
                deflate.WriteByte(0);
                deflate.Write(BitConverter.GetBytes(bytes.Length));
                deflate.Write(bytes);
            }

            foreach (var (gridId, x, y, mtime) in grids)
            {
                using var body = new MemoryStream();
                using (var b = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
                {
                    b.Write((byte)1);   // grid version 1 (<4 -> only identity is parsed)
                    b.Write(gridId);
                    b.Write(SegmentId);
                    b.Write(mtime);
                    b.Write(x);
                    b.Write(y);
                }
                WriteRecord("grid", body.ToArray());
            }

            foreach (var (tileX, tileY, name, resource) in markers)
                WriteRecord("mark", TaggedSMarker(tileX, tileY, name, resource));

            for (var i = 0; i < corruptMarkerRecords; i++)
                WriteRecord("mark", new byte[] { 9, 0xEE, 0xEE, 0xEE });
        }
        ms.Position = 0;
        return ms;
    }

    private static byte[] TaggedSMarker(int tileX, int tileY, string name, string resource)
    {
        var buf = new MemoryStream();
        buf.WriteByte(4);       // marker version 4
        buf.WriteByte(0x20);    // marker kind

        void Key(string key)
        {
            buf.WriteByte(0x02); // T_STR
            buf.Write(Encoding.UTF8.GetBytes(key));
            buf.WriteByte(0);
        }

        Key("res");
        buf.WriteByte(0x22);     // T_RESID
        buf.Write(Encoding.UTF8.GetBytes(resource));
        buf.WriteByte(0);
        buf.Write(BitConverter.GetBytes((ushort)1));

        Key("c");
        buf.WriteByte(0x03);     // T_COORD
        buf.Write(BitConverter.GetBytes(tileX));
        buf.Write(BitConverter.GetBytes(tileY));

        Key("seg");
        buf.WriteByte(0x0D);     // T_UID
        buf.Write(BitConverter.GetBytes(SegmentId));

        Key("oid");
        buf.WriteByte(0x0D);
        buf.Write(BitConverter.GetBytes(0L));

        Key("nm");
        buf.WriteByte(0x02);
        buf.Write(Encoding.UTF8.GetBytes(name));
        buf.WriteByte(0);

        buf.WriteByte(0x00);     // T_END
        return buf.ToArray();
    }
}
