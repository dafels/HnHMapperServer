using System.Text;
using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Cookbook export → import roundtrip tests.
///
/// These run against real SQLite (not the in-memory provider): the import's
/// wipe-and-replace path uses ExecuteDelete, which the in-memory provider does not
/// implement, and the unique indexes the import must respect only exist for real.
/// </summary>
public class FoodCatalogServiceExportImportTests : IDisposable
{
    private const string SourceTenant = "export-src-1";
    private const string TargetTenant = "import-dst-2";
    private const string AliceId = "user-alice";
    private const string BobId = "user-bob";
    private const string W16 = "c646473983afec09";
    private const string W161 = "b7c199a4557503a8";

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly FoodCatalogService _service;

    public FoodCatalogServiceExportImportTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-cookbook-export-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
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
    public async Task ExportAsync_ProducesPortableTenantScopedSnapshot()
    {
        var export = await _service.ExportAsync(SourceTenant);

        Assert.Equal(CookbookExportDto.FormatMarker, export.Format);
        Assert.Equal(CookbookExportDto.CurrentVersion, export.Version);
        Assert.Equal(2, export.FoodCount);
        Assert.Equal(3, export.VariantCount);

        // Only the source tenant's foods, and contributors as usernames, not user ids.
        Assert.DoesNotContain(export.Foods, f => f.Name == "Other Tenant Stew");
        var steak = Assert.Single(export.Foods, f => f.Name == "Autumn Steak");
        Assert.Equal("alice", steak.ContributedBy);
        Assert.Equal(new List<string> { W16, W161 }, steak.Worlds);

        var taggedVariant = Assert.Single(steak.Variants, v => v.Worlds.Count > 0);
        Assert.Equal(new List<string> { "alice", "bob" }, taggedVariant.Contributors);
        Assert.Equal(3, taggedVariant.TimesSeen);
        var worldValue = Assert.Single(taggedVariant.WorldValues);
        Assert.Equal(W161, worldValue.Genus);
        Assert.Equal(250, worldValue.Energy);

        // A contributor whose account is gone is dropped rather than exported as an id.
        var slime = Assert.Single(export.Foods, f => f.Name == "Cave Slime");
        Assert.Null(slime.ContributedBy);
    }

    [Fact]
    public async Task ImportAsync_RestoresExportedSnapshotIntoAnotherTenant()
    {
        var export = await _service.ExportAsync(SourceTenant);
        var result = await _service.ImportAsync(ToStream(export), null, TargetTenant);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Imported);
        Assert.Equal(3, result.Variants);
        Assert.Equal(0, result.Skipped);

        var foods = await _db.Foods.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == TargetTenant)
            .OrderBy(f => f.Name)
            .ToListAsync();
        Assert.Equal(2, foods.Count);

        var steak = foods.Single(f => f.Name == "Autumn Steak");
        Assert.Equal("gfx/invobjs/autumnsteak", steak.ResourceName);
        Assert.Equal(new List<string> { W16, W161 }, steak.Worlds);
        Assert.Equal(new List<string> { "Meat" }, steak.SatiationGroups);
        Assert.Equal("https://ringofbrodgar.com/wiki/Autumn_Steak", steak.WikiUrl);
        // Contributor username resolved back to the local account id.
        Assert.Equal(AliceId, steak.ContributedBy);
        // Discovery date survives the roundtrip.
        Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), steak.ImportedAt);

        var variants = await _db.FoodVariants.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => v.TenantId == TargetTenant && v.FoodId == steak.Id)
            .ToListAsync();
        Assert.Equal(2, variants.Count);

        var tagged = variants.Single(v => v.Worlds.Count > 0);
        Assert.Equal("Raw Meat:100", tagged.IngredientSignature);
        Assert.Equal(3, tagged.TimesSeen);
        Assert.Equal(new List<string> { AliceId, BobId }, tagged.Contributors);
        Assert.Equal(new List<string> { W161 }, tagged.Worlds);
        var worldValue = Assert.Single(tagged.WorldValues);
        Assert.Equal(W161, worldValue.Genus);
        Assert.Equal(250, worldValue.Energy);
        Assert.Equal(2, worldValue.Feps.Count);

        // The source tenant is untouched.
        Assert.Equal(2, await _db.Foods.IgnoreQueryFilters().CountAsync(f => f.TenantId == SourceTenant));
    }

    [Fact]
    public async Task ImportAsync_SnapshotDropsUnknownContributorNames()
    {
        var export = await _service.ExportAsync(SourceTenant);
        export.Foods[0].ContributedBy = "nobody-here";
        foreach (var variant in export.Foods.SelectMany(f => f.Variants))
        {
            variant.Contributors = new List<string> { "nobody-here", "bob" };
        }

        var result = await _service.ImportAsync(ToStream(export), null, TargetTenant);
        Assert.Empty(result.Errors);

        var foods = await _db.Foods.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == TargetTenant)
            .OrderBy(f => f.Name)
            .ToListAsync();
        Assert.Null(foods[0].ContributedBy);

        var variants = await _db.FoodVariants.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => v.TenantId == TargetTenant)
            .ToListAsync();
        Assert.All(variants, v => Assert.Equal(new List<string> { BobId }, v.Contributors));
    }

    [Fact]
    public async Task ImportAsync_RejectsObjectFileWithoutFormatMarker_AndLeavesCatalogAlone()
    {
        // wiki-food-data.json is also object-rooted — it must not be mistaken for an export.
        var wikiLike = """{"Autumn Steak": {"title": "Autumn Steak", "url": "x"}}""";

        var result = await _service.ImportAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(wikiLike)), null, SourceTenant);

        Assert.Equal(0, result.Imported);
        Assert.Contains(result.Errors, e => e.Contains("Unrecognized food data file"));
        Assert.Equal(2, await _db.Foods.IgnoreQueryFilters().CountAsync(f => f.TenantId == SourceTenant));
    }

    [Fact]
    public async Task ImportAsync_RejectsNewerExportVersion()
    {
        var export = await _service.ExportAsync(SourceTenant);
        export.Version = CookbookExportDto.CurrentVersion + 1;

        var result = await _service.ImportAsync(ToStream(export), null, TargetTenant);

        Assert.Equal(0, result.Imported);
        Assert.Contains(result.Errors, e => e.Contains("newer than this server supports"));
    }

    [Fact]
    public async Task ImportAsync_StillAcceptsTheRawGameDumpArray()
    {
        var dump = """
            [
              {
                "itemName": "0.1 kg of Dump Sausage",
                "resourceName": "gfx/invobjs/sausage",
                "hunger": 1.5,
                "energy": 200,
                "feps": [{"name": "Strength +1", "value": 2.0}],
                "ingredients": [{"name": "Raw Meat", "percentage": 100}]
              }
            ]
            """;

        var result = await _service.ImportAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(dump)), null, TargetTenant);

        Assert.Equal(1, result.Imported);
        var food = await _db.Foods.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(f => f.TenantId == TargetTenant);
        Assert.Equal("Dump Sausage", food.Name);
        var fep = Assert.Single(food.Feps);
        Assert.Equal("STR", fep.Attribute);
    }

    private static MemoryStream ToStream(CookbookExportDto export) =>
        // Same wire shape as the export endpoint (camelCase JSON).
        new(JsonSerializer.SerializeToUtf8Bytes(export, WireOptions));

    private void Seed()
    {
        var now = DateTime.UtcNow;

        _db.Users.Add(new ApplicationUser { Id = AliceId, UserName = "alice", NormalizedUserName = "ALICE" });
        _db.Users.Add(new ApplicationUser { Id = BobId, UserName = "bob", NormalizedUserName = "BOB" });

        foreach (var tenantId in new[] { SourceTenant, TargetTenant })
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

        var steak = new FoodEntity
        {
            TenantId = SourceTenant,
            Name = "Autumn Steak",
            ResourceName = "gfx/invobjs/autumnsteak",
            Energy = 300,
            Hunger = 1.25m,
            WikiUrl = "https://ringofbrodgar.com/wiki/Autumn_Steak",
            RecipeText = "Raw Meat",
            CookingStation = "Frying Pan and Fire",
            ImportedAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            ContributedBy = AliceId,
            Categories = new List<string> { "Meat Dishes" },
            SatiationGroups = new List<string> { "Meat" },
            Worlds = new List<string> { W16, W161 },
            Feps = new List<FoodFep>
            {
                new() { Attribute = "STR", Tier = 1, Value = 2.5m },
                new() { Attribute = "STR", Tier = 2, Value = 1.0m }
            },
            Ingredients = new List<FoodIngredient> { new() { Name = "Raw Meat", Percentage = 100 } }
        };

        var slime = new FoodEntity
        {
            TenantId = SourceTenant,
            Name = "Cave Slime",
            ResourceName = "gfx/invobjs/caveslime",
            Energy = 100,
            Hunger = 0.25m,
            ImportedAt = now,
            // Account deleted: the id resolves to no username and must not leak into the export.
            ContributedBy = "deleted-user-id"
        };

        var otherTenantFood = new FoodEntity
        {
            TenantId = TargetTenant,
            Name = "Other Tenant Stew",
            ResourceName = "gfx/invobjs/stew",
            Energy = 150,
            Hunger = 1m,
            ImportedAt = now
        };

        _db.Foods.AddRange(steak, slime, otherTenantFood);
        _db.SaveChanges();

        _db.FoodVariants.AddRange(
            new FoodVariantEntity
            {
                TenantId = SourceTenant,
                FoodId = steak.Id,
                IngredientSignature = "Raw Meat:100",
                Energy = 300,
                Hunger = 1.25m,
                TimesSeen = 3,
                Contributors = new List<string> { AliceId, BobId },
                Worlds = new List<string> { W161 },
                WorldValues = new List<FoodVariantWorldValue>
                {
                    new()
                    {
                        Genus = W161,
                        Energy = 250,
                        Hunger = 1.2m,
                        Feps = new List<FoodWorldFep>
                        {
                            new() { Attribute = "STR", Tier = 1, Value = 2.4m },
                            new() { Attribute = "STR", Tier = 2, Value = 0.9m }
                        }
                    }
                },
                Feps = new List<FoodFep> { new() { Attribute = "STR", Tier = 1, Value = 2.5m } },
                Ingredients = new List<FoodIngredient> { new() { Name = "Raw Meat", Percentage = 100 } }
            },
            new FoodVariantEntity
            {
                TenantId = SourceTenant,
                FoodId = steak.Id,
                IngredientSignature = "Raw Meat:50|Salt:50",
                Energy = 300,
                Hunger = 1.1m,
                TimesSeen = 1,
                Ingredients = new List<FoodIngredient>
                {
                    new() { Name = "Raw Meat", Percentage = 50 },
                    new() { Name = "Salt", Percentage = 50 }
                }
            },
            new FoodVariantEntity
            {
                TenantId = SourceTenant,
                FoodId = slime.Id,
                IngredientSignature = string.Empty,
                Energy = 100,
                Hunger = 0.25m,
                TimesSeen = 1
            },
            new FoodVariantEntity
            {
                TenantId = TargetTenant,
                FoodId = otherTenantFood.Id,
                IngredientSignature = string.Empty,
                Energy = 150,
                Hunger = 1m,
                TimesSeen = 1
            });
        _db.SaveChanges();
    }

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
                // Best effort — temp files are cleaned up by the OS eventually.
            }
        }
    }
}
