using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Per-user food panels within the current tenant. Panels are tenant-filtered by
/// the global query filter; per-user ownership is enforced here explicitly
/// (Timer/Notification pattern). Items reference foods by name so panels survive
/// catalog re-imports.
/// </summary>
public class FoodPanelService : IFoodPanelService
{
    private const string FavoritesName = "Favorites";
    private const int MaxPanelNameLength = 100;
    private const int MaxPanelsPerUser = 50;
    private const int MaxItemsPerPanel = 300;

    private readonly ApplicationDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantContext;
    private readonly ILogger<FoodPanelService> _logger;

    public FoodPanelService(
        ApplicationDbContext dbContext,
        ITenantContextAccessor tenantContext,
        ILogger<FoodPanelService> logger)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public async Task<List<FoodPanelDto>> GetPanelsAsync(string userId, CancellationToken ct = default)
    {
        // Query filter scopes to the tenant; user scoping is explicit.
        var panels = await _dbContext.FoodPanels
            .AsNoTracking()
            .Where(p => p.UserId == userId || p.IsShared)
            .ToListAsync(ct);

        if (panels.Count == 0)
        {
            return new List<FoodPanelDto>();
        }

        var panelIds = panels.Select(p => p.Id).ToList();
        var items = await _dbContext.FoodPanelItems
            .AsNoTracking()
            .Where(i => panelIds.Contains(i.PanelId))
            .ToListAsync(ct);
        var enriched = await EnrichItemsAsync(items, ct);
        var itemsByPanel = items
            .Select(i => (i.PanelId, Dto: enriched[i.Id]))
            .ToLookup(x => x.PanelId, x => x.Dto);

        var ownerIds = panels.Select(p => p.UserId).Distinct().ToList();
        var ownerNames = await _dbContext.Users
            .AsNoTracking()
            .Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName ?? "unknown", ct);

        // Own panels first (Favorites pinned); shared panels grouped per owner so
        // many people sharing e.g. their Favorites stay untangled.
        return panels
            .Select(p => MapToDto(p, itemsByPanel[p.Id], userId, ownerNames.GetValueOrDefault(p.UserId, "unknown")))
            .OrderByDescending(p => p.IsOwn)
            .ThenByDescending(p => p.IsFavorites)
            .ThenBy(p => p.IsOwn ? string.Empty : p.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolves each item's live display data (FEPs/hunger/energy) against the current
    /// catalog — whole foods from Foods, variant items from their FoodVariants row.
    /// Items whose food/variant is missing come back with Resolved = false.
    /// </summary>
    private async Task<Dictionary<int, FoodPanelItemDto>> EnrichItemsAsync(
        List<FoodPanelItemEntity> items, CancellationToken ct)
    {
        var result = new Dictionary<int, FoodPanelItemDto>(items.Count);
        if (items.Count == 0)
        {
            return result;
        }

        var names = items.Select(i => i.FoodName).Distinct().ToList();
        var foods = await _dbContext.Foods
            .AsNoTracking()
            .Where(f => names.Contains(f.Name))
            .ToListAsync(ct);
        var foodByName = foods.ToDictionary(f => f.Name, StringComparer.Ordinal);

        var variantItems = items.Where(i => i.IngredientSignature.Length > 0).ToList();
        var variantByKey = new Dictionary<(int FoodId, string Signature), FoodVariantEntity>();
        if (variantItems.Count > 0)
        {
            var foodIds = variantItems
                .Select(i => foodByName.GetValueOrDefault(i.FoodName)?.Id)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();
            var signatures = variantItems.Select(i => i.IngredientSignature).Distinct().ToList();

            if (foodIds.Count > 0)
            {
                var variants = await _dbContext.FoodVariants
                    .AsNoTracking()
                    .Where(v => foodIds.Contains(v.FoodId) && signatures.Contains(v.IngredientSignature))
                    .ToListAsync(ct);
                foreach (var variant in variants)
                {
                    variantByKey[(variant.FoodId, variant.IngredientSignature)] = variant;
                }
            }
        }

        foreach (var item in items)
        {
            var dto = new FoodPanelItemDto
            {
                ItemId = item.Id,
                FoodName = item.FoodName,
                IngredientSignature = item.IngredientSignature,
                Label = item.Label,
                Position = item.Position
            };

            var food = foodByName.GetValueOrDefault(item.FoodName);
            if (item.IngredientSignature.Length == 0)
            {
                if (food != null)
                {
                    dto.Resolved = true;
                    dto.Hunger = food.Hunger;
                    dto.Energy = food.Energy;
                    dto.Feps = food.Feps
                        .Select(f => new FoodFepDto { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
                        .ToList();
                }
            }
            else if (food != null && variantByKey.TryGetValue((food.Id, item.IngredientSignature), out var variant))
            {
                dto.Resolved = true;
                dto.Hunger = variant.Hunger;
                dto.Energy = variant.Energy;
                dto.Feps = variant.Feps
                    .Select(f => new FoodFepDto { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
                    .ToList();
            }

            result[item.Id] = dto;
        }

        return result;
    }

    public async Task<FoodPanelDto> CreatePanelAsync(string userId, string name, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        name = ValidateName(name);

        var ownCount = await _dbContext.FoodPanels.CountAsync(p => p.UserId == userId, ct);
        if (ownCount >= MaxPanelsPerUser)
        {
            throw new ArgumentException($"Panel limit reached ({MaxPanelsPerUser}).");
        }

        if (await _dbContext.FoodPanels.AnyAsync(p => p.UserId == userId && p.Name == name, ct))
        {
            throw new ArgumentException($"You already have a panel named '{name}'.");
        }

        var now = DateTime.UtcNow;
        var panel = new FoodPanelEntity
        {
            TenantId = tenantId,
            UserId = userId,
            Name = name,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.FoodPanels.Add(panel);
        await _dbContext.SaveChangesAsync(ct);

        return MapToDto(panel, Enumerable.Empty<FoodPanelItemDto>(), userId, string.Empty);
    }

    public async Task<FoodPanelDto> UpdatePanelAsync(int panelId, string userId, UpdateFoodPanelDto dto, CancellationToken ct = default)
    {
        var panel = await GetOwnedPanelAsync(panelId, userId, ct);

        if (dto.Name != null)
        {
            var name = ValidateName(dto.Name);
            if (name != panel.Name
                && await _dbContext.FoodPanels.AnyAsync(p => p.UserId == userId && p.Name == name && p.Id != panelId, ct))
            {
                throw new ArgumentException($"You already have a panel named '{name}'.");
            }

            panel.Name = name;
        }

        if (dto.IsShared.HasValue)
        {
            panel.IsShared = dto.IsShared.Value;
        }

        panel.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        return await LoadPanelDtoAsync(panel, userId, ct);
    }

    public async Task DeletePanelAsync(int panelId, string userId, CancellationToken ct = default)
    {
        var panel = await GetOwnedPanelAsync(panelId, userId, ct);
        _dbContext.FoodPanels.Remove(panel);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<FoodPanelDto> AddItemAsync(int panelId, string userId, string foodName, string? ingredientSignature, CancellationToken ct = default)
    {
        var panel = await GetOwnedPanelAsync(panelId, userId, ct);
        await AddItemInternalAsync(panel, foodName, ingredientSignature, ct);
        return await LoadPanelDtoAsync(panel, userId, ct);
    }

    public async Task<FoodPanelDto> RemoveItemAsync(int panelId, string userId, int itemId, CancellationToken ct = default)
    {
        var panel = await GetOwnedPanelAsync(panelId, userId, ct);

        await _dbContext.FoodPanelItems
            .Where(i => i.PanelId == panel.Id && i.Id == itemId)
            .ExecuteDeleteAsync(ct);

        panel.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        return await LoadPanelDtoAsync(panel, userId, ct);
    }

    public async Task<FoodPanelDto> ReorderAsync(int panelId, string userId, List<int> orderedItemIds, CancellationToken ct = default)
    {
        var panel = await GetOwnedPanelAsync(panelId, userId, ct);

        var items = await _dbContext.FoodPanelItems
            .Where(i => i.PanelId == panel.Id)
            .ToListAsync(ct);

        var order = orderedItemIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        foreach (var item in items)
        {
            if (order.TryGetValue(item.Id, out var position))
            {
                item.Position = position;
            }
            else
            {
                // Items missing from the requested order sink to the end, keeping relative order.
                item.Position = order.Count + item.Position;
            }
        }

        panel.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        return await LoadPanelDtoAsync(panel, userId, ct);
    }

    public async Task<FavoriteToggleResultDto> ToggleFavoriteAsync(string userId, string foodName, string? ingredientSignature, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var signature = (ingredientSignature ?? string.Empty).Trim();

        var favorites = await _dbContext.FoodPanels
            .FirstOrDefaultAsync(p => p.UserId == userId && p.IsFavorites, ct);

        if (favorites == null)
        {
            // Find a free name even if the user manually created panels named "Favorites"/"Favorites ★".
            var name = FavoritesName;
            var suffix = 1;
            while (await _dbContext.FoodPanels.AnyAsync(p => p.UserId == userId && p.Name == name, ct))
            {
                name = $"{FavoritesName} ★{(suffix > 1 ? suffix.ToString() : string.Empty)}";
                suffix++;
            }

            var now = DateTime.UtcNow;
            favorites = new FoodPanelEntity
            {
                TenantId = tenantId,
                UserId = userId,
                Name = name,
                IsFavorites = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.FoodPanels.Add(favorites);
            await _dbContext.SaveChangesAsync(ct);
        }

        var existing = await _dbContext.FoodPanelItems
            .FirstOrDefaultAsync(i => i.PanelId == favorites.Id
                                      && i.FoodName == foodName
                                      && i.IngredientSignature == signature, ct);

        bool isFavorite;
        if (existing != null)
        {
            _dbContext.FoodPanelItems.Remove(existing);
            isFavorite = false;
        }
        else
        {
            await AddItemInternalAsync(favorites, foodName, signature, ct, saveNow: false);
            isFavorite = true;
        }

        favorites.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        return new FavoriteToggleResultDto
        {
            FoodName = foodName,
            IngredientSignature = signature,
            IsFavorite = isFavorite,
            Panel = await LoadPanelDtoAsync(favorites, userId, ct)
        };
    }

    private async Task AddItemInternalAsync(FoodPanelEntity panel, string foodName, string? ingredientSignature, CancellationToken ct, bool saveNow = true)
    {
        foodName = foodName.Trim();
        var signature = (ingredientSignature ?? string.Empty).Trim();
        if (foodName.Length == 0)
        {
            throw new ArgumentException("Food name is required.");
        }

        var food = await _dbContext.Foods
            .Where(f => f.Name == foodName)
            .Select(f => new { f.Id })
            .FirstOrDefaultAsync(ct);
        if (food == null)
        {
            throw new ArgumentException($"Food '{foodName}' is not in the catalog.");
        }

        // Variant items get a human-readable ingredient label for the chip.
        string? label = null;
        if (signature.Length > 0)
        {
            var variant = await _dbContext.FoodVariants
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.FoodId == food.Id && v.IngredientSignature == signature, ct);
            if (variant == null)
            {
                throw new ArgumentException($"That recipe variant of '{foodName}' is not in the catalog.");
            }

            // Include proportions when they differ from 100 — variants can differ by percentage alone.
            label = string.Join(", ", variant.Ingredients.Select(i =>
                i.Percentage != 100 ? $"{i.Name} {i.Percentage:0.#}%" : i.Name));
            if (label.Length > 300)
            {
                label = label[..297] + "…";
            }
        }

        if (await _dbContext.FoodPanelItems.AnyAsync(
                i => i.PanelId == panel.Id && i.FoodName == foodName && i.IngredientSignature == signature, ct))
        {
            return; // already in the panel — treat as success
        }

        var itemCount = await _dbContext.FoodPanelItems.CountAsync(i => i.PanelId == panel.Id, ct);
        if (itemCount >= MaxItemsPerPanel)
        {
            throw new ArgumentException($"Panel is full ({MaxItemsPerPanel} foods).");
        }

        var maxPosition = itemCount == 0
            ? -1
            : await _dbContext.FoodPanelItems
                .Where(i => i.PanelId == panel.Id)
                .MaxAsync(i => i.Position, ct);

        _dbContext.FoodPanelItems.Add(new FoodPanelItemEntity
        {
            PanelId = panel.Id,
            FoodName = foodName,
            IngredientSignature = signature,
            Label = label,
            Position = maxPosition + 1,
            AddedAt = DateTime.UtcNow
        });

        panel.UpdatedAt = DateTime.UtcNow;
        if (saveNow)
        {
            await _dbContext.SaveChangesAsync(ct);
        }
    }

    private async Task<FoodPanelEntity> GetOwnedPanelAsync(int panelId, string userId, CancellationToken ct)
    {
        // Query filter guarantees the panel belongs to the current tenant.
        var panel = await _dbContext.FoodPanels.FirstOrDefaultAsync(p => p.Id == panelId, ct);
        if (panel == null)
        {
            throw new KeyNotFoundException("Panel not found.");
        }

        if (panel.UserId != userId)
        {
            throw new UnauthorizedAccessException("You can only modify your own panels.");
        }

        return panel;
    }

    private async Task<FoodPanelDto> LoadPanelDtoAsync(FoodPanelEntity panel, string userId, CancellationToken ct)
    {
        var items = await _dbContext.FoodPanelItems
            .AsNoTracking()
            .Where(i => i.PanelId == panel.Id)
            .ToListAsync(ct);
        var enriched = await EnrichItemsAsync(items, ct);

        return MapToDto(panel, items.Select(i => enriched[i.Id]), userId, string.Empty);
    }

    private string RequireTenant()
    {
        var tenantId = _tenantContext.GetCurrentTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new ArgumentException("No tenant context.");
        }

        return tenantId;
    }

    private static string ValidateName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("Panel name is required.");
        }

        if (name.Length > MaxPanelNameLength)
        {
            throw new ArgumentException($"Panel name is too long (max {MaxPanelNameLength}).");
        }

        return name;
    }

    private static FoodPanelDto MapToDto(
        FoodPanelEntity panel, IEnumerable<FoodPanelItemDto> items, string requestingUserId, string ownerName) => new()
    {
        Id = panel.Id,
        Name = panel.Name,
        IsShared = panel.IsShared,
        IsFavorites = panel.IsFavorites,
        IsOwn = panel.UserId == requestingUserId,
        OwnerName = ownerName,
        Items = items
            .OrderBy(i => i.Position)
            .ThenBy(i => i.ItemId)
            .ToList()
    };
}
