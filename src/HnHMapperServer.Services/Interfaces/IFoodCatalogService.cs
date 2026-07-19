using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Read, import, and client-ingestion access to the tenant-scoped cookbook food catalog.
/// Reads are scoped to the current tenant context; imports and ingestion target an
/// explicit tenant. Catalogs are cached per tenant between writes.
/// </summary>
public interface IFoodCatalogService
{
    /// <summary>
    /// Returns the current tenant's food catalog (cached; invalidated on import/upload).
    /// Empty when no tenant context is available.
    /// </summary>
    Task<List<FoodDto>> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns food/variant counts and last import/upload time for the given tenant.
    /// </summary>
    Task<CookbookStatusDto> GetStatusAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Removes every food and recipe variation of one tenant's cookbook. Panels and
    /// favorites are untouched — their name-keyed items gray out until foods return.
    /// </summary>
    Task<CookbookClearResultDto> ClearAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns all recorded recipe variations of one food (current tenant), best first.
    /// </summary>
    Task<List<FoodVariantDto>> GetVariationsAsync(int foodId, CancellationToken ct = default);

    /// <summary>
    /// Returns the wiki recipe line for every known craftable — including intermediates
    /// that are not eaten foods ("Unbaked Meatpie") — so recipes can be expanded
    /// recursively. Tenant-independent (built from the bundled wiki dump, cached).
    /// </summary>
    Task<List<RecipeIndexEntryDto>> GetRecipeIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// Wipe-and-replace import for one tenant from the raw source files.
    /// </summary>
    /// <param name="foodInfoJson">
    /// food-info2.json: array of per-eat records
    /// {itemName, resourceName, hunger, energy, feps:[{name, value}], ingredients:[{name, percentage}]}.
    /// Deduped here by volume-normalized name ("0.5 l of X" → "X").
    /// </param>
    /// <param name="wikiJson">
    /// Optional wiki-food-data.json (object keyed by page title). When null, the wiki dump
    /// bundled with the server is used. Supplies canonical base values, categories, and
    /// satiation groups for matching foods.
    /// </param>
    /// <param name="tenantId">Target tenant.</param>
    Task<CookbookImportResultDto> ImportAsync(Stream foodInfoJson, Stream? wikiJson, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Additive ingestion of game-client food uploads (Hurricane/KamiClient/Amber format)
    /// into one tenant's catalog: unknown foods are created (wiki-enriched from the bundled
    /// dump, attributed to <paramref name="contributedByUserId"/>), unknown ingredient
    /// combinations become new variants, repeats bump TimesSeen.
    /// </summary>
    Task<FoodUploadResultDto> IngestClientRecordsAsync(
        string tenantId, string? contributedByUserId, List<FoodUploadRecordDto> records, CancellationToken ct = default);
}
