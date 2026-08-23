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
    /// Tags every untagged food and recipe variation of one tenant with the given known
    /// world (genus hash), seeding per-world value snapshots from the canonical columns.
    /// Untagged variations inside already-tagged foods are included; existing world tags
    /// and snapshots are never modified, so the operation is idempotent. Throws
    /// ArgumentException for blank, sentinel, or unknown worlds.
    /// </summary>
    Task<CookbookWorldAssignResultDto> AssignUntaggedToWorldAsync(string tenantId, string world, CancellationToken ct = default);

    /// <summary>
    /// Returns all recorded recipe variations of one food (current tenant), best first.
    /// </summary>
    Task<List<FoodVariantDto>> GetVariationsAsync(int foodId, CancellationToken ct = default);

    /// <summary>
    /// Returns every recorded recipe variation of the current tenant's catalog in one
    /// list (FoodId set on each row) — the data source of the flat "all recipes"
    /// cookbook view. Empty when no tenant context is available.
    /// </summary>
    Task<List<FoodVariantDto>> GetAllVariationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Same rows and ordering as <see cref="GetAllVariationsAsync"/>, streamed so the
    /// bulk payload is never materialized in full on the server.
    /// </summary>
    IAsyncEnumerable<FoodVariantDto> StreamAllVariationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Foods whose base values OR any recorded recipe variation satisfy every threshold
    /// condition in <paramref name="expression"/> (FepFilterParser syntax) at the given
    /// quality, with per-food matching-variation counts. Empty when the expression
    /// contains no valid conditions.
    /// </summary>
    Task<List<FoodConditionMatchDto>> GetConditionMatchesAsync(string expression, int quality, string? world = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the wiki recipe line for every known craftable — including intermediates
    /// that are not eaten foods ("Unbaked Meatpie") — so recipes can be expanded
    /// recursively. Tenant-independent (built from the bundled wiki dump, cached).
    /// </summary>
    Task<List<RecipeIndexEntryDto>> GetRecipeIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// Full portable snapshot of one tenant's catalog — every food with every recorded
    /// recipe variation, world tags, per-world values, and contributor usernames — in
    /// the re-importable cookbook export format.
    /// </summary>
    Task<CookbookExportDto> ExportAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Wipe-and-replace import for one tenant. Accepts either the raw game dump or a
    /// cookbook export snapshot (auto-detected: the dump is a JSON array, an export is
    /// an object carrying the CookbookExportDto format marker).
    /// </summary>
    /// <param name="foodInfoJson">
    /// food-info2.json: array of per-eat records
    /// {itemName, resourceName, hunger, energy, feps:[{name, value}], ingredients:[{name, percentage}]},
    /// deduped here by volume-normalized name ("0.5 l of X" → "X") — or a cookbook
    /// export file, restored verbatim (wiki data is not consulted for those).
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
