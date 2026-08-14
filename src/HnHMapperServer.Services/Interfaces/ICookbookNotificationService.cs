using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Tenant-broadcast digest notifications for newly discovered cookbook foods.
/// </summary>
public interface ICookbookNotificationService
{
    /// <summary>
    /// Records newly created foods in the tenant's rolling digest notification:
    /// merges into the latest unread digest while it is fresh (anti-spam for the
    /// ~10s client flush cadence), otherwise creates a new one. Never throws —
    /// a failed notification must not fail the upload.
    /// </summary>
    Task NotifyNewFoodsAsync(
        string tenantId,
        string? contributorUserId,
        IReadOnlyList<FoodUploadNewFoodDto> newFoods,
        int newVariantCount,
        CancellationToken ct = default);
}
