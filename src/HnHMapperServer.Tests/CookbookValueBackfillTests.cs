using HnHMapperServer.Core.Cookbook;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// The canonical-value repair runs once, for every tenant, without anyone clicking
/// anything — and never again afterwards. Real SQLite: the marker is a Config row and
/// the repair itself pages with the change tracker cleared between batches.
/// </summary>
public class CookbookValueBackfillTests : IDisposable
{
    private const string TenantA = "backfill-a";
    private const string TenantB = "backfill-b";
    private const string GlobalTenantId = "__global__";
    private const string W162 = "fd63ddee958da329";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly FoodCatalogService _catalog;
    private readonly Mock<IAuditService> _audit = new();

    public CookbookValueBackfillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-cookbook-backfill-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        foreach (var tenant in new[] { GlobalTenantId, TenantA, TenantB })
        {
            _db.Tenants.Add(new TenantEntity
            {
                Id = tenant,
                Name = tenant,
                StorageQuotaMB = 1024,
                CurrentStorageMB = 0,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            });
        }

        _db.SaveChanges();

        _catalog = new FoodCatalogService(
            _db,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ITenantContextAccessor>(),
            Mock.Of<ILogger<FoodCatalogService>>());
    }

    [Fact]
    public async Task RunOnce_RepairsEveryTenant_ThenNeverAgain()
    {
        SeedWikiValuedFood(TenantA, "Seaberries", wikiHunger: 0.1m, clientHunger: 0.02m);
        SeedWikiValuedFood(TenantB, "Seaberries", wikiHunger: 0.1m, clientHunger: 0.02m);

        var first = await Backfill().RunOnceAsync();

        Assert.False(first.AlreadyApplied);
        Assert.Equal(2, first.TenantsProcessed);
        Assert.Equal(0, first.TenantsFailed);
        Assert.Equal(2, first.Updated);
        Assert.True(first.MarkerWritten);

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            var food = await LoadFoodAsync(tenant, "Seaberries");
            Assert.Equal(0.02m, food.Hunger);
            Assert.Equal(FoodValueSource.Upload, food.ValueSource);
        }

        // The marker makes every later start a no-op — including a start that would
        // otherwise re-run against foods a player has since corrected by hand.
        var second = await Backfill().RunOnceAsync();
        Assert.True(second.AlreadyApplied);
        Assert.Equal(0, second.TenantsProcessed);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public async Task RunOnce_WritesTheMarkerAsAGlobalConfigRow()
    {
        SeedWikiValuedFood(TenantA, "Seaberries", wikiHunger: 0.1m, clientHunger: 0.02m);

        await Backfill().RunOnceAsync();

        var marker = await _db.Config.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == GlobalTenantId && c.Key == "cookbook.valueBackfillVersion");
        Assert.Equal("1", marker.Value);
    }

    [Fact]
    public async Task RunOnce_AuditsOnlyTheTenantsItChanged()
    {
        SeedWikiValuedFood(TenantA, "Seaberries", wikiHunger: 0.1m, clientHunger: 0.02m);
        // Tenant B's food already matches what its client reported.
        SeedWikiValuedFood(TenantB, "Almonds", wikiHunger: 0.08m, clientHunger: 0.08m);

        var result = await Backfill().RunOnceAsync();

        Assert.Equal(2, result.TenantsProcessed);
        Assert.Equal(1, result.Updated);
        _audit.Verify(a => a.LogAsync(It.Is<AuditEntry>(e =>
            e.TenantId == TenantA && e.Action == "CookbookValuesRefreshed")), Times.Once);
        _audit.Verify(a => a.LogAsync(It.Is<AuditEntry>(e => e.TenantId == TenantB)), Times.Never);
    }

    [Fact]
    public async Task RunOnce_SkipsTheGlobalPseudoTenant()
    {
        SeedWikiValuedFood(TenantA, "Seaberries", wikiHunger: 0.1m, clientHunger: 0.02m);

        var result = await Backfill().RunOnceAsync();

        Assert.Equal(2, result.TenantsProcessed);   // A and B, never __global__
    }

    [Fact]
    public async Task RunOnce_WithholdsTheMarker_WhenATenantFails()
    {
        SeedWikiValuedFood(TenantA, "Seaberries", wikiHunger: 0.1m, clientHunger: 0.02m);

        var failing = new Mock<IFoodCatalogService>();
        failing.Setup(f => f.RefreshCanonicalValuesAsync(TenantA, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        failing.Setup(f => f.RefreshCanonicalValuesAsync(TenantB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CookbookValueRefreshResultDto { Foods = 1, Updated = 0 });

        var result = await new CookbookValueBackfill(
            _db, failing.Object, _audit.Object, Mock.Of<ILogger<CookbookValueBackfill>>()).RunOnceAsync();

        Assert.Equal(1, result.TenantsFailed);
        Assert.Equal(1, result.TenantsProcessed);   // the healthy tenant still ran
        Assert.False(result.MarkerWritten);

        var marker = await _db.Config.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Key == "cookbook.valueBackfillVersion");
        Assert.Null(marker);
    }

    private CookbookValueBackfill Backfill() => new(
        _db, _catalog, _audit.Object, Mock.Of<ILogger<CookbookValueBackfill>>());

    /// <summary>A food as the old wiki-first code left it: wiki values on the food row,
    /// the client's own numbers recorded only on its variation.</summary>
    private void SeedWikiValuedFood(string tenantId, string name, decimal wikiHunger, decimal clientHunger)
    {
        var food = new FoodEntity
        {
            TenantId = tenantId,
            Name = name,
            ResourceName = "gfx/invobjs/" + name.ToLowerInvariant(),
            Energy = 50,
            Hunger = wikiHunger,
            ImportedAt = DateTime.UtcNow,
            ValueSource = FoodValueSource.Wiki,
            Feps = new List<FoodFep> { new() { Attribute = "INT", Tier = 1, Value = 0.1m } }
        };
        _db.Foods.Add(food);
        _db.SaveChanges();

        _db.FoodVariants.Add(new FoodVariantEntity
        {
            TenantId = tenantId,
            FoodId = food.Id,
            IngredientSignature = string.Empty,
            Energy = 50,
            Hunger = clientHunger,
            TimesSeen = 1,
            Contributors = new List<string> { "player-1" },
            Worlds = new List<string> { W162 },
            WorldValues = new List<FoodVariantWorldValue>
            {
                new()
                {
                    Genus = W162,
                    Energy = 50,
                    Hunger = clientHunger,
                    Observed = true,
                    Feps = new List<FoodWorldFep> { new() { Attribute = "INT", Tier = 1, Value = 0.1m } }
                }
            },
            Feps = new List<FoodFep> { new() { Attribute = "INT", Tier = 1, Value = 0.1m } }
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private Task<FoodEntity> LoadFoodAsync(string tenantId, string name) =>
        _db.Foods.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(f => f.TenantId == tenantId && f.Name == name);

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch (IOException)
            {
                // Best effort — a locked temp file is not worth failing a test run over.
            }
        }
    }
}
