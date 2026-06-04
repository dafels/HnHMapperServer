using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    public PublicMapAnalysisServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

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
        => new(ctx, new AlignmentSolver(), NullLogger<PublicMapAnalysisService>.Instance);

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
}
