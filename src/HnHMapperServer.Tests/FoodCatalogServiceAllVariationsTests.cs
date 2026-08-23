using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Bulk variation list (GetAllVariationsAsync) — the data source of the cookbook's
/// flat "All recipes" view. Real SQLite with the ambient-tenant query filter active
/// (DbContext built WITH an IHttpContextAccessor), because tenant scoping of the
/// bulk list is exactly what these tests must prove.
/// </summary>
public class FoodCatalogServiceAllVariationsTests : IDisposable
{
    private const string TenantA = "flat-a";
    private const string TenantB = "flat-b";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly FoodCatalogService _service;
    private readonly int _breadId;
    private readonly int _stewId;

    public FoodCatalogServiceAllVariationsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-cookbook-flat-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TenantA;
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);

        _db = new ApplicationDbContext(options, accessor.Object);
        _db.Database.EnsureCreated();

        var tenantContext = new Mock<ITenantContextAccessor>();
        tenantContext.Setup(x => x.GetCurrentTenantId()).Returns(TenantA);

        _service = new FoodCatalogService(
            _db,
            new MemoryCache(new MemoryCacheOptions()),
            tenantContext.Object,
            Mock.Of<ILogger<FoodCatalogService>>());

        (_breadId, _stewId) = Seed();
    }

    [Fact]
    public async Task GetAllVariations_ReturnsEveryTenantVariant_WithFoodIdsSet()
    {
        var all = await _service.GetAllVariationsAsync();

        // 2 bread + 1 stew from tenant A; tenant B's variant is filtered out.
        Assert.Equal(3, all.Count);
        Assert.Equal(2, all.Count(v => v.FoodId == _breadId));
        Assert.Equal(1, all.Count(v => v.FoodId == _stewId));
        Assert.DoesNotContain(all, v => v.IngredientSignature == "Foreign:100");

        // Deterministic order: by food, best total first within a food.
        var breadTotals = all.Where(v => v.FoodId == _breadId)
            .Select(v => v.Feps.Sum(f => f.Value))
            .ToList();
        Assert.Equal(breadTotals.OrderByDescending(t => t), breadTotals);
    }

    [Fact]
    public async Task GetAllVariations_ResolvesContributorNames()
    {
        var all = await _service.GetAllVariationsAsync();

        var contributed = Assert.Single(all, v => v.IngredientSignature == "Flour:50|Water:50");
        Assert.Equal(new List<string> { "bronk" }, contributed.ContributorNames);

        // Unknown contributor ids render as "unknown" instead of leaking raw ids.
        var orphaned = Assert.Single(all, v => v.IngredientSignature == "Carrot:100");
        Assert.Equal(new List<string> { "unknown" }, orphaned.ContributorNames);
    }

    [Fact]
    public async Task GetAllVariations_EmptyWithoutTenantContext()
    {
        var noTenantService = new FoodCatalogService(
            _db,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ITenantContextAccessor>(),
            Mock.Of<ILogger<FoodCatalogService>>());

        Assert.Empty(await noTenantService.GetAllVariationsAsync());
    }

    [Fact]
    public async Task GetVariations_PerFood_AlsoCarriesFoodId()
    {
        var variants = await _service.GetVariationsAsync(_breadId);

        Assert.Equal(2, variants.Count);
        Assert.All(variants, v => Assert.Equal(_breadId, v.FoodId));
    }

    private (int BreadId, int StewId) Seed()
    {
        var now = DateTime.UtcNow;

        foreach (var tenantId in new[] { TenantA, TenantB })
        {
            _db.Tenants.Add(new TenantEntity
            {
                Id = tenantId,
                Name = tenantId,
                StorageQuotaMB = 1024,
                CurrentStorageMB = 0,
                CreatedAt = now,
                IsActive = true
            });
        }

        _db.Users.Add(new ApplicationUser
        {
            Id = "user-1",
            UserName = "bronk",
            NormalizedUserName = "BRONK"
        });
        _db.SaveChanges();

        var bread = new FoodEntity
        {
            TenantId = TenantA,
            Name = "Bread",
            ResourceName = "gfx/invobjs/bread",
            Energy = 100,
            Hunger = 0.5m,
            ImportedAt = now
        };
        var stew = new FoodEntity
        {
            TenantId = TenantA,
            Name = "Stew",
            ResourceName = "gfx/invobjs/stew",
            Energy = 200,
            Hunger = 1.5m,
            ImportedAt = now
        };
        var foreign = new FoodEntity
        {
            TenantId = TenantB,
            Name = "Foreign Bread",
            ResourceName = "gfx/invobjs/bread",
            Energy = 100,
            Hunger = 0.5m,
            ImportedAt = now
        };
        _db.Foods.AddRange(bread, stew, foreign);
        _db.SaveChanges();

        _db.FoodVariants.AddRange(
            new FoodVariantEntity
            {
                TenantId = TenantA,
                FoodId = bread.Id,
                IngredientSignature = "Flour:100",
                Energy = 100,
                Hunger = 0.5m,
                TimesSeen = 3,
                Feps = new List<FoodFep> { new() { Attribute = "STR", Tier = 1, Value = 2m } }
            },
            new FoodVariantEntity
            {
                TenantId = TenantA,
                FoodId = bread.Id,
                IngredientSignature = "Flour:50|Water:50",
                Energy = 100,
                Hunger = 0.5m,
                TimesSeen = 1,
                Contributors = new List<string> { "user-1" },
                Feps = new List<FoodFep> { new() { Attribute = "STR", Tier = 1, Value = 3m } }
            },
            new FoodVariantEntity
            {
                TenantId = TenantA,
                FoodId = stew.Id,
                IngredientSignature = "Carrot:100",
                Energy = 200,
                Hunger = 1.5m,
                TimesSeen = 1,
                Contributors = new List<string> { "gone-user" },
                Feps = new List<FoodFep> { new() { Attribute = "AGI", Tier = 1, Value = 1m } }
            },
            new FoodVariantEntity
            {
                TenantId = TenantB,
                FoodId = foreign.Id,
                IngredientSignature = "Foreign:100",
                Energy = 100,
                Hunger = 0.5m,
                TimesSeen = 1
            });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        return (bread.Id, stew.Id);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch (IOException)
            {
                // Best effort — temp files are cleaned by the OS eventually.
            }
        }
    }
}
