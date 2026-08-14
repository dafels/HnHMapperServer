using System.Collections.Concurrent;
using System.Text.Json;
using HnHMapperServer.Core.Cookbook;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Builds the tenant-broadcast "new foods" digest. Game clients flush uploads every
/// ~10 seconds during a cooking session, so bursts coalesce into one rolling
/// notification per tenant: while the latest digest is unread and fresh it is merged
/// in place (broadcast as notificationUpdated — silent on the client) instead of
/// stacking a new row per flush.
/// </summary>
public class CookbookNotificationService : ICookbookNotificationService
{
    public const string NotificationType = "CookbookFoodAdded";
    public const string NavigateActionType = "NavigateToCookbook";

    /// <summary>Sliding merge window, measured from the digest's (bumped) CreatedAt.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMinutes(15);

    /// <summary>Digests expire this long after their last merge.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromDays(14);

    // ActionData is a nested JSON string — the outer HTTP/SSE serializer does not
    // re-case its contents, so camelCase must be applied here for the JS consumers.
    private static readonly JsonSerializerOptions ActionDataJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Serializes the lookup→merge→save window per tenant in this process (concurrent
    // client flushes would otherwise race into duplicate digests).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TenantLocks = new();

    private readonly ApplicationDbContext _db;
    private readonly INotificationService _notificationService;
    private readonly IUpdateNotificationService _updateNotificationService;
    private readonly ILogger<CookbookNotificationService> _logger;

    public CookbookNotificationService(
        ApplicationDbContext db,
        INotificationService notificationService,
        IUpdateNotificationService updateNotificationService,
        ILogger<CookbookNotificationService> logger)
    {
        _db = db;
        _notificationService = notificationService;
        _updateNotificationService = updateNotificationService;
        _logger = logger;
    }

    public async Task NotifyNewFoodsAsync(
        string tenantId,
        string? contributorUserId,
        IReadOnlyList<FoodUploadNewFoodDto> newFoods,
        int newVariantCount,
        CancellationToken ct = default)
    {
        if (newFoods.Count == 0)
        {
            return;
        }

        try
        {
            var contributorName = "A player";
            if (!string.IsNullOrEmpty(contributorUserId))
            {
                contributorName = await _db.Users.AsNoTracking()
                    .Where(u => u.Id == contributorUserId)
                    .Select(u => u.UserName)
                    .FirstOrDefaultAsync(ct) ?? contributorName;
            }

            var tenantLock = TenantLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
            await tenantLock.WaitAsync(ct);
            try
            {
                var now = DateTime.UtcNow;
                var windowStart = now - CoalesceWindow;

                // Explicit tenant predicate + IgnoreQueryFilters: this must work without an
                // HttpContext (the ambient tenant filter would silently match nothing).
                var existing = await _db.Notifications
                    .IgnoreQueryFilters()
                    .Where(n => n.TenantId == tenantId
                        && n.Type == NotificationType
                        && n.UserId == null
                        && !n.IsRead
                        && n.ActionData != null
                        && n.CreatedAt >= windowStart)
                    .OrderByDescending(n => n.CreatedAt)
                    .FirstOrDefaultAsync(ct);

                var existingData = TryParseActionData(existing?.ActionData);
                if (existing != null && existingData != null)
                {
                    MergeIntoDigest(existingData, newFoods, contributorName, newVariantCount, now);
                    existing.Title = BuildTitle(existingData);
                    existing.Message = BuildMessage(existingData);
                    existing.ActionData = JsonSerializer.Serialize(existingData, ActionDataJson);
                    existing.CreatedAt = now; // floats to the top of the bell and slides the merge window
                    existing.ExpiresAt = now + Expiry;
                    await _db.SaveChangesAsync(ct);

                    // Silent client upsert — no toast, badge count unchanged. Discord is
                    // deliberately not re-fired (it only fires inside CreateAsync).
                    _updateNotificationService.NotifyNotificationUpdated(
                        NotificationService.MapToEventDto(existing));
                }
                else
                {
                    var data = new CookbookNotificationActionData();
                    MergeIntoDigest(data, newFoods, contributorName, newVariantCount, now);

                    // CreateAsync broadcasts notificationCreated and fires the Discord webhook.
                    await _notificationService.CreateAsync(new CreateNotificationDto
                    {
                        TenantId = tenantId,
                        UserId = null, // broadcast to everyone in the tenant
                        Type = NotificationType,
                        Title = BuildTitle(data),
                        Message = BuildMessage(data),
                        ActionType = NavigateActionType,
                        ActionData = JsonSerializer.Serialize(data, ActionDataJson),
                        Priority = "Normal",
                        ExpiresAt = now + Expiry
                    });
                }
            }
            finally
            {
                tenantLock.Release();
            }
        }
        catch (Exception ex)
        {
            // A failed notification must never fail the upload.
            _logger.LogWarning(ex, "Failed to create cookbook notification for tenant {TenantId}", tenantId);
        }
    }

    /// <summary>
    /// Null when the payload is missing, unparseable (legacy pre-digest rows), or from a
    /// newer schema than this build understands — all of which fall back to a fresh digest.
    /// </summary>
    private static CookbookNotificationActionData? TryParseActionData(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var data = JsonSerializer.Deserialize<CookbookNotificationActionData>(json, ActionDataJson);
            return data != null && data.SchemaVersion <= CookbookNotificationActionData.CurrentSchemaVersion
                ? data
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void MergeIntoDigest(
        CookbookNotificationActionData data,
        IReadOnlyList<FoodUploadNewFoodDto> newFoods,
        string contributorName,
        int newVariantCount,
        DateTime now)
    {
        data.TotalCount += newFoods.Count;
        data.VariantCount += newVariantCount;
        data.LastUpdatedAt = now;

        foreach (var food in newFoods)
        {
            // Foods are new-once by construction; the Contains guards are defensive.
            if (data.FoodIds.Count < CookbookNotificationActionData.MaxFoodIds
                && !data.FoodIds.Contains(food.FoodId))
            {
                data.FoodIds.Add(food.FoodId);
            }

            if (data.FoodNames.Count < CookbookNotificationActionData.MaxFoodNames
                && !data.FoodNames.Contains(food.Name))
            {
                data.FoodNames.Add(food.Name);
            }

            if (data.Previews.Count < CookbookNotificationActionData.MaxPreviews
                && data.Previews.All(p => p.Id != food.FoodId))
            {
                data.Previews.Add(new CookbookNotificationFoodPreview
                {
                    Id = food.FoodId,
                    Name = food.Name,
                    ResourceName = food.ResourceName,
                    Energy = food.Energy,
                    Hunger = food.Hunger,
                    Feps = food.Feps.ToList()
                });
            }

            foreach (var world in food.Worlds)
            {
                if (data.Worlds.Count < CookbookNotificationActionData.MaxWorlds
                    && !data.Worlds.Contains(world))
                {
                    data.Worlds.Add(world);
                }
            }
        }

        if (data.ContributorNames.Count < CookbookNotificationActionData.MaxContributors
            && !data.ContributorNames.Contains(contributorName))
        {
            data.ContributorNames.Add(contributorName);
        }
    }

    /// <summary>
    /// "New food discovered (W16.2)" / "12 new foods discovered". The world tag only
    /// appears when every food in the digest came from the same world.
    /// </summary>
    public static string BuildTitle(CookbookNotificationActionData data)
    {
        var worldTag = data.Worlds.Count == 1
            ? $" ({GameWorlds.DisplayName(data.Worlds[0])})"
            : string.Empty;

        var title = data.TotalCount == 1
            ? $"New food discovered{worldTag}"
            : $"{data.TotalCount} new foods discovered{worldTag}";

        return Truncate(title, 200);
    }

    /// <summary>
    /// "Ranger discovered Autumn Steak — check it out in the cookbook!" /
    /// "Ranger and Bo discovered 12 new foods: A, B, C, … (+9 more)".
    /// </summary>
    public static string BuildMessage(CookbookNotificationActionData data)
    {
        var who = FormatContributors(data.ContributorNames);

        if (data.TotalCount == 1 && data.FoodNames.Count > 0)
        {
            return Truncate($"{who} discovered {data.FoodNames[0]} — check it out in the cookbook!", 1000);
        }

        var listed = string.Join(", ", data.FoodNames.Take(3));
        var more = data.TotalCount - Math.Min(3, data.FoodNames.Count);
        if (more > 0)
        {
            listed += $", … (+{more} more)";
        }

        return Truncate($"{who} discovered {data.TotalCount} new foods: {listed}", 1000);
    }

    public static string FormatContributors(List<string> names) => names.Count switch
    {
        0 => "A player",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        3 => $"{names[0]}, {names[1]} and 1 other",
        _ => $"{names[0]}, {names[1]} and {names.Count - 2} others"
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
