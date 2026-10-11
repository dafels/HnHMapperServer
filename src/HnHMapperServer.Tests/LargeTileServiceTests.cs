using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace HnHMapperServer.Tests;

/// <summary>
/// Tests for LargeTileService's pyramid-consistency behavior: force-regeneration must delete a
/// stale WebP file when its sources are genuinely gone (so wipes/merges stop ghosting old
/// imagery), must NOT delete on a transient generation error, and map-level cache invalidation
/// must actually evict the static in-memory caches. A generation failure must never be treated
/// as "empty" (no negative-cache entry, no parent saved with a hole), and zoomed-out tiles over
/// unexplored areas must not recurse into their children. A pyramid file that is not a complete
/// WebP (zero-length/truncated residue of an interrupted write) counts as missing everywhere:
/// never served, never composited around, and the batch never composes a parent around a child
/// that failed.
///
/// LargeTileService's caches are static (shared across instances/tests), so every test uses a
/// unique tenant id to stay isolated.
/// </summary>
public class LargeTileServiceTests : IDisposable
{
    private readonly string _gridStorage;
    private readonly ApplicationDbContext _db;
    private readonly LargeTileService _service;
    private readonly Mock<ILogger<LargeTileService>> _logger = new();

    /// <summary>A real (tiny) WebP: readers now reject anything that is not a complete one.</summary>
    private static readonly byte[] TinyWebp = EncodeTinyWebp();

    private static byte[] EncodeTinyWebp()
    {
        using var img = new Image<Rgba32>(4, 4, new Rgba32(10, 120, 200, 255));
        using var ms = new MemoryStream();
        img.SaveAsWebp(ms, new WebpEncoder());
        return ms.ToArray();
    }

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

        _service = new LargeTileService(_db, configuration, _logger.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_gridStorage, recursive: true); } catch { }
    }

    private static string NewTenantId() => $"tenant-{Guid.NewGuid():N}";

    private string WriteFakeWebp(string tenantId, int mapId, int zoom, int x, int y) =>
        WriteTileBytes(tenantId, mapId, zoom, x, y, TinyWebp);

    private string WriteTileBytes(string tenantId, int mapId, int zoom, int x, int y, byte[] bytes)
    {
        var path = _service.GetLargeTilePath(tenantId, mapId, zoom, x, y);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private void SeedMap(string tenantId, int mapId)
    {
        _db.Maps.Add(new MapInfoEntity { Id = mapId, Name = $"Map{mapId}", TenantId = tenantId, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    private void VerifyLogged(LogLevel level, string fragment, Times times) =>
        _logger.Verify(x => x.Log(
            level,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(fragment)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);

    private static bool HasPixel(byte[] bytes, int px, int py)
    {
        using var img = Image.Load<Rgba32>(bytes);
        return img[px, py].A > 0;
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
    public async Task ForceRegenerate_GenerationError_DoesNotHideExistingFile()
    {
        var tenantId = NewTenantId();
        WriteFakeWebp(tenantId, 10, 0, 0, 0);

        _db.Dispose();
        Assert.Null(await _service.ForceRegenerateLargeTileAsync(tenantId, 10, 0, 0, 0));

        // A failed regeneration must not record the tile as missing: the negative cache is
        // checked before the disk, so it would hide the still-valid file for five minutes.
        var served = await _service.GetOrGenerateLargeTileAsync(tenantId, 10, 0, 0, 0);
        Assert.Equal(TinyWebp, served);
    }

    [Fact]
    public async Task GetOrGenerate_ChildFailure_FailsParentInsteadOfSavingAHole()
    {
        // Zoom-1 tile (0,0) has content in two quarters, zoom-0 (0,0) and (1,0). Generating
        // zoom-0 (1,0) is made to fail by putting a directory where its file has to be written.
        var tenantId = NewTenantId();
        const int mapId = 7;
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "left"));
        SeedZoom0Row(tenantId, mapId, 4, 0, WriteGridPng(tenantId, "right"));
        var blocked = _service.GetLargeTilePath(tenantId, mapId, 0, 1, 0);
        Directory.CreateDirectory(blocked);

        await Assert.ThrowsAnyAsync<Exception>(() => _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 1, 0, 0));
        Assert.False(File.Exists(_service.GetLargeTilePath(tenantId, mapId, 1, 0, 0)),
            "a parent must not be saved with a quarter missing because a child failed");

        // Once the cause is gone the next request builds the whole parent: the failure was
        // neither negative-cached nor kept as a partial tile.
        Directory.Delete(blocked);
        var bytes = await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 1, 0, 0);

        Assert.NotNull(bytes);
        using var img = Image.Load<Rgba32>(bytes);
        Assert.True(img[25, 25].A > 0, "left quarter, from zoom-0 (0,0), is missing");
        Assert.True(img[225, 25].A > 0, "right quarter, from zoom-0 (1,0), is missing");
    }

    [Fact]
    public async Task GetOrGenerate_EmptyFootprint_DoesNotRecurseIntoChildren()
    {
        var tenantId = NewTenantId();
        const int mapId = 8;

        // Nothing under zoom-2 (0,0), so one footprint query answers it.
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 2, 0, 0));

        // Had it recursed, every child would now be negative-cached as empty for five minutes
        // and this new data would stay invisible at zoom 1.
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "fresh"));
        Assert.NotNull(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 1, 0, 0));
    }

    [Fact]
    public async Task GetOrGenerate_FootprintCheck_HandlesNegativeCoordinates()
    {
        var tenantId = NewTenantId();
        const int mapId = 9;
        SeedZoom0Row(tenantId, mapId, -1, -1, WriteGridPng(tenantId, "north-west"));

        // A zoom-3 tile spans 32 base tiles: (-1,-1) covers -32..-1 on both axes, (0,0) 0..31.
        Assert.NotNull(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 3, -1, -1));
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 3, 0, 0));
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 3, -1, 0));
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
        Assert.Equal(2L * TinyWebp.Length, bytesFreed);
        Assert.False(Directory.Exists(Path.Combine(_gridStorage, "tenants", tenantId, "large", "5")));
        Assert.True(File.Exists(otherMap), "other maps' pyramids must be untouched");
    }

    // ---------- zero-length / truncated pyramid files ----------

    [Fact]
    public async Task GetOrGenerate_ZeroLengthFile_RegeneratesInsteadOfServingEmpty()
    {
        var tenantId = NewTenantId();
        const int mapId = 20;
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "g"));
        var path = WriteTileBytes(tenantId, mapId, 0, 0, 0, Array.Empty<byte>());

        var bytes = await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 0, 0, 0);

        Assert.NotNull(bytes);
        Assert.True(WebpTileFile.IsComplete(bytes), "empty bytes must never be served");
        Assert.True(HasPixel(bytes, 25, 25));
        Assert.True(WebpTileFile.IsComplete(File.ReadAllBytes(path)), "the file on disk was regenerated");
        VerifyLogged(LogLevel.Warning, "CORRUPT", Times.Once());
    }

    [Fact]
    public async Task GetOrGenerate_ZeroLengthFileWithoutSources_IsDeletedAndReturnsNull()
    {
        var tenantId = NewTenantId();
        var path = WriteTileBytes(tenantId, 21, 0, 0, 0, Array.Empty<byte>());

        Assert.Null(await _service.GetOrGenerateLargeTileAsync(tenantId, 21, 0, 0, 0));
        Assert.False(File.Exists(path), "a corrupt file with nothing to regenerate it from is a 404, not an empty 200");
    }

    [Fact]
    public async Task ForceRegenerate_OnlyChildZeroLength_RebuildsParentInsteadOfDeletingIt()
    {
        // The parent-deletion bug: the only child of a parent is a zero-length file.
        // Its decode error used to be swallowed, the parent read as "no sources", and REGEN-EMPTY
        // deleted the valid parent.
        var tenantId = NewTenantId();
        const int mapId = 22;
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "only"));
        Assert.NotNull(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 1, 0, 0));
        var childPath = _service.GetLargeTilePath(tenantId, mapId, 0, 0, 0);
        var parentPath = _service.GetLargeTilePath(tenantId, mapId, 1, 0, 0);
        File.WriteAllBytes(childPath, Array.Empty<byte>());
        _service.InvalidateMapCache(tenantId, mapId);

        var parent = await _service.ForceRegenerateLargeTileAsync(tenantId, mapId, 1, 0, 0);

        Assert.NotNull(parent);
        Assert.True(File.Exists(parentPath), "the parent must be recomposed, not deleted as empty");
        Assert.True(HasPixel(File.ReadAllBytes(parentPath), 25, 25));
        Assert.True(WebpTileFile.IsComplete(File.ReadAllBytes(childPath)), "the child was regenerated");
    }

    [Fact]
    public async Task ForceRegenerate_ZeroLengthSibling_NoHole()
    {
        var tenantId = NewTenantId();
        const int mapId = 23;
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "left"));
        SeedZoom0Row(tenantId, mapId, 4, 0, WriteGridPng(tenantId, "right"));
        Assert.NotNull(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 1, 0, 0));
        File.WriteAllBytes(_service.GetLargeTilePath(tenantId, mapId, 0, 1, 0), Array.Empty<byte>());
        _service.InvalidateMapCache(tenantId, mapId);

        var parent = await _service.ForceRegenerateLargeTileAsync(tenantId, mapId, 1, 0, 0);

        Assert.NotNull(parent);
        Assert.True(HasPixel(parent, 25, 25), "left quarter, from zoom-0 (0,0), is missing");
        Assert.True(HasPixel(parent, 225, 25), "right quarter, from the zero-length zoom-0 (1,0), is a hole");
    }

    [Theory]
    [InlineData(0)] // a zero-length zoom-0 tile, every ancestor missing
    [InlineData(5)] // a zero-length zoom-5 tile, zoom 6 missing
    public async Task Batch_ZeroLengthTile_IsRegeneratedWithItsMissingAncestors(int corruptZoom)
    {
        var tenantId = NewTenantId();
        const int mapId = 24;
        SeedMap(tenantId, mapId);
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "g"));
        Assert.Equal(7, await _service.GenerateMissingTilesAsync(tenantId));

        File.WriteAllBytes(_service.GetLargeTilePath(tenantId, mapId, corruptZoom, 0, 0), Array.Empty<byte>());
        for (var z = corruptZoom + 1; z <= 6; z++)
        {
            File.Delete(_service.GetLargeTilePath(tenantId, mapId, z, 0, 0));
        }

        Assert.Equal(7 - corruptZoom, await _service.GenerateMissingTilesAsync(tenantId));

        for (var z = corruptZoom; z <= 6; z++)
        {
            Assert.True(WebpTileFile.IsComplete(File.ReadAllBytes(_service.GetLargeTilePath(tenantId, mapId, z, 0, 0))),
                $"z{z} (0,0) must be a complete WebP again");
        }
        Assert.True(HasPixel(File.ReadAllBytes(_service.GetLargeTilePath(tenantId, mapId, 6, 0, 0)), 0, 0));
    }

    [Fact]
    public async Task Batch_FailedChild_DoesNotHoleGrandparent()
    {
        // z2 (0,0) has two zoom-1 children: (0,0) over zoom-0 (0,0)+(1,0), and (1,0) over (2,0).
        var tenantId = NewTenantId();
        const int mapId = 25;
        SeedMap(tenantId, mapId);
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "a"));
        SeedZoom0Row(tenantId, mapId, 4, 0, WriteGridPng(tenantId, "b"));
        SeedZoom0Row(tenantId, mapId, 8, 0, WriteGridPng(tenantId, "c"));
        Assert.Equal(10, await _service.GenerateMissingTilesAsync(tenantId)); // z0:3 z1:2 z2-z6:1 each

        // A truncated (non-empty, so the stat check passes) child and its two missing ancestors.
        var truncated = _service.GetLargeTilePath(tenantId, mapId, 0, 1, 0);
        File.WriteAllBytes(truncated, File.ReadAllBytes(truncated)[..40]);
        File.Delete(_service.GetLargeTilePath(tenantId, mapId, 1, 0, 0));
        File.Delete(_service.GetLargeTilePath(tenantId, mapId, 2, 0, 0));

        // The parent that reads it discards it and fails; the grandparent is not composed around
        // the failed parent either.
        Assert.Equal(0, await _service.GenerateMissingTilesAsync(tenantId));
        Assert.False(File.Exists(truncated), "the truncated child is discarded for regeneration");
        Assert.False(File.Exists(_service.GetLargeTilePath(tenantId, mapId, 1, 0, 0)), "parent saved with a hole");
        Assert.False(File.Exists(_service.GetLargeTilePath(tenantId, mapId, 2, 0, 0)), "grandparent saved with a hole");

        // The next scan heals the whole chain: z0-z2 rebuilt, and the existing z3-z6 ancestors
        // refreshed because a child of theirs was just rewritten.
        Assert.Equal(7, await _service.GenerateMissingTilesAsync(tenantId));
        var grandparent = File.ReadAllBytes(_service.GetLargeTilePath(tenantId, mapId, 2, 0, 0));
        Assert.True(HasPixel(grandparent, 12, 12), "zoom-0 (0,0) missing from z2");
        Assert.True(HasPixel(grandparent, 112, 12), "zoom-0 (1,0) missing from z2");
        Assert.True(HasPixel(grandparent, 212, 12), "zoom-0 (2,0) missing from z2");
    }

    [Fact]
    public async Task Batch_Zoom0WriteError_DoesNotHoleParent()
    {
        // A zoom-0 generation error used to come back as null ("empty"), so its parent was
        // composed without it and saved.
        var tenantId = NewTenantId();
        const int mapId = 26;
        SeedMap(tenantId, mapId);
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "left"));
        SeedZoom0Row(tenantId, mapId, 4, 0, WriteGridPng(tenantId, "right"));
        var blocked = _service.GetLargeTilePath(tenantId, mapId, 0, 1, 0);
        Directory.CreateDirectory(blocked);

        Assert.Equal(1, await _service.GenerateMissingTilesAsync(tenantId)); // only zoom-0 (0,0)
        Assert.False(File.Exists(_service.GetLargeTilePath(tenantId, mapId, 1, 0, 0)),
            "a parent must not be saved with a quarter missing because a child failed");

        Directory.Delete(blocked);
        Assert.Equal(7, await _service.GenerateMissingTilesAsync(tenantId)); // zoom-0 (1,0) + z1..z6

        var parent = File.ReadAllBytes(_service.GetLargeTilePath(tenantId, mapId, 1, 0, 0));
        Assert.True(HasPixel(parent, 25, 25));
        Assert.True(HasPixel(parent, 225, 25));
    }

    [Fact]
    public async Task Batch_RefreshesExistingParentOfAChildItGenerated()
    {
        // Zoom-0 cells (0,0) and (1,0) share the z1 parent (0,0). The parent exists from before
        // (composed while (1,0) had no file); gap-fill now generates (1,0) and must recompose the
        // parent — it used to skip any existing file, leaving exactly the "older than child" drift.
        var tenantId = NewTenantId();
        const int mapId = 29;
        SeedMap(tenantId, mapId);
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "left"));
        Assert.Equal(7, await _service.GenerateMissingTilesAsync(tenantId));

        var parentPath = _service.GetLargeTilePath(tenantId, mapId, 1, 0, 0);
        var oldParentTime = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(parentPath, oldParentTime);
        Assert.False(HasPixel(File.ReadAllBytes(parentPath), 225, 25));
        var untouchedSibling = _service.GetLargeTilePath(tenantId, mapId, 0, 0, 0);
        var siblingTime = File.GetLastWriteTimeUtc(untouchedSibling);

        SeedZoom0Row(tenantId, mapId, 4, 0, WriteGridPng(tenantId, "right"));

        // zoom-0 (1,0) generated, z1-z6 (0,0) refreshed; zoom-0 (0,0) left alone
        Assert.Equal(7, await _service.GenerateMissingTilesAsync(tenantId));
        Assert.True(File.GetLastWriteTimeUtc(parentPath) > oldParentTime);
        Assert.True(HasPixel(File.ReadAllBytes(parentPath), 225, 25), "the new child is in its parent");
        Assert.Equal(siblingTime, File.GetLastWriteTimeUtc(untouchedSibling));

        // Nothing new: nothing is regenerated.
        Assert.Equal(0, await _service.GenerateMissingTilesAsync(tenantId));
    }

    [Fact]
    public async Task Batch_EvictsWhatItWrites_AndReportsTheMap()
    {
        // The batch composes from files, but serving and ZoomQ's ForceRegenerate read children
        // through the cache: a refreshed parent left cached would be served stale and baked back
        // into the levels above with a fresh mtime. A new tile's cached 404 must go too.
        var tenantId = NewTenantId();
        const int mapId = 30;
        SeedMap(tenantId, mapId);
        SeedZoom0Row(tenantId, mapId, 0, 0, WriteGridPng(tenantId, "left"));
        Assert.Equal(7, await _service.GenerateMissingTilesAsync(tenantId));

        var cachedParent = await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 1, 0, 0);
        Assert.False(HasPixel(cachedParent!, 225, 25));
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 0, 1, 0)); // negative-cached

        SeedZoom0Row(tenantId, mapId, 4, 0, WriteGridPng(tenantId, "right"));
        var written = new HashSet<int>();
        Assert.Equal(7, await _service.GenerateMissingTilesAsync(tenantId, default, written));

        Assert.Equal(new[] { mapId }, written);
        Assert.True(HasPixel((await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 1, 0, 0))!, 225, 25));
        Assert.NotNull(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 0, 1, 0));

        var nothingWritten = new HashSet<int>();
        Assert.Equal(0, await _service.GenerateMissingTilesAsync(tenantId, default, nothingWritten));
        Assert.Empty(nothingWritten);
    }

    // ---------- missing tile sources ----------

    [Fact]
    public async Task Batch_MissingSources_WarnsOnceAndCountsSeparately()
    {
        var tenantId = NewTenantId();
        const int mapId = 27;
        SeedMap(tenantId, mapId);
        var gonePng = Path.Combine("tenants", tenantId, "grids", "gone.png");
        SeedZoom0Row(tenantId, mapId, 0, 0, gonePng);                       // dark: row, no file
        SeedZoom0Row(tenantId, mapId, 8, 0, "");                            // wipe tombstone
        SeedZoom0Row(tenantId, mapId, 16, 0, WriteGridPng(tenantId, "ok")); // good cell

        await _service.GenerateMissingTilesAsync(tenantId);
        await _service.GenerateMissingTilesAsync(tenantId);

        VerifyLogged(LogLevel.Warning, "SOURCES-MISSING", Times.Once());
        VerifyLogged(LogLevel.Warning, "1 zoom-0 cell(s) have tile rows", Times.Once());
        // The tombstone is no cell at all, and a dark cell is "empty", not a failure.
        VerifyLogged(LogLevel.Information, "failed=0 missingSources=1", Times.Exactly(2));

        // Restoring the file heals the cell without another warning.
        var full = Path.Combine(_gridStorage, gonePng);
        using (var img = new Image<Rgba32>(100, 100, new Rgba32(30, 200, 30, 255)))
        {
            img.SaveAsPng(full);
        }
        // z0, z1, z2 of the restored cell, plus the existing z3-z6 composed without it
        Assert.Equal(7, await _service.GenerateMissingTilesAsync(tenantId));
        VerifyLogged(LogLevel.Warning, "SOURCES-MISSING", Times.Once());
        Assert.True(File.Exists(_service.GetLargeTilePath(tenantId, mapId, 0, 0, 0)));
    }

    [Fact]
    public async Task GetOrGenerate_TombstoneOnlyFootprint_ReturnsNullWithoutWritingFiles()
    {
        var tenantId = NewTenantId();
        const int mapId = 28;
        SeedZoom0Row(tenantId, mapId, 0, 0, ""); // wipeTile leaves File = "" behind

        Assert.Null(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 3, 0, 0));
        Assert.Null(await _service.GetOrGenerateLargeTileAsync(tenantId, mapId, 0, 0, 0));
        Assert.False(Directory.Exists(Path.Combine(_gridStorage, "tenants", tenantId, "large")));
    }

    [Fact]
    public async Task BatchRegenerator_BuildsEachParentOnce_FromFreshChildren()
    {
        // Two uploads in neighbouring cells (0,0) and (1,1) share every ancestor. Bottom-up planning
        // builds z1 (0,0) once, after BOTH cells were regenerated; the per-request loop built it once
        // per request, the first time over the stale cell (1,1).
        var tenantId = NewTenantId();
        const int mapId = 30;
        SeedZoom0Row(tenantId, mapId, 3, 3, WriteGridPng(tenantId, "grid-33"));   // cell (0,0), local (3,3)
        SeedZoom0Row(tenantId, mapId, 4, 4, WriteGridPng(tenantId, "grid-44"));   // cell (1,1), local (0,0)
        using (var blue = new Image<Rgba32>(400, 400, new Rgba32(20, 20, 230, 255)))
        using (var ms = new MemoryStream())
        {
            blue.SaveAsWebp(ms, new WebpEncoder());
            WriteTileBytes(tenantId, mapId, 0, 1, 1, ms.ToArray()); // stale and fully opaque
        }

        var calls = new List<(int Zoom, int X, int Y)>();
        var recording = new Mock<ILargeTileService>();
        recording
            .Setup(t => t.ForceRegenerateLargeTileAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns<string, int, int, int, int>((t, m, z, x, y) =>
            {
                calls.Add((z, x, y));
                return _service.ForceRegenerateLargeTileAsync(t, m, z, x, y);
            });
        var scopeFactory = new ServiceCollection()
            .AddScoped(_ => recording.Object)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var queue = new ZoomTileQueueService(Mock.Of<ILogger<ZoomTileQueueService>>());
        var regenerator = new ZoomTileBatchRegenerator(queue, scopeFactory, Mock.Of<ILogger<ZoomTileBatchRegenerator>>());

        var plan = ZoomTileBatchPlanner.Plan(new[]
        {
            new ZoomTileRequest(tenantId, mapId, 3, 3),
            new ZoomTileRequest(tenantId, mapId, 4, 4)
        });
        var result = await regenerator.RegenerateAsync(plan, CancellationToken.None);

        Assert.Equal(2, result.Generated);
        Assert.Equal(8, calls.Count);                       // 2 cells + z1..z6 at (0,0)
        Assert.Equal(calls.Count, calls.Distinct().Count());

        var z1 = await File.ReadAllBytesAsync(_service.GetLargeTilePath(tenantId, mapId, 1, 0, 0));
        using var image = Image.Load<Rgba32>(z1);
        Assert.True(image[175, 175].A > 0, "child (0,0)'s grid must be in the parent");
        var fresh = image[225, 225];                         // child (1,1)'s grid, freshly drawn
        Assert.True(fresh.A > 0 && fresh.R > fresh.B + 100, $"expected the fresh red grid, got {fresh}");
        Assert.True(image[300, 300].A < 20, "the stale opaque cell (1,1) must not be composited");
    }
}
