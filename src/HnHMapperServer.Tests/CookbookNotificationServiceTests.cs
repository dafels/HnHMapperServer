using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// "New foods" digest coalescing tests.
///
/// Real SQLite, and the DbContext is constructed WITHOUT an IHttpContextAccessor:
/// the service must work where the ambient tenant query filter resolves to
/// TenantId == NULL (client-token requests, background contexts) — these tests
/// prove it never relies on that filter.
/// </summary>
public class CookbookNotificationServiceTests : IDisposable
{
    private const string TenantA = "cknotif-a";
    private const string TenantB = "cknotif-b";
    private const string W162 = "fd63ddee958da329"; // displays as "W16.2"
    private const string W161 = "b7c199a4557503a8"; // displays as "W16.1"

    private static readonly JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly UpdateNotificationService _updateSvc;
    private readonly CookbookNotificationService _service;

    public CookbookNotificationServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-cknotif-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        Seed();

        _updateSvc = new UpdateNotificationService();
        var notificationService = new NotificationService(
            _db,
            Mock.Of<ILogger<NotificationService>>(),
            Mock.Of<IServiceScopeFactory>(), // Discord fire-and-forget self-catches the mock NRE
            _updateSvc);

        _service = new CookbookNotificationService(
            _db,
            notificationService,
            _updateSvc,
            Mock.Of<ILogger<CookbookNotificationService>>());
    }

    [Fact]
    public async Task FirstBurst_CreatesDigest_WithActionDataAndCreatedEvent()
    {
        var created = _updateSvc.SubscribeToNotificationCreated();
        var updated = _updateSvc.SubscribeToNotificationUpdated();

        await _service.NotifyNewFoodsAsync(
            TenantA, "user-1",
            new[] { Food(41, "Autumn Steak", W162, ("STR", 2, 3.5m), ("AGI", 1, 1m)) },
            newVariantCount: 2);

        var row = Assert.Single(await AllRowsAsync(TenantA));
        Assert.Equal(CookbookNotificationService.NotificationType, row.Type);
        Assert.Equal(CookbookNotificationService.NavigateActionType, row.ActionType);
        Assert.Null(row.UserId);
        Assert.False(row.IsRead);
        Assert.Equal("Normal", row.Priority);
        Assert.NotNull(row.ExpiresAt);
        Assert.True(row.ExpiresAt > DateTime.UtcNow.AddDays(13.9));

        Assert.Equal("New food discovered (W16.2)", row.Title);
        Assert.Equal("Ranger discovered Autumn Steak — check it out in the cookbook!", row.Message);

        var data = Parse(row.ActionData);
        Assert.Equal(1, data.TotalCount);
        Assert.Equal(new List<int> { 41 }, data.FoodIds);
        Assert.Equal(new List<string> { "Autumn Steak" }, data.FoodNames);
        Assert.Equal(new List<string> { W162 }, data.Worlds);
        Assert.Equal(new List<string> { "Ranger" }, data.ContributorNames);
        Assert.Equal(2, data.VariantCount);

        var preview = Assert.Single(data.Previews);
        Assert.Equal(41, preview.Id);
        Assert.Equal("gfx/invobjs/autumnsteak", preview.ResourceName);
        Assert.Equal(200, preview.Energy);
        Assert.Equal(1.5m, preview.Hunger);
        Assert.Equal(2, preview.Feps.Count);

        // ActionData must serialize camelCase for the JS consumers.
        Assert.Contains("\"foodIds\"", row.ActionData);

        Assert.True(created.TryRead(out var createdEvent));
        Assert.Equal(row.Id, createdEvent.Id);
        Assert.Equal(TenantA, createdEvent.TenantId);
        Assert.False(created.TryRead(out _));
        Assert.False(updated.TryRead(out _));
    }

    [Fact]
    public async Task SecondBurstWithinWindow_MergesInPlace_AndBroadcastsUpdated()
    {
        var created = _updateSvc.SubscribeToNotificationCreated();
        var updated = _updateSvc.SubscribeToNotificationUpdated();

        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(1, "Stew", W162) }, 1);
        var firstCreatedAt = (await AllRowsAsync(TenantA)).Single().CreatedAt;
        var firstExpiresAt = (await AllRowsAsync(TenantA)).Single().ExpiresAt;

        await Task.Delay(20); // measurable CreatedAt bump
        await _service.NotifyNewFoodsAsync(
            TenantA, "user-1", new[] { Food(2, "Pie", W162), Food(3, "Loaf", W162) }, 4);

        var row = Assert.Single(await AllRowsAsync(TenantA));
        var data = Parse(row.ActionData);
        Assert.Equal(3, data.TotalCount);
        Assert.Equal(new List<int> { 1, 2, 3 }, data.FoodIds);
        Assert.Equal(5, data.VariantCount);
        Assert.Equal("3 new foods discovered (W16.2)", row.Title);
        Assert.StartsWith("Ranger discovered 3 new foods: Stew, Pie, Loaf", row.Message);
        Assert.True(row.CreatedAt > firstCreatedAt);
        Assert.True(row.ExpiresAt > firstExpiresAt);

        Assert.True(created.TryRead(out _));
        Assert.False(created.TryRead(out _)); // no second created
        Assert.True(updated.TryRead(out var updatedEvent));
        Assert.Equal(row.Id, updatedEvent.Id);
        Assert.False(updated.TryRead(out _));
    }

    [Fact]
    public async Task BurstAfterWindow_CreatesNewRow()
    {
        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(1, "Stew", W162) }, 0);

        var stale = (await AllRowsAsync(TenantA)).Single();
        stale.CreatedAt = DateTime.UtcNow - CookbookNotificationService.CoalesceWindow - TimeSpan.FromMinutes(1);
        await _db.SaveChangesAsync();

        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(2, "Pie", W162) }, 0);

        Assert.Equal(2, (await AllRowsAsync(TenantA)).Count);
    }

    [Fact]
    public async Task BurstAfterRead_CreatesNewRow_AndLeavesReadRowUntouched()
    {
        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(1, "Stew", W162) }, 0);

        var read = (await AllRowsAsync(TenantA)).Single();
        read.IsRead = true;
        await _db.SaveChangesAsync();

        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(2, "Pie", W162) }, 0);

        var rows = await AllRowsAsync(TenantA);
        Assert.Equal(2, rows.Count);
        var readRow = rows.Single(r => r.IsRead);
        Assert.Equal(1, Parse(readRow.ActionData).TotalCount);
    }

    [Fact]
    public async Task MultiContributor_MergesNames_AndPhrases()
    {
        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(1, "Stew", W162) }, 0);
        await _service.NotifyNewFoodsAsync(TenantA, "user-2", new[] { Food(2, "Pie", W162) }, 0);

        var row = Assert.Single(await AllRowsAsync(TenantA));
        Assert.Equal(new List<string> { "Ranger", "Bo" }, Parse(row.ActionData).ContributorNames);
        Assert.StartsWith("Ranger and Bo discovered 2 new foods", row.Message);

        // Phrasing table for the formatter itself.
        Assert.Equal("A player", CookbookNotificationService.FormatContributors(new List<string>()));
        Assert.Equal("Ranger", CookbookNotificationService.FormatContributors(new List<string> { "Ranger" }));
        Assert.Equal("Ranger and Bo", CookbookNotificationService.FormatContributors(new List<string> { "Ranger", "Bo" }));
        Assert.Equal("Ranger, Bo and 1 other", CookbookNotificationService.FormatContributors(new List<string> { "Ranger", "Bo", "Ash" }));
        Assert.Equal("Ranger, Bo and 2 others", CookbookNotificationService.FormatContributors(new List<string> { "Ranger", "Bo", "Ash", "Kim" }));
    }

    [Fact]
    public async Task BigBurst_CapsListsButCountsEverything()
    {
        var foods = Enumerable.Range(1, 60)
            .Select(i => Food(i, $"Food {i:000}", W162))
            .ToArray();

        await _service.NotifyNewFoodsAsync(TenantA, "user-1", foods, 0);

        var row = Assert.Single(await AllRowsAsync(TenantA));
        var data = Parse(row.ActionData);
        Assert.Equal(60, data.TotalCount);
        Assert.Equal(CookbookNotificationActionData.MaxFoodIds, data.FoodIds.Count);
        Assert.Equal(CookbookNotificationActionData.MaxFoodNames, data.FoodNames.Count);
        Assert.Equal(CookbookNotificationActionData.MaxPreviews, data.Previews.Count);
        Assert.Contains("(+57 more)", row.Message);
        Assert.Equal("60 new foods discovered (W16.2)", row.Title);
        Assert.True(row.Message.Length <= 1000);
        Assert.True(row.Title.Length <= 200);
    }

    [Fact]
    public async Task TenantIsolation_BurstsNeverCrossMerge()
    {
        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(1, "Stew", W162) }, 0);
        await _service.NotifyNewFoodsAsync(TenantB, "user-1", new[] { Food(2, "Pie", W162) }, 0);

        var rowA = Assert.Single(await AllRowsAsync(TenantA));
        var rowB = Assert.Single(await AllRowsAsync(TenantB));
        Assert.Equal(1, Parse(rowA.ActionData).TotalCount);
        Assert.Equal(1, Parse(rowB.ActionData).TotalCount);
    }

    [Fact]
    public async Task LegacyRow_WithNullActionData_IsNotMergedInto()
    {
        _db.Notifications.Add(new NotificationEntity
        {
            TenantId = TenantA,
            UserId = null,
            Type = CookbookNotificationService.NotificationType,
            Title = "New cookbook entries",
            Message = "legacy",
            ActionType = "NoAction",
            ActionData = null,
            IsRead = false,
            CreatedAt = DateTime.UtcNow,
            Priority = "Normal"
        });
        await _db.SaveChangesAsync();

        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(1, "Stew", W162) }, 0);

        var rows = await AllRowsAsync(TenantA);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Message == "legacy");
    }

    [Fact]
    public async Task WorldTag_OnlyWhileDigestIsSingleWorld()
    {
        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(1, "Stew", W162) }, 0);
        Assert.Equal("New food discovered (W16.2)", (await AllRowsAsync(TenantA)).Single().Title);

        await _service.NotifyNewFoodsAsync(TenantA, "user-1", new[] { Food(2, "Pie", W161) }, 0);

        var row = Assert.Single(await AllRowsAsync(TenantA));
        Assert.Equal("2 new foods discovered", row.Title); // mixed worlds → no tag
        Assert.Equal(new List<string> { W162, W161 }, Parse(row.ActionData).Worlds);
    }

    [Fact]
    public async Task UnknownOrMissingContributor_FallsBackToAPlayer()
    {
        await _service.NotifyNewFoodsAsync(TenantA, null, new[] { Food(1, "Stew", W162) }, 0);

        var row = Assert.Single(await AllRowsAsync(TenantA));
        Assert.Equal(new List<string> { "A player" }, Parse(row.ActionData).ContributorNames);
        Assert.StartsWith("A player discovered Stew", row.Message);

        // Unknown user id resolves the same way (merges into the existing digest).
        await _service.NotifyNewFoodsAsync(TenantA, "no-such-user", new[] { Food(2, "Pie", W162) }, 0);
        row = Assert.Single(await AllRowsAsync(TenantA));
        Assert.Equal(new List<string> { "A player" }, Parse(row.ActionData).ContributorNames);
    }

    [Fact]
    public async Task EmptyBurst_DoesNothing()
    {
        await _service.NotifyNewFoodsAsync(TenantA, "user-1", Array.Empty<FoodUploadNewFoodDto>(), 3);
        Assert.Empty(await AllRowsAsync(TenantA));
    }

    // ----- helpers -----

    private static FoodUploadNewFoodDto Food(
        int id, string name, string? world = null,
        params (string Attr, int Tier, decimal Value)[] feps) =>
        new()
        {
            FoodId = id,
            Name = name,
            ResourceName = $"gfx/invobjs/{name.ToLowerInvariant().Replace(" ", "")}",
            Energy = 200,
            Hunger = 1.5m,
            Worlds = world != null ? new List<string> { world } : new List<string>(),
            Feps = feps
                .Select(f => new FoodFepDto { Attribute = f.Attr, Tier = f.Tier, Value = f.Value })
                .ToList()
        };

    private async Task<List<NotificationEntity>> AllRowsAsync(string tenantId) =>
        await _db.Notifications
            .IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId)
            .OrderBy(n => n.Id)
            .ToListAsync();

    private static CookbookNotificationActionData Parse(string? actionData)
    {
        Assert.NotNull(actionData);
        var data = JsonSerializer.Deserialize<CookbookNotificationActionData>(actionData!, Camel);
        Assert.NotNull(data);
        return data!;
    }

    private void Seed()
    {
        foreach (var tenantId in new[] { TenantA, TenantB })
        {
            _db.Tenants.Add(new TenantEntity
            {
                Id = tenantId,
                Name = tenantId,
                StorageQuotaMB = 1024,
                CurrentStorageMB = 0,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            });
        }

        _db.Users.Add(new ApplicationUser { Id = "user-1", UserName = "Ranger" });
        _db.Users.Add(new ApplicationUser { Id = "user-2", UserName = "Bo" });
        _db.SaveChanges();
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
                // Temp file cleanup is best-effort.
            }
        }
    }
}
