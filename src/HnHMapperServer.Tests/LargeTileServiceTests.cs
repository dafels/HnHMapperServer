using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HnHMapperServer.Tests;

/// <summary>
/// Tests for LargeTileService's pyramid-consistency behavior: force-regeneration must delete a
/// stale WebP file when its sources are genuinely gone (so wipes/merges stop ghosting old
/// imagery), must NOT delete on a transient generation error, and map-level cache invalidation
/// must actually evict the static in-memory caches.
///
/// LargeTileService's caches are static (shared across instances/tests), so every test uses a
/// unique tenant id to stay isolated.
/// </summary>
public class LargeTileServiceTests : IDisposable
{
    private readonly string _gridStorage;
    private readonly ApplicationDbContext _db;
    private readonly LargeTileService _service;

    public LargeTileServiceTests()
    {
        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-largetile-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gridStorage);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDbContext(options);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GridStorage"] = _gridStorage })
            .Build();

        _service = new LargeTileService(_db, configuration, Mock.Of<ILogger<LargeTileService>>());
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    private static string NewTenantId() => $"tenant-{Guid.NewGuid():N}";

    private string WriteFakeWebp(string tenantId, int mapId, int zoom, int x, int y)
    {
        var path = _service.GetLargeTilePath(tenantId, mapId, zoom, x, y);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private string WriteGridPng(string tenantId, string gridId)
    {
        var relative = Path.Combine("tenants", tenantId, "grids", $"{gridId}.png");
        var full = Path.Combine(_gridStorage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var img = new Image<Rgba32>(100, 100, new Rgba32(200, 30, 30, 255));
        img.SaveAsPng(full);
        return relative;
    }

    private void SeedZoom0Row(string tenantId, int mapId, int x, int y, string file)
    {
        _db.Tiles.Add(new TileDataEntity
        {
            MapId = mapId, CoordX = x, CoordY = y, Zoom = 0,
            File = file, Cache = 1, TenantId = tenantId, FileSizeBytes = 1
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task ForceRegenerate_NoSources_DeletesStaleFile()
    {
        var tenantId = NewTenantId();
        var stalePath = WriteFakeWebp(tenantId, 1, 0, 3, 3);

        var result = await _service.ForceRegenerateLargeTileAsync(tenantId, 1, 0, 3, 3);

        Assert.Null(result);
        Assert.False(File.Exists(stalePath),
            "a tile whose sources are gone must not keep serving its stale file");
    }

    [Fact]
    public async Task ForceRegenerate_WithSources_OverwritesFile()
    {
        var tenantId = NewTenantId();
        var gridPng = WriteGridPng(tenantId, "grid-a");
        SeedZoom0Row(tenantId, 2, 4, 4, gridPng); // cell (1,1) covers base 4..7

        var result = await _service.ForceRegenerateLargeTileAsync(tenantId, 2, 0, 1, 1);

        Assert.NotNull(result);
        Assert.True(File.Exists(_service.GetLargeTilePath(tenantId, 2, 0, 1, 1)));
    }

    [Fact]
    public async Task ForceRegenerate_GenerationError_KeepsFile()
    {
        var tenantId = NewTenantId();
        var stalePath = WriteFakeWebp(tenantId, 3, 0, 0, 0);

        // A disposed context makes the zoom-0 source query throw — a transient failure, not
        // "no sources". The existing file must survive it.
        _db.Dispose();
        var result = await _service.ForceRegenerateLargeTileAsync(tenantId, 3, 0, 0, 0);

        Assert.Null(result);
        Assert.True(File.Exists(stalePath), "a transient generation error must never delete a good file");
    }

    [Fact]
    public async Task InvalidateMapCache_EvictsInMemoryEntries()
    {
        var tenantId = NewTenantId();
        var path = WriteFakeWebp(tenantId, 4, 0, 0, 0);

        // Populate the memory cache from disk, then delete the file behind its back.
        var first = await _service.GetOrGenerateLargeTileAsync(tenantId, 4, 0, 0, 0);
        Assert.NotNull(first);
        File.Delete(path);

        // Still served from the static memory cache.
        var cached = await _service.GetOrGenerateLargeTileAsync(tenantId, 4, 0, 0, 0);
        Assert.NotNull(cached);

        _service.InvalidateMapCache(tenantId, 4);

        // No file, no rows: eviction worked if this now misses everything.
        var afterInvalidate = await _service.GetOrGenerateLargeTileAsync(tenantId, 4, 0, 0, 0);
        Assert.Null(afterInvalidate);
    }

    [Fact]
    public void DeleteMapWebpTiles_RemovesWholePyramidAndCountsFiles()
    {
        var tenantId = NewTenantId();
        WriteFakeWebp(tenantId, 5, 0, 0, 0);
        WriteFakeWebp(tenantId, 5, 3, 1, 1);
        var otherMap = WriteFakeWebp(tenantId, 6, 0, 0, 0);

        var (filesDeleted, bytesFreed) = _service.DeleteMapWebpTiles(tenantId, 5);

        Assert.Equal(2, filesDeleted);
        Assert.Equal(6, bytesFreed);
        Assert.False(Directory.Exists(Path.Combine(_gridStorage, "tenants", tenantId, "large", "5")));
        Assert.True(File.Exists(otherMap), "other maps' pyramids must be untouched");
    }
}
