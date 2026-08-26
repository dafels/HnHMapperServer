using System.Text;
using HnHMapperServer.Core.Cookbook;
using HnHMapperServer.Core.DTOs;
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
/// Canonical food values come from the game client, not the bundled wiki dump.
///
/// The client reports what the running world actually gives; the wiki is community data
/// that is not revisited every world, and it used to win at food creation — so a food a
/// player's own client discovered still carried wiki numbers, and later uploads never
/// corrected them. These cover the repair pass and the live precedence that keeps it fixed.
///
/// Real SQLite (not the in-memory provider): the refresh pages foods with keyset paging
/// and clears the change tracker per batch, and the JSON-column world snapshots have to
/// round-trip for the Observed flag to mean anything.
/// </summary>
public class FoodCatalogCanonicalValueTests : IDisposable
{
    private const string TenantA = "values-a";
    private const string TenantB = "values-b";
    private const string W161 = "b7c199a4557503a8";
    private const string W162 = "fd63ddee958da329";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly FoodCatalogService _service;

    public FoodCatalogCanonicalValueTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-cookbook-values-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        foreach (var tenant in new[] { TenantA, TenantB })
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

        _service = new FoodCatalogService(
            _db,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ITenantContextAccessor>(),
            Mock.Of<ILogger<FoodCatalogService>>());
    }

    // ----- the repair pass ---------------------------------------------------

    [Fact]
    public async Task Refresh_ReplacesWikiValues_WithWhatTheClientReported()
    {
        // The exact shape of the reported Seaberries row: wiki hunger 0.1 on the food,
        // the client's 0.02 sitting unused on the variation.
        var food = SeedFood("Seaberries", energy: 50, hunger: 0.1m, ("INT", 1, 0.1m));
        SeedVariant(food, signature: string.Empty, energy: 50, hunger: 0.02m,
            feps: new[] { ("INT", 1, 0.1m) },
            contributors: new[] { "player-1" },
            worldValues: new[] { (W162, 50, 0.02m, new[] { ("INT", 1, 0.1m) }, true) });

        var result = await _service.RefreshCanonicalValuesAsync(TenantA);

        Assert.Equal(1, result.Foods);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.FromUploads);
        Assert.Equal(0, result.LeftOnWiki);

        var updated = await LoadFoodAsync(TenantA, "Seaberries");
        Assert.Equal(0.02m, updated.Hunger);
        Assert.Equal(50, updated.Energy);
        Assert.Equal(FoodValueSource.Upload, updated.ValueSource);
        Assert.Equal(W162, updated.ValueWorld);
    }

    [Fact]
    public async Task Refresh_PrefersRealObservation_OverSeededWorldSnapshot()
    {
        // A seeded snapshot is an admin's assertion that stored data belongs to a world
        // (bulk world assignment). A real observation from an OLDER world still wins.
        var food = SeedFood("Bat Wing Soup", energy: 400, hunger: 3m, ("STR", 1, 5m));
        SeedVariant(food, signature: string.Empty, energy: 400, hunger: 3m,
            feps: new[] { ("STR", 1, 5m) },
            worldValues: new[]
            {
                (W162, 400, 3m, new[] { ("STR", 1, 5m) }, false),   // seeded from stored columns
                (W161, 410, 1.5m, new[] { ("STR", 1, 7m) }, true)   // a player actually ate one
            });

        await _service.RefreshCanonicalValuesAsync(TenantA);

        var updated = await LoadFoodAsync(TenantA, "Bat Wing Soup");
        Assert.Equal(410, updated.Energy);
        Assert.Equal(1.5m, updated.Hunger);
        Assert.Equal(W161, updated.ValueWorld);
        Assert.Equal(FoodValueSource.Upload, updated.ValueSource);
    }

    [Fact]
    public async Task Refresh_PrefersNewestWorld_AmongRealObservations()
    {
        var food = SeedFood("Roast Chicken", energy: 300, hunger: 2m, ("CON", 1, 4m));
        SeedVariant(food, signature: string.Empty, energy: 300, hunger: 2m,
            feps: new[] { ("CON", 1, 4m) },
            worldValues: new[]
            {
                (W161, 300, 2m, new[] { ("CON", 1, 4m) }, true),
                (W162, 300, 0.8m, new[] { ("CON", 1, 9m) }, true)   // rebalanced this world
            });

        await _service.RefreshCanonicalValuesAsync(TenantA);

        var updated = await LoadFoodAsync(TenantA, "Roast Chicken");
        Assert.Equal(0.8m, updated.Hunger);
        Assert.Equal(W162, updated.ValueWorld);
        var fep = Assert.Single(updated.Feps);
        Assert.Equal(9m, fep.Value);
    }

    [Fact]
    public async Task Refresh_PrefersThePlainItem_OverARecipeVariation()
    {
        // Headline values describe the food itself, so the ingredient-less observation
        // wins even though a recipe variation of the same world is also on file.
        var food = SeedFood("Meat Pie", energy: 500, hunger: 4m, ("STR", 1, 10m));
        SeedVariant(food, signature: "Boar Meat:100", energy: 520, hunger: 5m,
            feps: new[] { ("STR", 1, 22m) },
            worldValues: new[] { (W162, 520, 5m, new[] { ("STR", 1, 22m) }, true) });
        SeedVariant(food, signature: string.Empty, energy: 500, hunger: 2.5m,
            feps: new[] { ("STR", 1, 12m) },
            worldValues: new[] { (W162, 500, 2.5m, new[] { ("STR", 1, 12m) }, true) });

        await _service.RefreshCanonicalValuesAsync(TenantA);

        var updated = await LoadFoodAsync(TenantA, "Meat Pie");
        Assert.Equal(2.5m, updated.Hunger);
        Assert.Equal(12m, Assert.Single(updated.Feps).Value);
    }

    [Fact]
    public async Task Refresh_DoesNotAdoptAZeroHunger_TheClientRoundedAway()
    {
        // The client rounds hunger to 2 decimals, so a food below 0.005 arrives as 0.
        // Taking it would make FEP/hunger meaningless, so hunger comes from the next
        // observation that has one — here the older world's.
        var food = SeedFood("Cave Slime", energy: 30, hunger: 0.75m, ("PSY", 1, 0.2m));
        SeedVariant(food, signature: string.Empty, energy: 30, hunger: 0m,
            feps: new[] { ("PSY", 1, 0.2m) },
            worldValues: new[]
            {
                (W162, 30, 0m, new[] { ("PSY", 1, 0.2m) }, true),
                (W161, 30, 0.01m, new[] { ("PSY", 1, 0.2m) }, true)
            });

        var result = await _service.RefreshCanonicalValuesAsync(TenantA);

        Assert.Equal(1, result.HungerFallbacks);
        var updated = await LoadFoodAsync(TenantA, "Cave Slime");
        Assert.Equal(0.01m, updated.Hunger);
        Assert.Equal(W162, updated.ValueWorld);   // FEPs/energy still from the newest world
    }

    [Fact]
    public async Task Refresh_IgnoresAnInformationFreeObservation()
    {
        // Liquids: names are volume-normalized, so a "0.01 l of Cave Slime" sip collapses
        // onto the food and its energy/FEPs/hunger all round to zero. Adopting it would
        // wipe the real values (measured on the dev database: E=200 -> 4, every FEP 0).
        SeedFoodWithFeps("Cave Slime", energy: 200, hunger: 0.75m,
            new[] { ("STR", 2, 1m), ("CON", 2, 1m), ("PSY", 2, 1m) });
        var food = await LoadFoodAsync(TenantA, "Cave Slime");
        SeedVariant(food, signature: string.Empty, energy: 4, hunger: 0m,
            feps: new[] { ("STR", 2, 0m), ("CON", 2, 0m), ("PSY", 2, 0m) });

        var result = await _service.RefreshCanonicalValuesAsync(TenantA);

        Assert.Equal(0, result.Updated);
        var untouched = await LoadFoodAsync(TenantA, "Cave Slime");
        Assert.Equal(200, untouched.Energy);
        Assert.Equal(0.75m, untouched.Hunger);
        Assert.Equal(1m, untouched.Feps[0].Value);
    }

    [Fact]
    public async Task Upload_OfAnInformationFreeRecord_LeavesValuesAlone()
    {
        SeedFoodWithFeps("Cave Slime", energy: 200, hunger: 0.75m, new[] { ("PSY", 2, 1m) },
            valueSource: FoodValueSource.Upload, valueWorld: W161);

        await _service.IngestClientRecordsAsync(TenantA, "player-1", new List<FoodUploadRecordDto>
        {
            Upload("Cave Slime", "gfx/invobjs/caveslime", energy: 4, hunger: 0m, genus: W162,
                feps: new[] { ("Psyche +2", 0m) })
        });

        var food = await LoadFoodAsync(TenantA, "Cave Slime");
        Assert.Equal(200, food.Energy);
        Assert.Equal(0.75m, food.Hunger);
        Assert.Equal(1m, Assert.Single(food.Feps).Value);
    }

    [Fact]
    public async Task Refresh_FallsBackToImportedGameData_WhenNothingWasObserved()
    {
        // The usual state of a tenant seeded from a game-data dump: no uploads at all,
        // but the dump's own values still beat the wiki's.
        var food = SeedFood("Bark Bread", energy: 500, hunger: 2.5m, ("CON", 1, 5m));
        SeedVariant(food, signature: string.Empty, energy: 500, hunger: 1.8m,
            feps: new[] { ("CON", 1, 5m), ("WILL", 1, 4.5m) });

        var result = await _service.RefreshCanonicalValuesAsync(TenantA);

        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.FromImports);
        Assert.Equal(0, result.FromUploads);

        var updated = await LoadFoodAsync(TenantA, "Bark Bread");
        Assert.Equal(1.8m, updated.Hunger);
        Assert.Equal(2, updated.Feps.Count);
        Assert.Equal(FoodValueSource.Import, updated.ValueSource);
        Assert.Null(updated.ValueWorld);
    }

    [Fact]
    public async Task Refresh_LeavesAFoodWithNoRecordedVariationAlone()
    {
        SeedFood("Yesteryear's Seaberries", energy: 50, hunger: 0.1m, ("INT", 1, 0.08m));

        var result = await _service.RefreshCanonicalValuesAsync(TenantA);

        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.LeftOnWiki);

        var untouched = await LoadFoodAsync(TenantA, "Yesteryear's Seaberries");
        Assert.Equal(0.1m, untouched.Hunger);
        Assert.Equal(FoodValueSource.Wiki, untouched.ValueSource);
    }

    [Fact]
    public async Task Refresh_IsIdempotent()
    {
        var food = SeedFood("Seaberries", energy: 50, hunger: 0.1m, ("INT", 1, 0.1m));
        SeedVariant(food, signature: string.Empty, energy: 50, hunger: 0.02m,
            feps: new[] { ("INT", 1, 0.1m) },
            worldValues: new[] { (W162, 50, 0.02m, new[] { ("INT", 1, 0.1m) }, true) });

        await _service.RefreshCanonicalValuesAsync(TenantA);
        var second = await _service.RefreshCanonicalValuesAsync(TenantA);

        Assert.Equal(1, second.Foods);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public async Task Refresh_LeavesOtherTenantsAlone()
    {
        var mine = SeedFood("Seaberries", energy: 50, hunger: 0.1m, ("INT", 1, 0.1m));
        SeedVariant(mine, signature: string.Empty, energy: 50, hunger: 0.02m,
            feps: new[] { ("INT", 1, 0.1m) },
            worldValues: new[] { (W162, 50, 0.02m, new[] { ("INT", 1, 0.1m) }, true) });

        var theirs = SeedFood("Seaberries", energy: 50, hunger: 0.1m, feps: ("INT", 1, 0.1m), tenantId: TenantB);
        SeedVariant(theirs, signature: string.Empty, energy: 50, hunger: 0.02m,
            feps: new[] { ("INT", 1, 0.1m) },
            worldValues: new[] { (W162, 50, 0.02m, new[] { ("INT", 1, 0.1m) }, true) },
            tenantId: TenantB);

        var result = await _service.RefreshCanonicalValuesAsync(TenantA);

        Assert.Equal(1, result.Foods);
        var other = await LoadFoodAsync(TenantB, "Seaberries");
        Assert.Equal(0.1m, other.Hunger);
        Assert.Equal(FoodValueSource.Wiki, other.ValueSource);
    }

    [Fact]
    public async Task Refresh_PagesThroughMoreFoodsThanOneBatch()
    {
        // FoodBatchSize is 200 — one over proves the keyset loop and the
        // ChangeTracker.Clear between pages.
        const int foodTotal = 201;
        for (var i = 0; i < foodTotal; i++)
        {
            var food = SeedFood($"Bulk Food {i:D3}", energy: 100, hunger: 2m, ("STR", 1, 1m));
            SeedVariant(food, signature: string.Empty, energy: 100, hunger: 0.5m,
                feps: new[] { ("STR", 1, 1m) });
        }

        var result = await _service.RefreshCanonicalValuesAsync(TenantA);

        Assert.Equal(foodTotal, result.Foods);
        Assert.Equal(foodTotal, result.Updated);

        var stillWrong = await _db.Foods.IgnoreQueryFilters()
            .Where(f => f.TenantId == TenantA && f.Hunger == 2m)
            .CountAsync();
        Assert.Equal(0, stillWrong);
    }

    // ----- live precedence ---------------------------------------------------

    [Fact]
    public async Task Upload_UpdatesTheHeadlineValues_OfAnExistingFood()
    {
        SeedFood("Seaberries", energy: 50, hunger: 0.1m, ("INT", 1, 0.1m));

        await _service.IngestClientRecordsAsync(TenantA, "player-1", new List<FoodUploadRecordDto>
        {
            Upload("Seaberries", "gfx/invobjs/seed-sandthorn", energy: 50, hunger: 0.02m, genus: W162,
                feps: new[] { ("Intelligence +1", 0.1m) })
        });

        var updated = await LoadFoodAsync(TenantA, "Seaberries");
        Assert.Equal(0.02m, updated.Hunger);
        Assert.Equal(FoodValueSource.Upload, updated.ValueSource);
        Assert.Equal(W162, updated.ValueWorld);
    }

    [Fact]
    public async Task Upload_ReplacesASeededWorldSnapshot_EvenWithAHigherFepTotal()
    {
        // Without the Observed flag the lowest-total-wins rule would reject this upload
        // forever, because the seeded snapshot's total is lower.
        var food = SeedFood("Roast Chicken", energy: 300, hunger: 2m, ("CON", 1, 4m));
        SeedVariant(food, signature: string.Empty, energy: 300, hunger: 2m,
            feps: new[] { ("CON", 1, 4m) },
            worldValues: new[] { (W162, 300, 2m, new[] { ("CON", 1, 4m) }, false) });

        await _service.IngestClientRecordsAsync(TenantA, "player-1", new List<FoodUploadRecordDto>
        {
            Upload("Roast Chicken", "gfx/invobjs/roastchicken", energy: 300, hunger: 0.8m, genus: W162,
                feps: new[] { ("Constitution +1", 9m) })
        });

        var variant = Assert.Single(await LoadVariantsAsync(food.Id));
        var snapshot = Assert.Single(variant.WorldValues);
        Assert.True(snapshot.Observed);
        Assert.Equal(0.8m, snapshot.Hunger);
        Assert.Equal(9m, Assert.Single(snapshot.Feps).Value);

        var updated = await LoadFoodAsync(TenantA, "Roast Chicken");
        Assert.Equal(0.8m, updated.Hunger);
        Assert.Equal(9m, Assert.Single(updated.Feps).Value);
    }

    [Fact]
    public async Task Upload_FromAnOlderWorld_DoesNotDowngradeNewerValues()
    {
        SeedFood("Roast Chicken", energy: 300, hunger: 0.8m, ("CON", 1, 9m),
            valueSource: FoodValueSource.Upload, valueWorld: W162);

        await _service.IngestClientRecordsAsync(TenantA, "player-2", new List<FoodUploadRecordDto>
        {
            Upload("Roast Chicken", "gfx/invobjs/roastchicken", energy: 300, hunger: 2m, genus: W161,
                feps: new[] { ("Constitution +1", 4m) })
        });

        var food = await LoadFoodAsync(TenantA, "Roast Chicken");
        Assert.Equal(0.8m, food.Hunger);
        Assert.Equal(W162, food.ValueWorld);

        // The older world's observation is still recorded as its own snapshot.
        var variant = Assert.Single(await LoadVariantsAsync(food.Id));
        Assert.Equal(W161, Assert.Single(variant.WorldValues).Genus);
    }

    [Fact]
    public async Task Upload_WithAZeroHunger_KeepsTheStoredOne()
    {
        SeedFood("Cave Slime", energy: 30, hunger: 0.01m, ("PSY", 1, 0.2m),
            valueSource: FoodValueSource.Upload, valueWorld: W161);

        await _service.IngestClientRecordsAsync(TenantA, "player-1", new List<FoodUploadRecordDto>
        {
            Upload("Cave Slime", "gfx/invobjs/caveslime", energy: 30, hunger: 0m, genus: W162,
                feps: new[] { ("Psyche +1", 0.15m) })
        });

        var food = await LoadFoodAsync(TenantA, "Cave Slime");
        Assert.Equal(0.01m, food.Hunger);
        Assert.Equal(0.15m, Assert.Single(food.Feps).Value);   // FEPs still adopted
        Assert.Equal(W162, food.ValueWorld);
    }

    [Fact]
    public async Task Import_UsesTheGameDumpValues_NotTheWikiPage()
    {
        var dump = """
            [{"itemName":"Seaberries","resourceName":"gfx/invobjs/seed-sandthorn",
              "hunger":0.02,"energy":50,"feps":[{"name":"Intelligence +1","value":0.1}]}]
            """;
        var wiki = """
            {"Seaberries":{"title":"Seaberries","url":"https://ringofbrodgar.com/wiki/Seaberries",
              "metaobj":{"energy":"50","hunger":"0.1","int":"0.1","sat1":"Berries"}}}
            """;

        var result = await _service.ImportAsync(Json(dump), Json(wiki), TenantA);

        Assert.Equal(1, result.Imported);
        var food = await LoadFoodAsync(TenantA, "Seaberries");
        Assert.Equal(0.02m, food.Hunger);                       // the game's number, not 0.1
        Assert.Equal(FoodValueSource.Import, food.ValueSource);
        // Descriptive wiki fields still apply.
        Assert.Equal(new List<string> { "Berries" }, food.SatiationGroups);
        Assert.Equal("https://ringofbrodgar.com/wiki/Seaberries", food.WikiUrl);
    }

    [Fact]
    public async Task Import_FallsBackToTheWiki_WhenTheRecordCarriesNoValues()
    {
        var dump = """
            [{"itemName":"Seaberries","resourceName":"gfx/invobjs/seed-sandthorn",
              "hunger":0,"energy":0,"feps":[]}]
            """;
        var wiki = """
            {"Seaberries":{"title":"Seaberries","url":"https://ringofbrodgar.com/wiki/Seaberries",
              "metaobj":{"energy":"50","hunger":"0.1","int":"0.1","sat1":"Berries"}}}
            """;

        await _service.ImportAsync(Json(dump), Json(wiki), TenantA);

        var food = await LoadFoodAsync(TenantA, "Seaberries");
        Assert.Equal(0.1m, food.Hunger);
        Assert.Equal(50, food.Energy);
        Assert.Equal(FoodValueSource.Wiki, food.ValueSource);
    }

    [Fact]
    public async Task Export_CarriesProvenance_AndSnapshotImportRestoresIt()
    {
        var food = SeedFood("Seaberries", energy: 50, hunger: 0.02m, ("INT", 1, 0.1m),
            valueSource: FoodValueSource.Upload, valueWorld: W162);
        SeedVariant(food, signature: string.Empty, energy: 50, hunger: 0.02m,
            feps: new[] { ("INT", 1, 0.1m) },
            worldValues: new[] { (W162, 50, 0.02m, new[] { ("INT", 1, 0.1m) }, true) });

        var export = await _service.ExportAsync(TenantA);
        var exported = Assert.Single(export.Foods);
        Assert.Equal(FoodValueSource.Upload, exported.ValueSource);
        Assert.Equal(W162, exported.ValueWorld);
        Assert.True(Assert.Single(Assert.Single(exported.Variants).WorldValues).Observed);

        await _service.ImportAsync(Json(System.Text.Json.JsonSerializer.Serialize(export)), null, TenantA);

        var restored = await LoadFoodAsync(TenantA, "Seaberries");
        Assert.Equal(FoodValueSource.Upload, restored.ValueSource);
        Assert.Equal(W162, restored.ValueWorld);
        var variant = Assert.Single(await LoadVariantsAsync(restored.Id));
        Assert.True(Assert.Single(variant.WorldValues).Observed);
    }

    // ----- helpers -----------------------------------------------------------

    private static Stream Json(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static FoodUploadRecordDto Upload(
        string name,
        string resource,
        decimal energy,
        decimal hunger,
        string genus,
        (string Name, decimal Value)[] feps) => new()
    {
        ItemName = name,
        ResourceName = resource,
        Energy = energy,
        Hunger = hunger,
        Genus = genus,
        Feps = feps.Select(f => new FoodUploadFepDto { Name = f.Name, Value = f.Value }).ToList()
    };

    private void SeedFoodWithFeps(
        string name,
        int energy,
        decimal hunger,
        (string Attribute, int Tier, decimal Value)[] feps,
        string tenantId = TenantA,
        string valueSource = FoodValueSource.Wiki,
        string? valueWorld = null)
    {
        var food = SeedFood(name, energy, hunger, feps[0], tenantId, valueSource, valueWorld);
        foreach (var extra in feps.Skip(1))
        {
            food.Feps.Add(new FoodFep { Attribute = extra.Attribute, Tier = extra.Tier, Value = extra.Value });
        }

        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private FoodEntity SeedFood(
        string name,
        int energy,
        decimal hunger,
        (string Attribute, int Tier, decimal Value) feps,
        string tenantId = TenantA,
        string valueSource = FoodValueSource.Wiki,
        string? valueWorld = null)
    {
        var food = new FoodEntity
        {
            TenantId = tenantId,
            Name = name,
            ResourceName = "gfx/invobjs/" + name.ToLowerInvariant().Replace(" ", string.Empty).Replace("'", string.Empty),
            Energy = energy,
            Hunger = hunger,
            ImportedAt = DateTime.UtcNow,
            ValueSource = valueSource,
            ValueWorld = valueWorld,
            Feps = new List<FoodFep>
            {
                new() { Attribute = feps.Attribute, Tier = feps.Tier, Value = feps.Value }
            }
        };

        _db.Foods.Add(food);
        _db.SaveChanges();
        return food;
    }

    private void SeedVariant(
        FoodEntity food,
        string signature,
        int energy,
        decimal hunger,
        (string Attribute, int Tier, decimal Value)[] feps,
        string[]? contributors = null,
        (string Genus, int Energy, decimal Hunger, (string Attribute, int Tier, decimal Value)[] Feps, bool Observed)[]? worldValues = null,
        string tenantId = TenantA)
    {
        _db.FoodVariants.Add(new FoodVariantEntity
        {
            TenantId = tenantId,
            FoodId = food.Id,
            IngredientSignature = signature,
            Energy = energy,
            Hunger = hunger,
            TimesSeen = 1,
            Contributors = (contributors ?? Array.Empty<string>()).ToList(),
            Worlds = (worldValues ?? Array.Empty<(string, int, decimal, (string, int, decimal)[], bool)>())
                .Select(w => w.Genus)
                .ToList(),
            WorldValues = (worldValues ?? Array.Empty<(string, int, decimal, (string, int, decimal)[], bool)>())
                .Select(w => new FoodVariantWorldValue
                {
                    Genus = w.Genus,
                    Energy = w.Energy,
                    Hunger = w.Hunger,
                    Observed = w.Observed,
                    Feps = w.Feps
                        .Select(f => new FoodWorldFep { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
                        .ToList()
                })
                .ToList(),
            Feps = feps
                .Select(f => new FoodFep { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
                .ToList()
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private Task<FoodEntity> LoadFoodAsync(string tenantId, string name) =>
        _db.Foods.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(f => f.TenantId == tenantId && f.Name == name);

    private Task<List<FoodVariantEntity>> LoadVariantsAsync(int foodId) =>
        _db.FoodVariants.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => v.FoodId == foodId)
            .ToListAsync();

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
                // Best effort — a locked temp file is not worth failing a test run over.
            }
        }
    }
}
