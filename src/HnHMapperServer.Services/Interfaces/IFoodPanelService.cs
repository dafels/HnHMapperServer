using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Per-user food panels (named collections) within the current tenant.
/// Reads return the user's own panels plus tenant-shared ones; every mutation
/// is owner-only. Validation/ownership failures throw:
/// ArgumentException → 400, UnauthorizedAccessException → 403, KeyNotFoundException → 404.
/// </summary>
public interface IFoodPanelService
{
    Task<List<FoodPanelDto>> GetPanelsAsync(string userId, CancellationToken ct = default);

    Task<FoodPanelDto> CreatePanelAsync(string userId, string name, CancellationToken ct = default);

    Task<FoodPanelDto> UpdatePanelAsync(int panelId, string userId, UpdateFoodPanelDto dto, CancellationToken ct = default);

    Task DeletePanelAsync(int panelId, string userId, CancellationToken ct = default);

    /// <summary>Adds a food — or one recipe variant when a signature is given — to a panel.</summary>
    Task<FoodPanelDto> AddItemAsync(int panelId, string userId, string foodName, string? ingredientSignature, CancellationToken ct = default);

    Task<FoodPanelDto> RemoveItemAsync(int panelId, string userId, int itemId, CancellationToken ct = default);

    Task<FoodPanelDto> ReorderAsync(int panelId, string userId, List<int> orderedItemIds, CancellationToken ct = default);

    /// <summary>Adds/removes a food or variant in the user's Favorites panel, creating the panel on first use.</summary>
    Task<FavoriteToggleResultDto> ToggleFavoriteAsync(string userId, string foodName, string? ingredientSignature, CancellationToken ct = default);
}
