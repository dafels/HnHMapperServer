using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Bulk "assign untagged cookbook data to a world" tests.
///
/// These run against real SQLite (not the in-memory provider): the assignment
/// filters on Worlds.Count == 0, which must translate to json_each SQL for the
/// keyset paging to work, and only the real provider proves that.
/// </summary>
public class FoodCatalogServiceWorldAssignTests : IDisposable
{
    private const string TenantA = "assign-a";
    private const string TenantB = "assign-b";
    private const string W16 = "c646473983afec09";
    private const string W161 = "b7c199a4557503a8";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly FoodCatalogService _service;

    public FoodCatalogServiceWorldAssignTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-cookbook-assign-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        Seed();

        _service = new FoodCatalogService(
            _db,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ITenantContextAccessor>(),
            Mock.Of<ILogger<FoodCatalogService>>());
    }

    [Fact]
    public async Task AssignUntagged_TagsFoodsAndVariants_AndSeedsWorldValuesFromCanonical()
    {
        var result = await _service.AssignUntaggedToWorldAsync(TenantA, W161);

        // Untagged Stew + Lonely Loaf (fully untagged) + Tagged Steak (gains the world
        // via its transferred variant); Sealed Pie is fully tagged and untouched.
        Assert.Equal(3, result.Foods);
        Assert.Equal(3, result.Variants);
        Assert.Equal(W161, result.World);

        var stew = await LoadFoodAsync(TenantA, "Untagged Stew");
        Assert.Equal(new List<string> { W161 }, stew.Worlds);

        var stewVariants = await LoadVariantsAsync(stew.Id);
        Assert.Equal(2, stewVariants.Count);
        foreach (var variant in stewVariants)
        {
            Assert.Equal(new List<string> { W161 }, variant.Worlds);
            var seeded = Assert.Single(variant.WorldValues);
            Assert.Equal(W161, seeded.Genus);
            // The seed is a copy of the canonical (all-worlds merge) columns.
            Assert.Equal(variant.Energy, seeded.Energy);
            Assert.Equal(variant.Hunger, seeded.Hunger);
            Assert.Equal(
                variant.Feps.Select(f => (f.Attribute, f.Tier, f.Value)),
                seeded.Feps.Select(f => (f.Attribute, f.Tier, f.Value)));
        }

        // Everything else on the variant is untouched.
        var first = stewVariants.Single(v => v.IngredientSignature == "Carrot:100");
        Assert.Equal(2, first.TimesSeen);
        Assert.Equal(new List<string> { "user-1" }, first.Contributors);

        // A variant-less untagged food is still tagged (the Untagged bucket must empty).
        var loaf = await LoadFoodAsync(TenantA, "Lonely Loaf");
        Assert.Equal(new List<string> { W161 }, loaf.Worlds);
    }

    [Fact]
    public async Task AssignUntagged_MergesIntoAlreadyTaggedFood_WithoutTouchingExistingTags()
    {
        await _service.AssignUntaggedToWorldAsync(TenantA, W161);

        // The W16-tagged food gains W161 (its untagged variant transferred), deduped.
        var steak = await LoadFoodAsync(TenantA, "Tagged Steak");
        Assert.Equal(new List<string> { W16, W161 }, steak.Worlds);

        var variants = await LoadVariantsAsync(steak.Id);

        // Pre-existing tagged variant: worlds and snapshot byte-identical.
        var tagged = variants.Single(v => v.IngredientSignature == "Raw Meat:100");
        Assert.Equal(new List<string> { W16 }, tagged.Worlds);
        var existing = Assert.Single(tagged.WorldValues);
        Assert.Equal(W16, existing.Genus);
        Assert.Equal(250, existing.Energy);
        Assert.Equal(1.2m, existing.Hunger);

        // The untagged sibling transferred and got its own W161 snapshot.
        var transferred = variants.Single(v => v.IngredientSignature == "Raw Meat:50|Salt:50");
        Assert.Equal(new List<string> { W161 }, transferred.Worlds);
        Assert.Equal(W161, Assert.Single(transferred.WorldValues).Genus);

        // A fully tagged food (and its variant) is completely untouched.
        var pie = await LoadFoodAsync(TenantA, "Sealed Pie");
        Assert.Equal(new List<string> { W161 }, pie.Worlds);
        var pieVariant = Assert.Single(await LoadVariantsAsync(pie.Id));
        Assert.Equal(new List<string> { W161 }, pieVariant.Worlds);
        Assert.Equal(111, Assert.Single(pieVariant.WorldValues).Energy);
    }

    [Fact]
    public async Task AssignUntagged_SecondRunIsANoOp()
    {
        await _service.AssignUntaggedToWorldAsync(TenantA, W161);
        var second = await _service.AssignUntaggedToWorldAsync(TenantA, W161);

        Assert.Equal(0, second.Foods);
        Assert.Equal(0, second.Variants);

        // No duplicate tags or snapshots from the re-run.
        var stew = await LoadFoodAsync(TenantA, "Untagged Stew");
        Assert.Equal(new List<string> { W161 }, stew.Worlds);
        var variant = (await LoadVariantsAsync(stew.Id)).First();
        Assert.Equal(new List<string> { W161 }, variant.Worlds);
        Assert.Single(variant.WorldValues);
    }

    [Fact]
    public async Task AssignUntagged_LeavesOtherTenantsAlone()
    {
        await _service.AssignUntaggedToWorldAsync(TenantA, W161);

        var otherFood = await LoadFoodAsync(TenantB, "Other Tenant Stew");
        Assert.Empty(otherFood.Worlds);
        var otherVariant = Assert.Single(await LoadVariantsAsync(otherFood.Id));
        Assert.Empty(otherVariant.Worlds);
        Assert.Empty(otherVariant.WorldValues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("untagged")]
    [InlineData("UNTAGGED")]
    [InlineData("ffffffffffffffff")] // normalizable but not a known world
    public async Task AssignUntagged_RejectsInvalidWorlds(string world)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.AssignUntaggedToWorldAsync(TenantA, world));

        // Nothing was tagged by the failed call.
        var status = await _service.GetStatusAsync(TenantA);
        Assert.Equal(2, status.UntaggedFoodCount);
        Assert.Equal(3, status.UntaggedVariantCount);
    }

    [Fact]
    public async Task GetStatus_ReportsUntaggedCounts_BeforeAndAfterAssignment()
    {
        var before = await _service.GetStatusAsync(TenantA);
        Assert.Equal(4, before.FoodCount);
        Assert.Equal(5, before.VariantCount);        // 2 stew + 2 steak + 1 pie
        Assert.Equal(2, before.UntaggedFoodCount);   // Untagged Stew, Lonely Loaf
        Assert.Equal(3, before.UntaggedVariantCount); // 2 stew + 1 steak variant

        await _service.AssignUntaggedToWorldAsync(TenantA, W161);

        var after = await _service.GetStatusAsync(TenantA);
        Assert.Equal(4, after.FoodCount);
        Assert.Equal(5, after.VariantCount);
        Assert.Equal(0, after.UntaggedFoodCount);
        Assert.Equal(0, after.UntaggedVariantCount);
    }

    [Fact]
    public async Task AssignUntagged_TagsSurviveExport()
    {
        await _service.AssignUntaggedToWorldAsync(TenantA, W161);

        var export = await _service.ExportAsync(TenantA);

        var stew = Assert.Single(export.Foods, f => f.Name == "Untagged Stew");
        Assert.Equal(new List<string> { W161 }, stew.Worlds);
        Assert.All(stew.Variants, v =>
        {
            Assert.Equal(new List<string> { W161 }, v.Worlds);
            Assert.Equal(W161, Assert.Single(v.WorldValues).Genus);
        });
    }

    [Fact]
    public async Task AssignUntagged_HandlesMoreVariantsThanOneBatch()
    {
        // VariantBatchSize is 2000 — one over forces a second keyset page, proving
        // the paging + ChangeTracker.Clear-per-batch loop inside the transaction.
        const int variantTotal = 2001;
        const string bulkTenant = "assign-bulk";

        _db.Tenants.Add(new TenantEntity
        {
            Id = bulkTenant,
            Name = bulkTenant,
            StorageQuotaMB = 1024,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });

        var food = new FoodEntity
        {
            TenantId = bulkTenant,
            Name = "Bulk Bread",
            ResourceName = "gfx/invobjs/bread",
            Energy = 100,
            Hunger = 0.5m,
            ImportedAt = DateTime.UtcNow
        };
        _db.Foods.Add(food);
        _db.SaveChanges();

        for (var i = 0; i < variantTotal; i++)
        {
            _db.FoodVariants.Add(new FoodVariantEntity
            {
                TenantId = bulkTenant,
                FoodId = food.Id,
                IngredientSignature = $"Ing:{i}",
                Energy = 100,
                Hunger = 0.5m,
                TimesSeen = 1
            });
        }
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        var result = await _service.AssignUntaggedToWorldAsync(bulkTenant, W161);

        Assert.Equal(1, result.Foods);
        Assert.Equal(variantTotal, result.Variants);

        var untaggedLeft = await _db.FoodVariants.IgnoreQueryFilters()
            .Where(v => v.TenantId == bulkTenant && v.Worlds.Count == 0)
            .CountAsync();
        Assert.Equal(0, untaggedLeft);
    }

    private Task<FoodEntity> LoadFoodAsync(string tenantId, string name) =>
        _db.Foods.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(f => f.TenantId == tenantId && f.Name == name);

    private Task<List<FoodVariantEntity>> LoadVariantsAsync(int foodId) =>
        _db.FoodVariants.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => v.FoodId == foodId)
            .OrderBy(v => v.Id)
            .ToListAsync();

    private void Seed()
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
        _db.SaveChanges();

        // Fully untagged food with two untagged variants.
        var stew = new FoodEntity
        {
            TenantId = TenantA,
            Name = "Untagged Stew",
            ResourceName = "gfx/invobjs/stew",
            Energy = 200,
            Hunger = 1.5m,
            ImportedAt = now
        };

        // Already-tagged food that still has one untagged (pre-tagging) variant.
        var steak = new FoodEntity
        {
            TenantId = TenantA,
            Name = "Tagged Steak",
            ResourceName = "gfx/invobjs/steak",
            Energy = 300,
            Hunger = 1.25m,
            ImportedAt = now,
            Worlds = new List<string> { W16 }
        };

        // Fully tagged food — must be completely untouched by the assignment.
        var pie = new FoodEntity
        {
            TenantId = TenantA,
            Name = "Sealed Pie",
            ResourceName = "gfx/invobjs/pie",
            Energy = 400,
            Hunger = 2m,
            ImportedAt = now,
            Worlds = new List<string> { W161 }
        };

        // Untagged food with no variants at all (wiki-only rows exist in real catalogs).
        var loaf = new FoodEntity
        {
            TenantId = TenantA,
            Name = "Lonely Loaf",
            ResourceName = "gfx/invobjs/loaf",
            Energy = 150,
            Hunger = 1m,
            ImportedAt = now
        };

        var otherTenantFood = new FoodEntity
        {
            TenantId = TenantB,
            Name = "Other Tenant Stew",
            ResourceName = "gfx/invobjs/stew",
            Energy = 150,
            Hunger = 1m,
            ImportedAt = now
        };

        _db.Foods.AddRange(stew, steak, pie, loaf, otherTenantFood);
        _db.SaveChanges();

        _db.FoodVariants.AddRange(
            new FoodVariantEntity
            {
                TenantId = TenantA,
                FoodId = stew.Id,
                IngredientSignature = "Carrot:100",
                Energy = 180,
                Hunger = 1.4m,
                TimesSeen = 2,
                Contributors = new List<string> { "user-1" },
                Feps = new List<FoodFep>
                {
                    new() { Attribute = "STR", Tier = 1, Value = 2.0m },
                    new() { Attribute = "INT", Tier = 2, Value = 0.5m }
                },
                Ingredients = new List<FoodIngredient> { new() { Name = "Carrot", Percentage = 100 } }
            },
            new FoodVariantEntity
            {
                TenantId = TenantA,
                FoodId = stew.Id,
                IngredientSignature = "Beet:100",
                Energy = 200,
                Hunger = 1.5m,
                TimesSeen = 1,
                Feps = new List<FoodFep> { new() { Attribute = "AGI", Tier = 1, Value = 1.0m } }
            },
            new FoodVariantEntity
            {
                TenantId = TenantA,
                FoodId = steak.Id,
                IngredientSignature = "Raw Meat:100",
                Energy = 300,
                Hunger = 1.25m,
                TimesSeen = 3,
                Worlds = new List<string> { W16 },
                WorldValues = new List<FoodVariantWorldValue>
                {
                    new()
                    {
                        Genus = W16,
                        Energy = 250,
                        Hunger = 1.2m,
                        Feps = new List<FoodWorldFep> { new() { Attribute = "STR", Tier = 1, Value = 2.4m } }
                    }
                },
                Feps = new List<FoodFep> { new() { Attribute = "STR", Tier = 1, Value = 2.5m } }
            },
            new FoodVariantEntity
            {
                TenantId = TenantA,
                FoodId = steak.Id,
                IngredientSignature = "Raw Meat:50|Salt:50",
                Energy = 300,
                Hunger = 1.1m,
                TimesSeen = 1,
                Feps = new List<FoodFep> { new() { Attribute = "STR", Tier = 1, Value = 2.2m } }
            },
            new FoodVariantEntity
            {
                TenantId = TenantA,
                FoodId = pie.Id,
                IngredientSignature = "Flour:100",
                Energy = 400,
                Hunger = 2m,
                TimesSeen = 1,
                Worlds = new List<string> { W161 },
                WorldValues = new List<FoodVariantWorldValue>
                {
                    new() { Genus = W161, Energy = 111, Hunger = 1.9m }
                }
            },
            new FoodVariantEntity
            {
                TenantId = TenantB,
                FoodId = otherTenantFood.Id,
                IngredientSignature = string.Empty,
                Energy = 150,
                Hunger = 1m,
                TimesSeen = 1
            });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
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
                // Best effort — temp files are cleaned up by the OS eventually.
            }
        }
    }
}
