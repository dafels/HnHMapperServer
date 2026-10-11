using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ITenantContextAccessor = HnHMapperServer.Core.Interfaces.ITenantContextAccessor;

namespace HnHMapperServer.Tests;

/// <summary>
/// The storage charge of TileService's file writers equals the change on disk: replacements are
/// charged by size change (grid refreshes and zoom rebuilds overwrite the same file — charging
/// the full size each time drifted CurrentStorageMB above disk), and a retired map's directories
/// credit the freed legacy bytes but never the WebP pyramid (reconciled-only). The quota service
/// is a mock whose callback sums the deltas.
/// </summary>
public class TileServiceStorageAccountingTests : IDisposable
{
    private const string TenantId = "accounting-tenant";
    private const double MiB = 1024.0 * 1024.0;

    private readonly ApplicationDbContext _db;
    private readonly TileService _tileService;
    private readonly string _gridStorage;
    private readonly List<long> _charged = new();

    public TileServiceStorageAccountingTests()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TenantId;
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            accessor.Object);

        var tenantContext = new Mock<ITenantContextAccessor>();
        tenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TenantId);
        tenantContext.Setup(x => x.GetRequiredTenantId()).Returns(TenantId);

        var quota = new Mock<IStorageQuotaService>();
        quota.Setup(q => q.IncrementStorageUsageAsync(TenantId, It.IsAny<double>()))
            .Callback<string, double>((_, mb) => _charged.Add((long)Math.Round(mb * MiB)))
            .Returns(Task.CompletedTask);

        _tileService = new TileService(
            new TileRepository(_db, tenantContext.Object),
            new GridRepository(_db, tenantContext.Object),
            Mock.Of<IUpdateNotificationService>(),
            quota.Object,
            Mock.Of<ILogger<TileService>>(),
            _db);

        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-accounting-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        _db.Maps.AddRange(
            new MapInfoEntity { Id = 1, Name = "live", TenantId = TenantId, CreatedAt = DateTime.UtcNow },
            new MapInfoEntity { Id = 5, Name = "retired", TenantId = TenantId, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    private string TenantDir => Path.Combine(_gridStorage, "tenants", TenantId);

    /// <summary>Bytes under tenants/{t}/ excluding the reconciled-only WebP pyramid.</summary>
    private long NonWebpBytes()
    {
        if (!Directory.Exists(TenantDir)) return 0;
        var large = Path.Combine(TenantDir, "large") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(TenantDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(large, StringComparison.Ordinal))
            .Sum(f => new FileInfo(f).Length);
    }

    private static byte[] Png(Rgba32 color, int noiseSeed = 0)
    {
        using var img = new Image<Rgba32>(100, 100);
        img.Mutate(ctx => ctx.BackgroundColor(color));
        if (noiseSeed != 0)
        {
            // Noise makes the encoded size differ from a flat tile's.
            var rng = new Random(noiseSeed);
            for (var i = 0; i < 400; i++)
                img[rng.Next(100), rng.Next(100)] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        }
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private async Task SeedZoom0Async(int x, int y, byte[] png)
    {
        var relative = Path.Combine("tenants", TenantId, "grids", $"g{x}_{y}.png");
        var full = Path.Combine(_gridStorage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, png);
        _db.Tiles.Add(new TileDataEntity
        {
            MapId = 1, CoordX = x, CoordY = y, Zoom = 0, File = relative, Cache = 1,
            TenantId = TenantId, FileSizeBytes = png.Length
        });
        await _db.SaveChangesAsync();
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public async Task ReplaceTileFileAsync_SamePathTwice_ChargesSizeChangeOnly()
    {
        var path = Path.Combine(TenantDir, "grids", "g.png");

        Assert.Equal(1000, await _tileService.ReplaceTileFileAsync(TenantId, path, new byte[1000]));
        Assert.Equal(-400, await _tileService.ReplaceTileFileAsync(TenantId, path, new byte[600]));
        Assert.Equal(0, await _tileService.ReplaceTileFileAsync(TenantId, path, new byte[600]));

        Assert.Equal(new long[] { 1000, -400 }, _charged); // a same-size refresh charges nothing
        Assert.Equal(600, new FileInfo(path).Length);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Equal(NonWebpBytes(), _charged.Sum());
    }

    [Fact]
    public async Task UpdateZoomLevelAsync_CalledTwice_SumOfDeltasEqualsBytesOnDisk()
    {
        await SeedZoom0Async(0, 0, Png(new Rgba32(200, 30, 30)));
        await SeedZoom0Async(1, 0, Png(new Rgba32(30, 200, 30)));

        await _tileService.UpdateZoomLevelAsync(1, new Coord(0, 0), 1, TenantId, _gridStorage);
        var afterFirst = _charged.Sum();

        // A sub-tile changes (a refresh), the parent is rebuilt over the same file.
        await File.WriteAllBytesAsync(Path.Combine(_gridStorage, "tenants", TenantId, "grids", "g0_0.png"), Png(new Rgba32(10, 10, 220), noiseSeed: 7));
        var afterRefresh = NonWebpBytes();
        await _tileService.UpdateZoomLevelAsync(1, new Coord(0, 0), 1, TenantId, _gridStorage);

        var zoomFile = Path.Combine(TenantDir, "1", "1", "0_0.png");
        Assert.True(File.Exists(zoomFile));
        Assert.Equal(new FileInfo(zoomFile).Length, _charged.Sum());
        Assert.True(afterFirst > 0);
        // The rebuild charged exactly what it changed on disk (the test's own grid write is not charged).
        Assert.Equal(NonWebpBytes() - afterRefresh, _charged.Sum() - afterFirst);
    }

    [Fact]
    public async Task RebuildIncompleteZoomTilesAsync_OrphanZoomFileWithoutRow_NotDoubleCounted()
    {
        await SeedZoom0Async(0, 0, Png(new Rgba32(200, 30, 30)));
        // A zoom-1 file on disk with no Tiles row (e.g. left by an earlier rebuild whose row was
        // dropped). The old compensation looked up the old size through the row, so it charged
        // the full new size on top of the orphan.
        var zoomFile = Path.Combine(TenantDir, "1", "1", "0_0.png");
        WriteFile(zoomFile, 5000);
        _db.DirtyZoomTiles.Add(new DirtyZoomTileEntity
        {
            TenantId = TenantId, MapId = 1, CoordX = 0, CoordY = 0, Zoom = 1, CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        var before = NonWebpBytes();

        var rebuilt = await _tileService.RebuildIncompleteZoomTilesAsync(TenantId, _gridStorage, 10);

        Assert.Equal(1, rebuilt);
        Assert.Equal(NonWebpBytes() - before, _charged.Sum());
        Assert.Equal(new FileInfo(zoomFile).Length - 5000, _charged.Sum());
    }

    [Fact]
    public async Task DeleteRetiredMapDirectoriesAsync_CreditsLegacyBytesOnly()
    {
        WriteFile(Path.Combine(TenantDir, "5", "0", "3_4.png"), 700);   // imported zoom-0
        WriteFile(Path.Combine(TenantDir, "5", "1", "1_2.png"), 300);   // legacy zoom 1
        WriteFile(Path.Combine(TenantDir, "large", "5", "0", "0_0.webp"), 9000);
        WriteFile(Path.Combine(TenantDir, "grids", "keep.png"), 50);
        var before = NonWebpBytes();

        var charged = await _tileService.DeleteRetiredMapDirectoriesAsync(TenantId, 5, _gridStorage, rehomedBytes: 0);

        Assert.False(Directory.Exists(Path.Combine(TenantDir, "5")));
        Assert.False(Directory.Exists(Path.Combine(TenantDir, "large", "5")));
        Assert.True(File.Exists(Path.Combine(TenantDir, "grids", "keep.png")));
        Assert.Equal(-1000, charged);
        Assert.Equal(new long[] { -1000 }, _charged); // one update, no WebP credit
        Assert.Equal(NonWebpBytes() - before, _charged.Sum());
    }

    [Fact]
    public async Task DeleteRetiredMapDirectoriesAsync_LiveReference_KeepsDirectory_ChargesRehomedBytes()
    {
        WriteFile(Path.Combine(TenantDir, "5", "0", "3_4.png"), 700);
        WriteFile(Path.Combine(TenantDir, "large", "5", "0", "0_0.webp"), 9000);
        // A live map's row still points into map 5's directory (a re-home that could not happen).
        _db.Tiles.Add(new TileDataEntity
        {
            MapId = 1, CoordX = 3, CoordY = 4, Zoom = 0, File = "tenants/" + TenantId + "/5/0/3_4.png",
            Cache = 1, TenantId = TenantId, FileSizeBytes = 700
        });
        await _db.SaveChangesAsync();

        var charged = await _tileService.DeleteRetiredMapDirectoriesAsync(TenantId, 5, _gridStorage, rehomedBytes: 250);

        Assert.True(File.Exists(Path.Combine(TenantDir, "5", "0", "3_4.png")));
        Assert.False(Directory.Exists(Path.Combine(TenantDir, "large", "5")));
        Assert.Equal(250, charged);
        Assert.Equal(new long[] { 250 }, _charged);
    }
}
