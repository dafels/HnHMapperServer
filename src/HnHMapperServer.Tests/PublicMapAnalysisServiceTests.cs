using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// DB-backed integration tests for the pre-merge analysis: exercises source loading, the aligner,
/// friendly-name labelling, persistence, and end-to-end order-independence. Uses SQLite in-memory
/// (matches production semantics incl. ExecuteDelete) rather than the EF InMemory provider.
/// </summary>
public class PublicMapAnalysisServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _tempDir;

    public PublicMapAnalysisServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _tempDir = Path.Combine(Path.GetTempPath(), $"hnh-hmap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>Write a minimal valid .hmap (Version-1 grids carry only identity — enough for alignment).</summary>
    private string WriteHmap(string relativePath, params (long gridId, int x, int y)[] grids)
    {
        var full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var fs = new FileStream(full, FileMode.Create, FileAccess.Write);
        fs.Write(System.Text.Encoding.ASCII.GetBytes("Haven Mapfile 1"));
        fs.WriteByte(0x78); fs.WriteByte(0xDA); // 2-byte zlib header (reader skips these)
        using (var deflate = new System.IO.Compression.DeflateStream(fs, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            foreach (var (gridId, x, y) in grids)
            {
                var type = System.Text.Encoding.UTF8.GetBytes("grid");
                deflate.Write(type, 0, type.Length);
                deflate.WriteByte(0); // null terminator

                using var body = new MemoryStream();
                using (var b = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    b.Write((byte)1);      // Version 1 (<4 -> only identity is parsed)
                    b.Write(gridId);       // GridId (int64)
                    b.Write(0L);           // SegmentId
                    b.Write(123456L);      // ModifiedTime
                    b.Write(x);            // TileX (grid coord)
                    b.Write(y);            // TileY (grid coord)
                }
                var bytes = body.ToArray();
                var len = BitConverter.GetBytes(bytes.Length);
                deflate.Write(len, 0, len.Length);
                deflate.Write(bytes, 0, bytes.Length);
            }
        }
        return full;
    }

    private ApplicationDbContext NewContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "tenantA";
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        var ctx = new ApplicationDbContext(options, accessor.Object);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private static PublicMapAnalysisService NewService(ApplicationDbContext ctx)
        => new(ctx, new AlignmentSolver(), Mock.Of<IConfiguration>(), NullLogger<PublicMapAnalysisService>.Instance);

    /// <summary>Seed two tenants, three maps (S1 &amp; S2 overlap, S3 standalone), and grids.</summary>
    private static void SeedWorld(ApplicationDbContext ctx)
    {
        ctx.Tenants.AddRange(
            new TenantEntity { Id = "tenantA", Name = "Tenant A", StorageQuotaMB = 1024, CurrentStorageMB = 0, CreatedAt = DateTime.UtcNow, IsActive = true },
            new TenantEntity { Id = "tenantB", Name = "Tenant B", StorageQuotaMB = 1024, CurrentStorageMB = 0, CreatedAt = DateTime.UtcNow, IsActive = true });

        ctx.Maps.AddRange(
            new MapInfoEntity { Id = 1, Name = "Alpha", TenantId = "tenantA", CreatedAt = DateTime.UtcNow },
            new MapInfoEntity { Id = 2, Name = "Beta", TenantId = "tenantB", CreatedAt = DateTime.UtcNow },
            new MapInfoEntity { Id = 3, Name = "Gamma", TenantId = "tenantA", CreatedAt = DateTime.UtcNow });

        // S1 (tenantA/1) and S2 (tenantB/2) share grid ids g0..g5 at a fixed offset -> overlap.
        for (int i = 0; i < 6; i++)
        {
            ctx.Grids.Add(new GridDataEntity { Id = $"g{i}", CoordX = i, CoordY = 0, Map = 1, TenantId = "tenantA", NextUpdate = DateTime.UtcNow });
            ctx.Grids.Add(new GridDataEntity { Id = $"g{i}", CoordX = 100 + i, CoordY = 50, Map = 2, TenantId = "tenantB", NextUpdate = DateTime.UtcNow });
            // S3 (tenantA/3): unique ids -> standalone landmass.
            ctx.Grids.Add(new GridDataEntity { Id = $"z{i}", CoordX = i, CoordY = 0, Map = 3, TenantId = "tenantA", NextUpdate = DateTime.UtcNow });
        }
        ctx.SaveChanges();
    }

    private static void SeedPublicMap(ApplicationDbContext ctx, string publicMapId,
        params (string tenantId, int mapId)[] sourcesInOrder)
    {
        ctx.PublicMaps.Add(new PublicMapEntity
        {
            Id = publicMapId,
            Name = publicMapId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            GenerationStatus = "pending",
        });
        int priority = 0;
        foreach (var (tenantId, mapId) in sourcesInOrder)
        {
            ctx.PublicMapSources.Add(new PublicMapSourceEntity
            {
                PublicMapId = publicMapId,
                TenantId = tenantId,
                MapId = mapId,
                Priority = priority++,
                AddedAt = DateTime.UtcNow,
                AddedBy = "test",
            });
        }
        ctx.SaveChanges();
    }

    [Fact]
    public async Task Analyze_GroupsOverlappingSources_AndIsolatesStandalone()
    {
        using var ctx = NewContext();
        SeedWorld(ctx);
        SeedPublicMap(ctx, "public", ("tenantA", 1), ("tenantB", 2), ("tenantA", 3));

        var report = await NewService(ctx).AnalyzeAsync("public");

        Assert.NotNull(report);
        Assert.Equal(3, report!.TotalSources);
        Assert.Equal(18, report.TotalGrids);
        Assert.Equal(2, report.ClusterCount);
        Assert.Equal(1, report.StandaloneCount);
        Assert.False(report.HasConflicts);

        // The merged landmass contains S1 + S2 and aligns their shared grid g0 to one coord.
        var merged = report.Clusters.Single(c => !c.IsStandalone);
        Assert.Equal(2, merged.Sources.Count);
        var s1 = merged.Sources.Single(s => s.TenantId == "tenantA" && s.MapId == 1);
        var s2 = merged.Sources.Single(s => s.TenantId == "tenantB" && s.MapId == 2);
        // g0: S1 local (0,0), S2 local (100,50). Unified must coincide.
        Assert.Equal((0 + s1.OffsetX, 0 + s1.OffsetY), (100 + s2.OffsetX, 50 + s2.OffsetY));

        // Friendly names resolved.
        Assert.Equal("Tenant A", s1.TenantName);
        Assert.Equal("Beta", s2.MapName);

        // Persisted and retrievable.
        Assert.Equal(1, await ctx.PublicMapAnalyses.CountAsync());
        var stored = await NewService(ctx).GetStoredAsync("public");
        Assert.NotNull(stored);
        Assert.Equal(report.AlignmentHash, stored!.AlignmentHash);
        Assert.Equal(report.ClusterCount, stored.ClusterCount);
    }

    [Fact]
    public async Task Analyze_IsOrderIndependent_AtTheDatabaseLevel()
    {
        using var ctx = NewContext();
        SeedWorld(ctx);
        SeedPublicMap(ctx, "forward", ("tenantA", 1), ("tenantB", 2), ("tenantA", 3));
        SeedPublicMap(ctx, "reverse", ("tenantA", 3), ("tenantB", 2), ("tenantA", 1));

        var svc = NewService(ctx);
        var a = await svc.AnalyzeAsync("forward");
        var b = await svc.AnalyzeAsync("reverse");

        Assert.NotNull(a);
        Assert.NotNull(b);
        // Same source set + content => identical fingerprint and structure regardless of add order.
        Assert.Equal(a!.AlignmentHash, b!.AlignmentHash);
        Assert.Equal(a.ClusterCount, b.ClusterCount);
        Assert.Equal(a.StandaloneCount, b.StandaloneCount);

        // Per-source resolved offsets are identical across the two add-orders.
        var offA = a.Clusters.SelectMany(c => c.Sources)
            .ToDictionary(s => (s.TenantId, s.MapId), s => (s.OffsetX, s.OffsetY));
        var offB = b.Clusters.SelectMany(c => c.Sources)
            .ToDictionary(s => (s.TenantId, s.MapId), s => (s.OffsetX, s.OffsetY));
        Assert.Equal(offA, offB);
    }

    [Fact]
    public async Task Analyze_PersistOverwrites_SingleRowPerMap()
    {
        using var ctx = NewContext();
        SeedWorld(ctx);
        SeedPublicMap(ctx, "public", ("tenantA", 1), ("tenantB", 2), ("tenantA", 3));

        var svc = NewService(ctx);
        await svc.AnalyzeAsync("public");
        await svc.AnalyzeAsync("public");

        Assert.Equal(1, await ctx.PublicMapAnalyses.CountAsync(a => a.PublicMapId == "public"));
    }

    [Fact]
    public async Task Analyze_MergesTenantAndHmap_SharingGridIds_IntoOneLandmass()
    {
        using var ctx = NewContext();

        // Tenant source whose grid ids are the int64 hmap grid ids (as strings) — the shared id space.
        ctx.Tenants.Add(new TenantEntity { Id = "tenantA", Name = "Tenant A", StorageQuotaMB = 1024, CurrentStorageMB = 0, CreatedAt = DateTime.UtcNow, IsActive = true });
        ctx.Maps.Add(new MapInfoEntity { Id = 1, Name = "Alpha", TenantId = "tenantA", CreatedAt = DateTime.UtcNow });
        for (int i = 0; i < 6; i++)
            ctx.Grids.Add(new GridDataEntity { Id = (1000 + i).ToString(), CoordX = i, CoordY = 0, Map = 1, TenantId = "tenantA", NextUpdate = DateTime.UtcNow });

        // Hmap file with the SAME grid ids at a shifted origin.
        const string rel = "hmap-sources/test.hmap";
        WriteHmap(rel, Enumerable.Range(0, 6).Select(i => ((long)(1000 + i), 100 + i, 50)).ToArray());
        ctx.HmapSources.Add(new HmapSourceEntity { Id = 1, Name = "Test HMap", FileName = "test.hmap", FilePath = rel, FileSizeBytes = 1, UploadedAt = DateTime.UtcNow });

        ctx.PublicMaps.Add(new PublicMapEntity { Id = "public", Name = "public", IsActive = true, CreatedAt = DateTime.UtcNow, CreatedBy = "test", GenerationStatus = "pending" });
        ctx.PublicMapSources.Add(new PublicMapSourceEntity { PublicMapId = "public", TenantId = "tenantA", MapId = 1, Priority = 0, AddedAt = DateTime.UtcNow, AddedBy = "test" });
        ctx.PublicMapHmapSources.Add(new PublicMapHmapSourceEntity { PublicMapId = "public", HmapSourceId = 1, Priority = 0, AddedAt = DateTime.UtcNow });
        ctx.SaveChanges();

        var svc = new PublicMapAnalysisService(ctx, new AlignmentSolver(),
            Mock.Of<IConfiguration>(c => c["GridStorage"] == _tempDir),
            NullLogger<PublicMapAnalysisService>.Instance);
        var report = await svc.AnalyzeAsync("public");

        Assert.NotNull(report);
        Assert.Equal(2, report!.TotalSources);
        Assert.Equal(1, report.ClusterCount);   // tenant + hmap woven into ONE landmass
        Assert.Equal(0, report.StandaloneCount);

        var cluster = report.Clusters.Single();
        var ts = cluster.Sources.Single(s => s.SourceType == "Tenant");
        var hs = cluster.Sources.Single(s => s.SourceType == "Hmap");
        Assert.Equal(1, hs.HmapSourceId);
        // Shared grid "1000": tenant local (0,0), hmap local (100,50) -> same unified coordinate.
        Assert.Equal((0 + ts.OffsetX, 0 + ts.OffsetY), (100 + hs.OffsetX, 50 + hs.OffsetY));
    }
}
