using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HnHMapperServer.Core.Cookbook;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Tenant-scoped cookbook food catalog: cached per-tenant reads, per-tenant
/// superadmin imports (wipe-and-replace), and additive game-client ingestion.
/// The import/ingestion join the raw game-data records with the wiki dump
/// (uploaded or bundled) server-side, so source files stay portable.
/// </summary>
public partial class FoodCatalogService : IFoodCatalogService
{
    private const string WikiCacheKey = "cookbook:wiki";
    private const string RecipeIndexCacheKey = "cookbook:recipeindex";
    private const int MaxErrors = 50;

    // SQLite does not enforce the model's HasMaxLength — client uploads must be
    // bounded here. Matches Foods.Name (200) / Foods.ResourceName (300).
    private const int MaxUploadNameLength = 200;
    private const int MaxUploadResourceLength = 300;
    private const int VariantBatchSize = 2000;
    /// <summary>Foods per page of the canonical value refresh (their variants load with them).</summary>
    private const int FoodBatchSize = 200;
    private const int MaxSignatureLength = 1000;

    private readonly ApplicationDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly ITenantContextAccessor _tenantContext;
    private readonly ILogger<FoodCatalogService> _logger;

    /// <summary>Strips volume prefixes like "0.5 l of " so variants collapse onto one food.</summary>
    [GeneratedRegex(@"^\s*\d+(?:\.\d+)?\s*(?:l|ml|g|kg|q)\s+of\s+", RegexOptions.IgnoreCase)]
    private static partial Regex VolumePrefixRegex();

    /// <summary>Parses game-dump FEP names like "Strength +2".</summary>
    [GeneratedRegex(@"^(\w+)\s*\+(\d)$")]
    private static partial Regex FepNameRegex();

    /// <summary>
    /// Mediawiki links like "[[requires::Raw Meat]]". For piped links the display text
    /// wins over the target ("[[requires::Category:Sharp Tools|Sharp Tool]]" → "Sharp Tool").
    /// </summary>
    [GeneratedRegex(@"\[\[(?:[a-zA-Z ]+::)?([^\]|]+?)(?:\|([^\]]*))?\]\]")]
    private static partial Regex WikiLinkRegex();

    /// <summary>
    /// Truncated trailing link ("..., [[requires::Wine" with no closing brackets).
    /// Only matched after a list separator (or at the start) so glued fragments stay rejected.
    /// </summary>
    [GeneratedRegex(@"(^|,\s*|:\s+|\bor\s+|\band\s+)\[\[(?:[a-zA-Z ]+::)?([^\]|{}]+)$")]
    private static partial Regex TruncatedTrailingLinkRegex();

    [GeneratedRegex(@"optional\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex OptionalMarkerRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Leftover wiki markup that marks a requirements value as unusable.</summary>
    private static readonly string[] WikiJunkNeedles =
    {
        "[[", "]]", "{{", "}}", "<", ">", "Category:", "File:", "#ask"
    };

    private static readonly HashSet<string> WikiEmptyValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "unknown", "?", "n/a", "-"
    };

    private static readonly Dictionary<string, string> FullNameToAbbrev = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Strength"] = "STR",
        ["Agility"] = "AGI",
        ["Intelligence"] = "INT",
        ["Constitution"] = "CON",
        ["Perception"] = "PER",
        ["Charisma"] = "CHA",
        ["Dexterity"] = "DEX",
        ["Will"] = "WILL",
        ["Psyche"] = "PSY"
    };

    /// <summary>Wiki metaobj keys ("str" carries the +1 value, "str2" the +2 value).</summary>
    private static readonly (string Key, string Abbrev)[] WikiStatKeys =
    {
        ("str", "STR"), ("agi", "AGI"), ("int", "INT"), ("con", "CON"), ("per", "PER"),
        ("cha", "CHA"), ("dex", "DEX"), ("wil", "WILL"), ("psy", "PSY")
    };

    /// <summary>Display order for FEP lines (canonical stat order, +1 before +2).</summary>
    private static readonly Dictionary<string, int> StatOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["STR"] = 0, ["AGI"] = 1, ["INT"] = 2, ["CON"] = 3, ["PER"] = 4,
        ["CHA"] = 5, ["DEX"] = 6, ["WILL"] = 7, ["PSY"] = 8
    };

    /// <summary>Wiki maintenance categories that are not food groups.</summary>
    private static readonly HashSet<string> CategoryBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "GenericTypePage", "Guide", "Tmp_xyobj(neg)"
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FoodCatalogService(
        ApplicationDbContext dbContext,
        IMemoryCache cache,
        ITenantContextAccessor tenantContext,
        ILogger<FoodCatalogService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    private static string CatalogCacheKey(string tenantId) => $"cookbook:catalog:{tenantId}";

    private static string ConditionStatsCacheKey(string tenantId) => $"cookbook:conditionstats:{tenantId}";

    /// <summary>Compact per-food targets (base + every variation) for variant-aware filtering.</summary>
    private sealed record FoodConditionStats(
        int FoodId,
        FepConditionTarget Base,
        Dictionary<string, FepConditionTarget> BaseByWorld,
        List<VariantConditionStats> Variants);

    /// <summary>One variation's targets: the all-worlds merge plus per-world snapshots.</summary>
    private sealed record VariantConditionStats(
        FepConditionTarget Target,
        List<string> Worlds,
        Dictionary<string, FepConditionTarget> ByWorld);

    public async Task<List<FoodDto>> GetCatalogAsync(CancellationToken ct = default)
    {
        var tenantId = _tenantContext.GetCurrentTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return new List<FoodDto>();
        }

        var catalog = await _cache.GetOrCreateAsync(CatalogCacheKey(tenantId), async _ =>
        {
            // Query filters scope both sets to the current tenant. Variants are fetched
            // whole: counts, per-world counts, and per-world representative values are
            // aggregated in one pass (this runs only on cache rebuild).
            var foods = await _dbContext.Foods
                .AsNoTracking()
                .ToListAsync(ct);

            var variantsByFood = (await _dbContext.FoodVariants
                    .AsNoTracking()
                    .ToListAsync(ct))
                .ToLookup(v => v.FoodId);

            var contributorIds = foods
                .Where(f => f.ContributedBy != null)
                .Select(f => f.ContributedBy!)
                .Distinct()
                .ToList();
            var contributorNames = contributorIds.Count == 0
                ? new Dictionary<string, string>()
                : await _dbContext.Users
                    .AsNoTracking()
                    .Where(u => contributorIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.UserName ?? "unknown", ct);

            return foods
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f =>
                {
                    var dto = MapToDto(f);
                    var own = variantsByFood[f.Id].ToList();
                    dto.VariantCount = own.Count;
                    dto.UntaggedVariantCount = own.Count(v => v.Worlds.Count == 0);
                    dto.WorldVariantCounts = own
                        .SelectMany(v => v.Worlds)
                        .GroupBy(g => g, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                    // Food-level per-world representative = the lowest-total world snapshot
                    // across the food's variations (same closest-to-base heuristic).
                    dto.WorldValues = own
                        .SelectMany(v => v.WorldValues)
                        .GroupBy(w => w.Genus, StringComparer.Ordinal)
                        .Select(g => g.OrderBy(w => w.Feps.Sum(x => x.Value)).First())
                        .Select(MapWorldValueDto)
                        .ToList();
                    dto.ContributedByName = f.ContributedBy != null
                        ? contributorNames.GetValueOrDefault(f.ContributedBy, "unknown")
                        : null;
                    return dto;
                })
                .ToList();
        });

        return catalog ?? new List<FoodDto>();
    }

    public async Task<CookbookStatusDto> GetStatusAsync(string tenantId, CancellationToken ct = default)
    {
        var query = _dbContext.Foods.IgnoreQueryFilters().Where(f => f.TenantId == tenantId);
        var count = await query.CountAsync(ct);
        DateTime? lastImportedAt = count > 0
            ? await query.MaxAsync(f => (DateTime?)f.ImportedAt, ct)
            : null;
        var variantCount = count > 0
            ? await _dbContext.FoodVariants.IgnoreQueryFilters()
                .Where(v => v.TenantId == tenantId)
                .CountAsync(ct)
            : 0;
        // Worlds is a primitive collection (JSON TEXT); EF translates Count via json_each.
        var untaggedFoods = count > 0
            ? await query.Where(f => f.Worlds.Count == 0).CountAsync(ct)
            : 0;
        var untaggedVariants = count > 0
            ? await _dbContext.FoodVariants.IgnoreQueryFilters()
                .Where(v => v.TenantId == tenantId && v.Worlds.Count == 0)
                .CountAsync(ct)
            : 0;

        return new CookbookStatusDto
        {
            FoodCount = count,
            VariantCount = variantCount,
            LastImportedAt = lastImportedAt,
            UntaggedFoodCount = untaggedFoods,
            UntaggedVariantCount = untaggedVariants
        };
    }

    public async Task<CookbookClearResultDto> ClearAsync(string tenantId, CancellationToken ct = default)
    {
        var result = new CookbookClearResultDto();

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(ct))
        {
            // ExecuteDelete bypasses query filters — scope explicitly to the target tenant.
            result.Variants = await _dbContext.FoodVariants.IgnoreQueryFilters()
                .Where(v => v.TenantId == tenantId)
                .ExecuteDeleteAsync(ct);
            result.Foods = await _dbContext.Foods.IgnoreQueryFilters()
                .Where(f => f.TenantId == tenantId)
                .ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        }

        _cache.Remove(CatalogCacheKey(tenantId));
        _cache.Remove(ConditionStatsCacheKey(tenantId));

        _logger.LogInformation(
            "Cookbook cleared for tenant {TenantId}: {Foods} foods, {Variants} variants removed",
            tenantId, result.Foods, result.Variants);

        return result;
    }

    public async Task<CookbookWorldAssignResultDto> AssignUntaggedToWorldAsync(
        string tenantId, string world, CancellationToken ct = default)
    {
        var genus = GameWorlds.Normalize(world);
        if (genus == null || string.Equals(genus, GameWorlds.UntaggedSentinel, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A target world is required.", nameof(world));
        }
        // Known worlds only: an admin-picked bulk tag is irreversible, unlike ingestion,
        // where an unknown genus is real data from the game client.
        if (GameWorlds.OrderOf(genus) < 0)
        {
            throw new ArgumentException($"'{genus}' is not a known world.", nameof(world));
        }

        var result = new CookbookWorldAssignResultDto { World = genus };
        var touchedFoodIds = new HashSet<int>();

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(ct))
        {
            // Pass 1: untagged variants. Keyset paging by Id — tagged rows leave the
            // Worlds.Count == 0 filter, so offset paging would skip rows — with a
            // tracker clear per batch (same shape as the import's FlushVariantBatchAsync).
            var lastId = 0;
            while (true)
            {
                var batch = await _dbContext.FoodVariants.IgnoreQueryFilters()
                    .Where(v => v.TenantId == tenantId && v.Id > lastId && v.Worlds.Count == 0)
                    .OrderBy(v => v.Id)
                    .Take(VariantBatchSize)
                    .ToListAsync(ct);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var variant in batch)
                {
                    variant.Worlds.Add(genus);
                    // Seed the world snapshot from the canonical columns so a later real
                    // upload from this world competes under the lowest-total-wins heuristic
                    // instead of becoming the snapshot unconditionally.
                    if (!variant.WorldValues.Any(w => w.Genus == genus))
                    {
                        variant.WorldValues.Add(BuildWorldValueFromCanonical(genus, variant));
                    }
                    touchedFoodIds.Add(variant.FoodId);
                    result.Variants++;
                }

                lastId = batch[^1].Id;
                await _dbContext.SaveChangesAsync(ct);
                _dbContext.ChangeTracker.Clear();
            }

            // Pass 2: foods. Untagged foods always gain the world (including variant-less
            // ones, so the Untagged master bucket empties); tagged foods whose untagged
            // variants were just transferred gain it too — mirrors ingestion's food-level
            // append and keeps master-row world filtering consistent with WorldVariantCounts.
            var foods = await _dbContext.Foods.IgnoreQueryFilters()
                .Where(f => f.TenantId == tenantId)
                .ToListAsync(ct);
            foreach (var food in foods)
            {
                if (food.Worlds.Count == 0
                    || (touchedFoodIds.Contains(food.Id) && !food.Worlds.Contains(genus)))
                {
                    food.Worlds.Add(genus);
                    result.Foods++;
                }
            }
            await _dbContext.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
        }

        if (result.Foods > 0 || result.Variants > 0)
        {
            _cache.Remove(CatalogCacheKey(tenantId));
            _cache.Remove(ConditionStatsCacheKey(tenantId));

            _logger.LogInformation(
                "Cookbook world assignment for tenant {TenantId}: {Foods} foods, {Variants} variants tagged {World}",
                tenantId, result.Foods, result.Variants, genus);
        }

        return result;
    }

    public async Task<CookbookValueRefreshResultDto> RefreshCanonicalValuesAsync(
        string tenantId, CancellationToken ct = default)
    {
        var result = new CookbookValueRefreshResultDto();

        // Keyset paging by food id with the batch's variants fetched in one query:
        // a tenant holds ~1k foods and ~50k variants, so neither side is loaded whole.
        var lastId = 0;
        while (true)
        {
            var foods = await _dbContext.Foods.IgnoreQueryFilters()
                .Where(f => f.TenantId == tenantId && f.Id > lastId)
                .OrderBy(f => f.Id)
                .Take(FoodBatchSize)
                .ToListAsync(ct);
            if (foods.Count == 0)
            {
                break;
            }

            lastId = foods[^1].Id;
            var foodIds = foods.Select(f => f.Id).ToList();
            var variantsByFood = (await _dbContext.FoodVariants.IgnoreQueryFilters()
                    .Where(v => v.TenantId == tenantId && foodIds.Contains(v.FoodId))
                    .ToListAsync(ct))
                .GroupBy(v => v.FoodId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var food in foods)
            {
                result.Foods++;
                var outcome = ApplyBestObservation(
                    food, variantsByFood.GetValueOrDefault(food.Id) ?? new List<FoodVariantEntity>());
                if (outcome.Changed)
                {
                    result.Updated++;
                }

                if (outcome.HungerFallback)
                {
                    result.HungerFallbacks++;
                }

                switch (FoodValueSource.Rank(food.ValueSource))
                {
                    case 2:
                        result.FromUploads++;
                        break;
                    case 1:
                        result.FromImports++;
                        break;
                    default:
                        result.LeftOnWiki++;
                        break;
                }
            }

            await _dbContext.SaveChangesAsync(ct);
            _dbContext.ChangeTracker.Clear();
        }

        if (result.Updated > 0)
        {
            _cache.Remove(CatalogCacheKey(tenantId));
            _cache.Remove(ConditionStatsCacheKey(tenantId));
        }

        _logger.LogInformation(
            "Cookbook value refresh for tenant {TenantId}: {Updated} of {Foods} foods updated "
            + "({FromUploads} from client uploads, {FromImports} from imports, {LeftOnWiki} still wiki-only)",
            tenantId, result.Updated, result.Foods, result.FromUploads, result.FromImports, result.LeftOnWiki);

        return result;
    }

    public async Task<List<FoodConditionMatchDto>> GetConditionMatchesAsync(string expression, int quality, string? world = null, CancellationToken ct = default)
    {
        var tenantId = _tenantContext.GetCurrentTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return new List<FoodConditionMatchDto>();
        }

        var (conditions, _) = FepFilterParser.Parse(expression);
        if (conditions.Count == 0)
        {
            return new List<FoodConditionMatchDto>();
        }

        var stats = await _cache.GetOrCreateAsync(ConditionStatsCacheKey(tenantId), async _ =>
        {
            // Query filters scope both sets to the current tenant.
            var foods = await _dbContext.Foods.AsNoTracking().ToListAsync(ct);
            var variants = await _dbContext.FoodVariants.AsNoTracking().ToListAsync(ct);
            var variantsByFood = variants.ToLookup(v => v.FoodId);

            return foods
                .Select(f =>
                {
                    var own = variantsByFood[f.Id].ToList();
                    return new FoodConditionStats(
                        f.Id,
                        FepConditionEvaluator.BuildTarget(f.Energy, f.Hunger,
                            f.Feps.Select(x => (x.Attribute, x.Tier, x.Value))),
                        own
                            .SelectMany(v => v.WorldValues)
                            .GroupBy(w => w.Genus, StringComparer.Ordinal)
                            .ToDictionary(
                                g => g.Key,
                                g =>
                                {
                                    var best = g.OrderBy(w => w.Feps.Sum(x => x.Value)).First();
                                    return FepConditionEvaluator.BuildTarget(best.Energy, best.Hunger,
                                        best.Feps.Select(x => (x.Attribute, x.Tier, x.Value)));
                                },
                                StringComparer.Ordinal),
                        own
                            .Select(v => new VariantConditionStats(
                                FepConditionEvaluator.BuildTarget(v.Energy, v.Hunger,
                                    v.Feps.Select(x => (x.Attribute, x.Tier, x.Value))),
                                v.Worlds.ToList(),
                                v.WorldValues.ToDictionary(
                                    w => w.Genus,
                                    w => FepConditionEvaluator.BuildTarget(w.Energy, w.Hunger,
                                        w.Feps.Select(x => (x.Attribute, x.Tier, x.Value))),
                                    StringComparer.Ordinal)))
                            .ToList());
                })
                .ToList();
        }) ?? new List<FoodConditionStats>();

        // Same quality math as the cookbook UI's QualityMultiplier.
        var scale = FoodQualityScale.For(quality);

        // World scoping mirrors the UI: a selected world evaluates world-effective values
        // (per-world snapshot, canonical fallback) and counts only that bucket's variants.
        var worldKey = string.IsNullOrWhiteSpace(world) ? null : world.Trim();
        var untaggedOnly = worldKey == GameWorlds.UntaggedSentinel;

        bool InBucket(List<string> worlds) =>
            worldKey == null || (untaggedOnly ? worlds.Count == 0 : worlds.Contains(worldKey));

        FepConditionTarget BaseTarget(FoodConditionStats s) =>
            worldKey != null && !untaggedOnly && s.BaseByWorld.TryGetValue(worldKey, out var t) ? t : s.Base;

        FepConditionTarget VariantTarget(VariantConditionStats v) =>
            worldKey != null && !untaggedOnly && v.ByWorld.TryGetValue(worldKey, out var t) ? t : v.Target;

        return stats
            .Select(s => new FoodConditionMatchDto
            {
                FoodId = s.FoodId,
                BaseMatches = FepConditionEvaluator.Matches(BaseTarget(s), conditions, scale),
                MatchingVariants = s.Variants.Count(v =>
                    InBucket(v.Worlds) && FepConditionEvaluator.Matches(VariantTarget(v), conditions, scale))
            })
            .Where(m => m.BaseMatches || m.MatchingVariants > 0)
            .ToList();
    }

    public async Task<List<FoodVariantDto>> GetVariationsAsync(int foodId, CancellationToken ct = default)
    {
        // Query filter scopes to the current tenant, so foreign food ids return empty.
        var variants = await _dbContext.FoodVariants
            .AsNoTracking()
            .Where(v => v.FoodId == foodId)
            .ToListAsync(ct);

        var contributorNames = await ResolveContributorNamesAsync(variants, ct);

        return variants
            .Select(v => MapVariantDto(v, contributorNames))
            .OrderByDescending(v => v.Feps.Sum(f => f.Value))
            .ToList();
    }

    public async Task<List<FoodVariantDto>> GetAllVariationsAsync(CancellationToken ct = default)
    {
        var result = new List<FoodVariantDto>();
        await foreach (var dto in StreamAllVariationsAsync(ct))
        {
            result.Add(dto);
        }
        return result;
    }

    /// <summary>
    /// Streams every recorded variation of the current tenant, ordered exactly like
    /// <see cref="GetAllVariationsAsync"/> (FoodId, then heaviest FEP total first).
    /// <para>
    /// The endpoint serializes this straight to the response, so the ~36 MB payload never
    /// exists as a whole on the server. The buffering version materialized the entities,
    /// then a DTO list, then the ordered copy, then the JSON buffer — measured at roughly
    /// +400 MB of process memory for one call. Here only one food's variations are held at
    /// a time (a few thousand rows for the most-cooked foods, a handful for the rest),
    /// because the secondary sort key is computed from JSON FEPs and cannot be ordered in
    /// SQL. Ordering by FoodId in the database is what makes that per-food grouping safe.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<FoodVariantDto> StreamAllVariationsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Same tenant-context guard as GetCatalogAsync: no tenant → empty, never leak.
        var tenantId = _tenantContext.GetCurrentTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            yield break;
        }

        // Contributor ids first, so names resolve before any row is written. Only the
        // id set is retained here, not the variants themselves.
        var contributorIds = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var contributors in _dbContext.FoodVariants
            .AsNoTracking()
            .Select(v => v.Contributors)
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            foreach (var id in contributors)
            {
                contributorIds.Add(id);
            }
        }
        var contributorNames = await ResolveContributorNamesAsync(contributorIds, ct);

        // Query filter scopes to the current tenant. Deliberately uncached: the flat
        // view's Web-side cache refetches per tenant only every few minutes, and holding
        // a second ~50k-DTO list per tenant in this process buys nothing for that rate.
        var pending = new List<FoodVariantDto>();
        var pendingFoodId = -1;

        await foreach (var v in _dbContext.FoodVariants
            .AsNoTracking()
            .OrderBy(v => v.FoodId)
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            if (v.FoodId != pendingFoodId && pending.Count > 0)
            {
                foreach (var dto in SortWithinFood(pending))
                {
                    yield return dto;
                }
                pending.Clear();
            }

            pendingFoodId = v.FoodId;
            pending.Add(MapVariantDto(v, contributorNames));
        }

        foreach (var dto in SortWithinFood(pending))
        {
            yield return dto;
        }
    }

    /// <summary>Heaviest FEP total first — the per-food ordering callers rely on.</summary>
    private static IEnumerable<FoodVariantDto> SortWithinFood(List<FoodVariantDto> variants) =>
        variants.OrderByDescending(v => v.Feps.Sum(f => f.Value));

    private Task<Dictionary<string, string>> ResolveContributorNamesAsync(
        List<FoodVariantEntity> variants, CancellationToken ct) =>
        ResolveContributorNamesAsync(
            variants.SelectMany(v => v.Contributors).ToHashSet(StringComparer.Ordinal), ct);

    private async Task<Dictionary<string, string>> ResolveContributorNamesAsync(
        IReadOnlyCollection<string> contributorIds, CancellationToken ct)
    {
        if (contributorIds.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var ids = contributorIds as List<string> ?? contributorIds.ToList();
        return await _dbContext.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName ?? "unknown", ct);
    }

    private static FoodVariantDto MapVariantDto(FoodVariantEntity v, Dictionary<string, string> contributorNames) =>
        new()
        {
            FoodId = v.FoodId,
            IngredientSignature = v.IngredientSignature,
            Energy = v.Energy,
            Hunger = v.Hunger,
            TimesSeen = v.TimesSeen,
            ContributorNames = v.Contributors
                .Select(id => contributorNames.GetValueOrDefault(id, "unknown"))
                .ToList(),
            Worlds = v.Worlds.ToList(),
            WorldValues = v.WorldValues.Select(MapWorldValueDto).ToList(),
            Feps = v.Feps
                .Select(f => new FoodFepDto { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
                .ToList(),
            Ingredients = v.Ingredients
                .Select(i => new FoodIngredientDto { Name = i.Name, Percentage = i.Percentage })
                .ToList()
        };

    public async Task<CookbookExportDto> ExportAsync(string tenantId, CancellationToken ct = default)
    {
        // Explicit tenant id (admin operation) — same pattern as GetStatusAsync/ClearAsync.
        var foods = await _dbContext.Foods.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId)
            .ToListAsync(ct);
        var variantsByFood = (await _dbContext.FoodVariants.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(v => v.TenantId == tenantId)
                .ToListAsync(ct))
            .ToLookup(v => v.FoodId);

        // Contributors travel as usernames, not internal user ids, so the file stays
        // portable; import re-resolves the names against local accounts. Ids whose
        // account is gone are dropped (they would only ever render as "unknown").
        var contributorIds = foods
            .Where(f => f.ContributedBy != null)
            .Select(f => f.ContributedBy!)
            .Concat(variantsByFood.SelectMany(g => g).SelectMany(v => v.Contributors))
            .Distinct()
            .ToList();
        var contributorNames = contributorIds.Count == 0
            ? new Dictionary<string, string>()
            : await _dbContext.Users
                .AsNoTracking()
                .Where(u => contributorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? string.Empty, ct);

        string? NameOf(string? id) =>
            id != null && contributorNames.TryGetValue(id, out var name) && name.Length > 0 ? name : null;

        var export = new CookbookExportDto
        {
            Format = CookbookExportDto.FormatMarker,
            Version = CookbookExportDto.CurrentVersion,
            ExportedAt = DateTime.UtcNow,
            Foods = foods
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new CookbookExportFoodDto
                {
                    Name = f.Name,
                    ResourceName = f.ResourceName,
                    Energy = f.Energy,
                    Hunger = f.Hunger,
                    ValueSource = f.ValueSource,
                    ValueWorld = f.ValueWorld,
                    WikiUrl = f.WikiUrl,
                    RecipeText = f.RecipeText,
                    CookingStation = f.CookingStation,
                    AddedAt = f.ImportedAt,
                    ContributedBy = NameOf(f.ContributedBy),
                    Categories = f.Categories.ToList(),
                    SatiationGroups = f.SatiationGroups.ToList(),
                    Worlds = f.Worlds.ToList(),
                    Feps = MapFepDtos(f.Feps),
                    Ingredients = MapIngredientDtos(f.Ingredients),
                    Variants = variantsByFood[f.Id]
                        .OrderBy(v => v.IngredientSignature, StringComparer.Ordinal)
                        .Select(v => new CookbookExportVariantDto
                        {
                            IngredientSignature = v.IngredientSignature,
                            Energy = v.Energy,
                            Hunger = v.Hunger,
                            TimesSeen = v.TimesSeen,
                            Contributors = v.Contributors
                                .Select(NameOf)
                                .Where(n => n != null)
                                .Select(n => n!)
                                .ToList(),
                            Worlds = v.Worlds.ToList(),
                            WorldValues = v.WorldValues.Select(MapWorldValueDto).ToList(),
                            Feps = MapFepDtos(v.Feps),
                            Ingredients = MapIngredientDtos(v.Ingredients)
                        })
                        .ToList()
                })
                .ToList()
        };

        export.FoodCount = export.Foods.Count;
        export.VariantCount = export.Foods.Sum(f => f.Variants.Count);

        _logger.LogInformation(
            "Cookbook export for tenant {TenantId}: {Foods} foods, {Variants} variants",
            tenantId, export.FoodCount, export.VariantCount);

        return export;
    }

    public async Task<CookbookImportResultDto> ImportAsync(
        Stream foodInfoJson, Stream? wikiJson, string tenantId, CancellationToken ct = default)
    {
        var result = new CookbookImportResultDto();

        List<SourceFoodRecord>? records;
        try
        {
            using var doc = await JsonDocument.ParseAsync(foodInfoJson, cancellationToken: ct);

            // A cookbook export snapshot (object with a format marker) restores verbatim;
            // the raw game dump (array of per-eat records) goes through the wiki join below.
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var snapshot = TryReadExportSnapshot(doc.RootElement, result);
                return snapshot == null ? result : await ImportSnapshotAsync(snapshot, tenantId, result, ct);
            }

            records = doc.RootElement.Deserialize<List<SourceFoodRecord>>(JsonOpts);
        }
        catch (JsonException ex)
        {
            result.Errors.Add($"Food data file is not valid JSON: {ex.Message}");
            return result;
        }

        if (records == null || records.Count == 0)
        {
            result.Errors.Add("Food data file contains no records.");
            return result;
        }

        Dictionary<string, WikiPage>? wiki;
        if (wikiJson != null)
        {
            try
            {
                wiki = await JsonSerializer.DeserializeAsync<Dictionary<string, WikiPage>>(wikiJson, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                result.Errors.Add($"Wiki data file is not valid JSON: {ex.Message}");
                return result;
            }
        }
        else
        {
            wiki = await GetBundledWikiAsync(ct);
        }

        // Collapse per-eat records (~49k, incl. volume/quality variants) onto one food per name.
        var groups = new Dictionary<string, List<SourceFoodRecord>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.ItemName))
            {
                continue;
            }

            var name = NormalizeName(record.ItemName);
            if (!groups.TryGetValue(name, out var list))
            {
                list = new List<SourceFoodRecord>();
                groups[name] = list;
            }

            list.Add(record);
        }

        var importedAt = DateTime.UtcNow;
        var entities = new List<FoodEntity>(groups.Count);
        var variantRecords = new Dictionary<FoodEntity, List<(SourceFoodRecord Record, int Seen, string Signature)>>();

        foreach (var (name, groupRecords) in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var baseRecord = PickBaseRecord(groupRecords);
            if (FoodResourceName.Normalize(baseRecord.ResourceName).Length == 0)
            {
                result.Skipped++;
                AddError(result, $"'{name}': no resource path in any record");
                continue;
            }

            var entity = BuildFoodEntity(
                name, baseRecord, wiki, tenantId, importedAt,
                FoodValueSource.Import, null, out var wikiMatched);
            if (wikiMatched)
            {
                result.WikiMatched++;
            }
            else
            {
                result.Fallback++;
            }

            entities.Add(entity);
            variantRecords[entity] = DedupeVariants(groupRecords);
        }

        if (entities.Count == 0)
        {
            result.Errors.Add("No importable foods found in the food data file.");
            return result;
        }

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(ct))
        {
            // ExecuteDelete bypasses query filters — scope explicitly to the target tenant.
            await _dbContext.FoodVariants.IgnoreQueryFilters()
                .Where(v => v.TenantId == tenantId)
                .ExecuteDeleteAsync(ct);
            await _dbContext.Foods.IgnoreQueryFilters()
                .Where(f => f.TenantId == tenantId)
                .ExecuteDeleteAsync(ct);

            _dbContext.Foods.AddRange(entities);
            await _dbContext.SaveChangesAsync(ct);

            // Variants reference the now-assigned food ids; insert in batches to keep
            // the change tracker small (~49k rows).
            var batch = new List<FoodVariantEntity>(VariantBatchSize);
            foreach (var (food, dedupedRecords) in variantRecords)
            {
                foreach (var (record, seen, signature) in dedupedRecords)
                {
                    batch.Add(new FoodVariantEntity
                    {
                        TenantId = tenantId,
                        FoodId = food.Id,
                        IngredientSignature = signature,
                        Energy = (int)Math.Round(record.Energy),
                        Hunger = record.Hunger,
                        TimesSeen = seen,
                        Feps = ParseDumpFeps(record.Feps, food.Name),
                        Ingredients = MapIngredients(record.Ingredients)
                    });
                    result.Variants++;

                    if (batch.Count >= VariantBatchSize)
                    {
                        await FlushVariantBatchAsync(batch, ct);
                    }
                }
            }

            await FlushVariantBatchAsync(batch, ct);
            await transaction.CommitAsync(ct);
        }

        _cache.Remove(CatalogCacheKey(tenantId));
        _cache.Remove(ConditionStatsCacheKey(tenantId));
        result.Imported = entities.Count;

        _logger.LogInformation(
            "Cookbook import for tenant {TenantId}: {Imported} foods, {Variants} variants ({WikiMatched} wiki-matched, {Fallback} fallback, {Skipped} skipped) from {Records} source records",
            tenantId, result.Imported, result.Variants, result.WikiMatched, result.Fallback, result.Skipped, records.Count);

        return result;
    }

    /// <summary>
    /// Reads an object-rooted foods file as a cookbook export snapshot. Returns null
    /// (with result errors) when the object is not one — e.g. wiki-food-data.json
    /// uploaded as the foods file — or a newer version than this server writes.
    /// </summary>
    private static CookbookExportDto? TryReadExportSnapshot(JsonElement root, CookbookImportResultDto result)
    {
        CookbookExportDto? snapshot = null;
        try
        {
            snapshot = root.Deserialize<CookbookExportDto>(JsonOpts);
        }
        catch (JsonException)
        {
            // Not export-shaped; falls through to the marker check below.
        }

        if (snapshot == null
            || !string.Equals(snapshot.Format, CookbookExportDto.FormatMarker, StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add(
                "Unrecognized food data file: expected the game data dump (a JSON array) or a cookbook export "
                + $"(\"format\": \"{CookbookExportDto.FormatMarker}\"). The wiki file alone cannot be imported.");
            return null;
        }

        if (snapshot.Version > CookbookExportDto.CurrentVersion)
        {
            result.Errors.Add(
                $"This cookbook export is version {snapshot.Version}, newer than this server supports "
                + $"(version {CookbookExportDto.CurrentVersion}). Update the server before importing it.");
            return null;
        }

        return snapshot;
    }

    /// <summary>
    /// Wipe-and-replace restore of a cookbook export snapshot: foods and variations land
    /// verbatim (world tags, per-world values, TimesSeen, discovery dates, signatures),
    /// contributor usernames are re-resolved to local accounts (unknown names drop).
    /// </summary>
    private async Task<CookbookImportResultDto> ImportSnapshotAsync(
        CookbookExportDto snapshot, string tenantId, CookbookImportResultDto result, CancellationToken ct)
    {
        if (snapshot.Foods.Count == 0)
        {
            result.Errors.Add("The cookbook export contains no foods.");
            return result;
        }

        var resolveUser = await BuildUsernameResolverAsync(snapshot, ct);
        var importedAt = DateTime.UtcNow;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var entities = new List<(FoodEntity Food, List<FoodVariantEntity> Variants)>();

        foreach (var food in snapshot.Foods)
        {
            var name = string.IsNullOrWhiteSpace(food.Name) ? string.Empty : NormalizeName(food.Name);
            var resource = FoodResourceName.Normalize(food.ResourceName);
            if (name.Length is 0 or > MaxUploadNameLength
                || resource.Length is 0 or > MaxUploadResourceLength)
            {
                result.Skipped++;
                AddError(result, $"'{food.Name}': missing or oversized name/resource");
                continue;
            }

            if (!seenNames.Add(name))
            {
                result.Skipped++;
                AddError(result, $"'{name}': duplicate food name in the export");
                continue;
            }

            var entity = new FoodEntity
            {
                TenantId = tenantId,
                Name = name,
                ResourceName = resource,
                Energy = food.Energy,
                Hunger = food.Hunger,
                // Exports written before value provenance existed restore as "Import",
                // so a later client upload outranks them (see FoodValueSource).
                ValueSource = FoodValueSource.Rank(food.ValueSource) > 0 || food.ValueSource == FoodValueSource.Wiki
                    ? food.ValueSource!
                    : FoodValueSource.Import,
                ValueWorld = GameWorlds.Normalize(food.ValueWorld),
                WikiUrl = TrimToNull(food.WikiUrl, 500),
                RecipeText = TrimToNull(food.RecipeText, 500),
                CookingStation = TrimToNull(food.CookingStation, 300),
                ImportedAt = food.AddedAt == default ? importedAt : food.AddedAt,
                ContributedBy = resolveUser(food.ContributedBy),
                Categories = CleanStrings(food.Categories),
                SatiationGroups = CleanStrings(food.SatiationGroups),
                Worlds = CleanWorlds(food.Worlds),
                Feps = MapSnapshotFeps(food.Feps),
                Ingredients = MapSnapshotIngredients(food.Ingredients)
            };

            // Wiki-matched foods are recognizable by their page URL surviving the roundtrip.
            if (entity.WikiUrl != null)
            {
                result.WikiMatched++;
            }
            else
            {
                result.Fallback++;
            }

            var variants = new List<FoodVariantEntity>();
            var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variant in food.Variants)
            {
                var ingredients = MapSnapshotIngredients(variant.Ingredients);
                // Exported signatures are kept verbatim (panels pin variants by them);
                // recompute only when a hand-edited file lacks or overflows one.
                var signature = variant.IngredientSignature is { Length: > 0 and <= MaxSignatureLength }
                    ? variant.IngredientSignature
                    : ComputeSignature(ingredients
                        .Select(i => new SourceIngredient { Name = i.Name, Percentage = i.Percentage })
                        .ToList());
                if (!seenSignatures.Add(signature))
                {
                    continue;
                }

                variants.Add(new FoodVariantEntity
                {
                    TenantId = tenantId,
                    IngredientSignature = signature,
                    Energy = variant.Energy,
                    Hunger = variant.Hunger,
                    TimesSeen = Math.Max(1, variant.TimesSeen),
                    Contributors = variant.Contributors
                        .Select(resolveUser)
                        .Where(id => id != null)
                        .Select(id => id!)
                        .Distinct()
                        .ToList(),
                    Worlds = CleanWorlds(variant.Worlds),
                    WorldValues = MapSnapshotWorldValues(variant.WorldValues),
                    Feps = MapSnapshotFeps(variant.Feps),
                    Ingredients = ingredients
                });
            }

            entities.Add((entity, variants));
        }

        if (entities.Count == 0)
        {
            result.Errors.Add("No importable foods found in the cookbook export.");
            return result;
        }

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(ct))
        {
            // ExecuteDelete bypasses query filters — scope explicitly to the target tenant.
            await _dbContext.FoodVariants.IgnoreQueryFilters()
                .Where(v => v.TenantId == tenantId)
                .ExecuteDeleteAsync(ct);
            await _dbContext.Foods.IgnoreQueryFilters()
                .Where(f => f.TenantId == tenantId)
                .ExecuteDeleteAsync(ct);

            _dbContext.Foods.AddRange(entities.Select(e => e.Food));
            await _dbContext.SaveChangesAsync(ct);

            // Variants reference the now-assigned food ids; insert in batches to keep
            // the change tracker small (~49k rows).
            var batch = new List<FoodVariantEntity>(VariantBatchSize);
            foreach (var (food, variants) in entities)
            {
                foreach (var variant in variants)
                {
                    variant.FoodId = food.Id;
                    batch.Add(variant);
                    result.Variants++;

                    if (batch.Count >= VariantBatchSize)
                    {
                        await FlushVariantBatchAsync(batch, ct);
                    }
                }
            }

            await FlushVariantBatchAsync(batch, ct);
            await transaction.CommitAsync(ct);
        }

        _cache.Remove(CatalogCacheKey(tenantId));
        _cache.Remove(ConditionStatsCacheKey(tenantId));
        result.Imported = entities.Count;

        _logger.LogInformation(
            "Cookbook export restored for tenant {TenantId}: {Imported} foods, {Variants} variants ({Skipped} skipped) from a snapshot of {ExportedAt:u}",
            tenantId, result.Imported, result.Variants, result.Skipped, snapshot.ExportedAt);

        return result;
    }

    /// <summary>Maps exported contributor usernames back to local account ids (unknown names drop).</summary>
    private async Task<Func<string?, string?>> BuildUsernameResolverAsync(CookbookExportDto snapshot, CancellationToken ct)
    {
        var names = snapshot.Foods
            .Select(f => f.ContributedBy)
            .Concat(snapshot.Foods.SelectMany(f => f.Variants).SelectMany(v => v.Contributors))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            return _ => null;
        }

        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.NormalizedUserName != null && names.Contains(u.NormalizedUserName))
            .Select(u => new { u.Id, u.NormalizedUserName })
            .ToListAsync(ct);

        var idByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var user in users)
        {
            idByName.TryAdd(user.NormalizedUserName!, user.Id);
        }

        return name => !string.IsNullOrWhiteSpace(name)
                       && idByName.TryGetValue(name.Trim().ToUpperInvariant(), out var id)
            ? id
            : null;
    }

    private static string? TrimToNull(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static List<string> CleanStrings(List<string>? values) =>
        (values ?? new List<string>())
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Genus hashes filtered through the same normalization as client uploads.</summary>
    private static List<string> CleanWorlds(List<string>? worlds) =>
        (worlds ?? new List<string>())
            .Select(GameWorlds.Normalize)
            .Where(w => w != null)
            .Select(w => w!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static List<FoodFep> MapSnapshotFeps(List<FoodFepDto>? feps) =>
        (feps ?? new List<FoodFepDto>())
            .Where(f => !string.IsNullOrWhiteSpace(f.Attribute)
                        && StatOrder.ContainsKey(f.Attribute.Trim())
                        && f.Tier is 1 or 2)
            .Select(f => new FoodFep { Attribute = f.Attribute.Trim().ToUpperInvariant(), Tier = f.Tier, Value = f.Value })
            .OrderBy(f => StatOrder[f.Attribute])
            .ThenBy(f => f.Tier)
            .ToList();

    private static List<FoodIngredient> MapSnapshotIngredients(List<FoodIngredientDto>? ingredients) =>
        (ingredients ?? new List<FoodIngredientDto>())
            .Where(i => !string.IsNullOrWhiteSpace(i.Name) && i.Name.Length <= MaxUploadNameLength)
            .Select(i => new FoodIngredient { Name = i.Name.Trim(), Percentage = i.Percentage })
            .ToList();

    private static List<FoodVariantWorldValue> MapSnapshotWorldValues(List<FoodWorldValueDto>? values)
    {
        var result = new List<FoodVariantWorldValue>();
        foreach (var value in values ?? new List<FoodWorldValueDto>())
        {
            var genus = GameWorlds.Normalize(value.Genus);
            if (genus == null || result.Any(w => w.Genus == genus))
            {
                continue;
            }

            result.Add(new FoodVariantWorldValue
            {
                Genus = genus,
                Energy = value.Energy,
                Hunger = value.Hunger,
                Observed = value.Observed,
                Feps = MapWorldFeps(MapSnapshotFeps(value.Feps))
            });
        }

        return result;
    }

    private static List<FoodFepDto> MapFepDtos(List<FoodFep> feps) =>
        feps.Select(f => new FoodFepDto { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value }).ToList();

    private static List<FoodIngredientDto> MapIngredientDtos(List<FoodIngredient> ingredients) =>
        ingredients.Select(i => new FoodIngredientDto { Name = i.Name, Percentage = i.Percentage }).ToList();

    public async Task<FoodUploadResultDto> IngestClientRecordsAsync(
        string tenantId, string? contributedByUserId, List<FoodUploadRecordDto> records, CancellationToken ct = default)
    {
        var result = new FoodUploadResultDto { Received = records.Count };
        var wiki = await GetBundledWikiAsync(ct);
        var now = DateTime.UtcNow;
        var changed = false;
        var newFoodEntities = new List<FoodEntity>();

        foreach (var upload in records)
        {
            if (string.IsNullOrWhiteSpace(upload.ItemName)
                || FoodResourceName.Normalize(upload.ResourceName).Length == 0)
            {
                result.Skipped++;
                continue;
            }

            // Real names top out around 60 chars — anything beyond the schema
            // lengths is garbage or abuse; reject rather than store it.
            if (upload.ItemName.Length > MaxUploadNameLength
                || upload.ResourceName.Length > MaxUploadResourceLength
                || upload.Ingredients?.Any(i => i.Name != null && i.Name.Length > MaxUploadNameLength) == true)
            {
                result.Skipped++;
                continue;
            }

            var source = ToSourceRecord(upload);
            var name = NormalizeName(source.ItemName!);
            var signature = ComputeSignature(source.Ingredients);
            var genus = GameWorlds.Normalize(upload.Genus);
            // Parsed once per record: the variant, its world snapshot and the food's
            // canonical values all need it (and ParseDumpFeps logs unknown FEP names).
            var recordFeps = ParseDumpFeps(source.Feps, name);
            var recordTotal = recordFeps.Sum(f => f.Value);

            var food = await _dbContext.Foods.IgnoreQueryFilters()
                .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.Name == name, ct);
            if (food == null)
            {
                food = BuildFoodEntity(name, source, wiki, tenantId, now, FoodValueSource.Upload, genus, out _);
                food.ContributedBy = contributedByUserId;
                _dbContext.Foods.Add(food);
                await _dbContext.SaveChangesAsync(ct);
                result.NewFoods++;
                result.NewFoodNames.Add(name);
                newFoodEntities.Add(food);
                changed = true;
            }

            if (genus != null && !food.Worlds.Contains(genus))
            {
                food.Worlds.Add(genus);
                changed = true;
            }

            var variant = await _dbContext.FoodVariants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(v => v.FoodId == food.Id && v.IngredientSignature == signature, ct);
            if (variant == null)
            {
                _dbContext.FoodVariants.Add(new FoodVariantEntity
                {
                    TenantId = tenantId,
                    FoodId = food.Id,
                    IngredientSignature = signature,
                    Energy = (int)Math.Round(source.Energy),
                    Hunger = source.Hunger,
                    TimesSeen = 1,
                    Contributors = contributedByUserId != null
                        ? new List<string> { contributedByUserId }
                        : new List<string>(),
                    Worlds = genus != null
                        ? new List<string> { genus }
                        : new List<string>(),
                    WorldValues = genus != null
                        ? new List<FoodVariantWorldValue> { BuildWorldValue(genus, source, recordFeps) }
                        : new List<FoodVariantWorldValue>(),
                    Feps = recordFeps,
                    Ingredients = MapIngredients(source.Ingredients)
                });
                result.NewVariants++;
                changed = true;
            }
            else
            {
                variant.TimesSeen++;
                if (contributedByUserId != null && !variant.Contributors.Contains(contributedByUserId))
                {
                    variant.Contributors.Add(contributedByUserId);
                }
                if (genus != null && !variant.Worlds.Contains(genus))
                {
                    variant.Worlds.Add(genus);
                }
                // Keep the lowest observed FEP total as the representative record
                // (closest to base quality — same heuristic as the import).
                var newTotal = recordTotal;
                var oldTotal = variant.Feps.Sum(f => f.Value);
                if (newTotal > 0 && (oldTotal == 0 || newTotal < oldTotal))
                {
                    variant.Feps = recordFeps;
                    variant.Hunger = source.Hunger;
                    variant.Energy = (int)Math.Round(source.Energy);
                }

                // Same heuristic per world: each world keeps its own representative
                // snapshot — except that a real observation always replaces a snapshot
                // the bulk world assignment seeded from stored columns, whatever the
                // totals say. Otherwise seeded data could reject live uploads forever.
                if (genus != null)
                {
                    var worldValue = variant.WorldValues.FirstOrDefault(w => w.Genus == genus);
                    if (worldValue == null)
                    {
                        variant.WorldValues.Add(BuildWorldValue(genus, source, recordFeps));
                    }
                    else
                    {
                        var oldWorldTotal = worldValue.Feps.Sum(f => f.Value);
                        if (!worldValue.Observed
                            || (newTotal > 0 && (oldWorldTotal == 0 || newTotal < oldWorldTotal)))
                        {
                            worldValue.Energy = (int)Math.Round(source.Energy);
                            worldValue.Hunger = source.Hunger;
                            worldValue.Observed = true;
                            worldValue.Feps = MapWorldFeps(recordFeps);
                        }
                    }
                }

                result.Duplicates++;
                changed = true;
            }

            // The client is the source of truth for the food's headline values too —
            // without this they stayed frozen at whatever created the row (usually the
            // wiki), so a world's real numbers never reached the catalog.
            if (TryPromoteCanonical(food, FoodValueSource.Upload, genus, source, recordFeps))
            {
                changed = true;
            }
        }

        try
        {
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Concurrent upload of the same new variant can trip the unique
            // (FoodId, IngredientSignature) index — the data is already there, so
            // treat it as duplicates rather than failing the batch.
            _logger.LogWarning(ex, "Cookbook ingestion conflict for tenant {TenantId} (concurrent upload)", tenantId);
            _dbContext.ChangeTracker.Clear();
        }

        if (changed)
        {
            _cache.Remove(CatalogCacheKey(tenantId));
            _cache.Remove(ConditionStatsCacheKey(tenantId));
        }

        // Snapshot for the notification digest. The food rows were committed at creation
        // (so their Ids are final even through the conflict-recovery ChangeTracker.Clear),
        // and Worlds is read after the genus append so world tags from this batch are included.
        result.NewFoodDetails = newFoodEntities
            .Select(f => new FoodUploadNewFoodDto
            {
                FoodId = f.Id,
                Name = f.Name,
                ResourceName = f.ResourceName,
                Energy = f.Energy,
                Hunger = f.Hunger,
                Worlds = f.Worlds.ToList(),
                Feps = f.Feps
                    .Select(fep => new FoodFepDto { Attribute = fep.Attribute, Tier = fep.Tier, Value = fep.Value })
                    .ToList()
            })
            .ToList();

        if (result.NewFoods > 0 || result.NewVariants > 0)
        {
            _logger.LogInformation(
                "Cookbook client upload for tenant {TenantId}: {Received} records → {NewFoods} new foods, {NewVariants} new variants, {Duplicates} duplicates",
                tenantId, result.Received, result.NewFoods, result.NewVariants, result.Duplicates);
        }

        return result;
    }

    public async Task<List<RecipeIndexEntryDto>> GetRecipeIndexAsync(CancellationToken ct = default)
    {
        var index = await _cache.GetOrCreateAsync(RecipeIndexCacheKey, async _ =>
        {
            var entries = new List<RecipeIndexEntryDto>();
            var wiki = await GetBundledWikiAsync(ct);
            if (wiki == null)
            {
                return entries;
            }

            foreach (var (name, page) in wiki)
            {
                if (page == null)
                {
                    continue;
                }

                var recipe = CleanWikiRequirement(page, "objectsreq", salvageTruncatedLink: true);
                if (recipe == null)
                {
                    continue;
                }

                entries.Add(new RecipeIndexEntryDto
                {
                    Name = name,
                    Recipe = recipe,
                    Station = CleanWikiRequirement(page, "producedby", salvageTruncatedLink: false)
                });
            }

            return entries;
        });

        return index ?? new List<RecipeIndexEntryDto>();
    }

    /// <summary>Loads and caches the wiki dump bundled with the server (groups/satiations source).</summary>
    private async Task<Dictionary<string, WikiPage>?> GetBundledWikiAsync(CancellationToken ct)
    {
        return await _cache.GetOrCreateAsync<Dictionary<string, WikiPage>?>(WikiCacheKey, async _ =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "wiki-food-data.json");
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<Dictionary<string, WikiPage>>(stream, JsonOpts, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load bundled wiki data from {Path}", path);
                return null;
            }
        });
    }

    private static string NormalizeName(string rawName)
    {
        var name = VolumePrefixRegex().Replace(rawName.Trim(), string.Empty);
        return name.Length > 0 ? name : rawName.Trim();
    }

    /// <summary>Base record ≈ lowest observed quality: smallest FEP total among records with FEPs.</summary>
    private static SourceFoodRecord PickBaseRecord(List<SourceFoodRecord> groupRecords)
    {
        var withFeps = groupRecords.Where(r => r.Feps is { Count: > 0 }).ToList();
        var candidates = withFeps.Count > 0 ? withFeps : groupRecords;
        return candidates
            .OrderBy(r => r.Feps?.Sum(f => f.Value) ?? 0m)
            .ThenBy(r => r.Ingredients?.Count ?? 0)
            .First();
    }

    /// <summary>
    /// Builds a food entity from a base record. The game record supplies the values —
    /// it is what the running world actually gives, while the wiki page is community
    /// data that is not revisited every world. The wiki supplies descriptive fields
    /// (recipe, station, url, groupings) whenever the name matches, and steps in for
    /// values only when the record carries none, or for a hunger the client rounded
    /// away (it sends 2 decimals, so a sub-0.005 food arrives as 0).
    /// </summary>
    /// <param name="valueSource">Provenance of the record: upload or import.</param>
    /// <param name="valueWorld">Genus the record was observed in, when known.</param>
    /// <param name="wikiMatched">True when a wiki page with usable values enriched this food.</param>
    private FoodEntity BuildFoodEntity(
        string name,
        SourceFoodRecord baseRecord,
        Dictionary<string, WikiPage>? wiki,
        string tenantId,
        DateTime importedAt,
        string valueSource,
        string? valueWorld,
        out bool wikiMatched)
    {
        var entity = new FoodEntity
        {
            TenantId = tenantId,
            Name = name,
            ResourceName = FoodResourceName.Normalize(baseRecord.ResourceName),
            ImportedAt = importedAt,
            Ingredients = MapIngredients(baseRecord.Ingredients)
        };

        wikiMatched = false;
        WikiPage? valuePage = null;
        decimal wikiHungerValue = 0m;
        decimal wikiEnergyValue = 0m;
        if (wiki != null && wiki.TryGetValue(name, out var page) && page != null)
        {
            ApplyWikiDescriptiveFields(entity, page);
            if (TryGetMetaDecimal(page, "hunger", out var wikiHunger)
                && TryGetMetaDecimal(page, "energy", out var wikiEnergy))
            {
                valuePage = page;
                wikiHungerValue = wikiHunger;
                wikiEnergyValue = wikiEnergy;
                wikiMatched = true;
            }
        }

        var recordFeps = ParseDumpFeps(baseRecord.Feps, name);
        var recordEnergy = (int)Math.Round(baseRecord.Energy);
        var recordHasValues = recordFeps.Count > 0 || recordEnergy > 0 || baseRecord.Hunger > 0;

        if (recordHasValues || valuePage == null)
        {
            entity.Energy = recordEnergy;
            entity.Hunger = baseRecord.Hunger > 0 || valuePage == null
                ? baseRecord.Hunger
                : wikiHungerValue;
            entity.Feps.AddRange(recordFeps);
            entity.ValueSource = valueSource;
            entity.ValueWorld = valueWorld;
        }
        else
        {
            ApplyWikiValues(entity, valuePage, wikiHungerValue, wikiEnergyValue);
            entity.ValueSource = FoodValueSource.Wiki;
            entity.ValueWorld = null;
        }

        entity.Feps = entity.Feps
            .OrderBy(f => StatOrder.TryGetValue(f.Attribute, out var order) ? order : int.MaxValue)
            .ThenBy(f => f.Tier)
            .ToList();

        return entity;
    }

    /// <summary>
    /// Collapses a food's records onto distinct ingredient combinations. Per combination the
    /// lowest-FEP-total record is kept (closest to base quality) with how often it was seen.
    /// </summary>
    private static List<(SourceFoodRecord Record, int Seen, string Signature)> DedupeVariants(
        List<SourceFoodRecord> groupRecords)
    {
        return groupRecords
            .GroupBy(r => ComputeSignature(r.Ingredients))
            .Select(g => (
                Record: g.OrderBy(r => r.Feps?.Sum(f => f.Value) ?? 0m).First(),
                Seen: g.Count(),
                Signature: g.Key))
            .ToList();
    }

    /// <summary>Canonical ingredient-combination key: sorted "name:roundedPct" joined with '|'.</summary>
    private static string ComputeSignature(List<SourceIngredient>? ingredients)
    {
        var signature = string.Join("|", (ingredients ?? new List<SourceIngredient>())
            .Select(i => $"{i.Name?.Trim()}:{Math.Round(i.Percentage)}")
            .OrderBy(s => s, StringComparer.Ordinal));

        if (signature.Length <= MaxSignatureLength)
        {
            return signature;
        }

        // Pathologically long ingredient lists: fall back to a stable hash.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static List<FoodIngredient> MapIngredients(List<SourceIngredient>? ingredients) =>
        (ingredients ?? new List<SourceIngredient>())
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new FoodIngredient { Name = i.Name!.Trim(), Percentage = i.Percentage })
            .ToList();

    /// <summary>One world's representative snapshot of an upload record.</summary>
    /// <summary>A world snapshot from a real game-client record — the source of truth.</summary>
    private static FoodVariantWorldValue BuildWorldValue(string genus, SourceFoodRecord source, List<FoodFep> feps) => new()
    {
        Genus = genus,
        Energy = (int)Math.Round(source.Energy),
        Hunger = source.Hunger,
        Observed = true,
        Feps = MapWorldFeps(feps)
    };

    private static List<FoodWorldFep> MapWorldFeps(List<FoodFep> feps) =>
        feps.Select(f => new FoodWorldFep { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value }).ToList();

    /// <summary>
    /// A world snapshot seeded from a variant's canonical (all-worlds merge) columns —
    /// used when bulk-assigning untagged data to a world, where no upload record exists.
    /// </summary>
    private static FoodVariantWorldValue BuildWorldValueFromCanonical(string genus, FoodVariantEntity variant) => new()
    {
        Genus = genus,
        Energy = variant.Energy,
        Hunger = variant.Hunger,
        // Asserted by an admin, not observed in that world: a later real upload from
        // this world replaces it outright instead of competing on FEP total.
        Observed = false,
        Feps = MapWorldFeps(variant.Feps)
    };

    /// <summary>One stored observation a food's canonical values could be taken from.</summary>
    private sealed record CanonicalCandidate(
        string Source,
        string? World,
        bool Observed,
        bool IsBaseVariant,
        int Energy,
        decimal Hunger,
        List<FoodFep> Feps)
    {
        public decimal Total { get; } = Feps.Sum(f => f.Value);
    }

    private readonly record struct CanonicalOutcome(bool Changed, bool HungerFallback);

    /// <summary>
    /// Release order used to rank observations: newer world first, an unknown genus
    /// after the known ones, and an untagged observation last of all.
    /// </summary>
    private static int WorldOrder(string? genus) =>
        genus == null ? -2 : GameWorlds.OrderOf(genus);

    /// <summary>
    /// Best observation first: a real client observation beats a seeded or imported one,
    /// then the newest world, then the plain item over a recipe variation, then the
    /// lowest FEP total (closest to base quality), then one that actually has a hunger.
    /// </summary>
    private static int CompareCandidates(CanonicalCandidate a, CanonicalCandidate b)
    {
        var byObserved = b.Observed.CompareTo(a.Observed);
        if (byObserved != 0)
        {
            return byObserved;
        }

        var byWorld = WorldOrder(b.World).CompareTo(WorldOrder(a.World));
        if (byWorld != 0)
        {
            return byWorld;
        }

        var byBase = b.IsBaseVariant.CompareTo(a.IsBaseVariant);
        if (byBase != 0)
        {
            return byBase;
        }

        var byTotal = a.Total.CompareTo(b.Total);
        return byTotal != 0 ? byTotal : (b.Hunger > 0).CompareTo(a.Hunger > 0);
    }

    /// <summary>
    /// Rewrites a food's canonical values from the best observation among its variations.
    /// Foods with no recorded variation keep what they have (wiki values, normally).
    /// </summary>
    private static CanonicalOutcome ApplyBestObservation(FoodEntity food, List<FoodVariantEntity> variants)
    {
        var candidates = new List<CanonicalCandidate>();
        foreach (var variant in variants)
        {
            var isBase = variant.IngredientSignature.Length == 0;
            foreach (var world in variant.WorldValues)
            {
                candidates.Add(new CanonicalCandidate(
                    world.Observed ? FoodValueSource.Upload : FoodValueSource.Import,
                    world.Genus,
                    world.Observed,
                    isBase,
                    world.Energy,
                    world.Hunger,
                    world.Feps.Select(f => new FoodFep { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
                        .ToList()));
            }

            // The variant's own columns: a client upload from before world tagging when
            // contributors were recorded, an imported dump record otherwise.
            var observed = variant.Contributors.Count > 0;
            candidates.Add(new CanonicalCandidate(
                observed ? FoodValueSource.Upload : FoodValueSource.Import,
                null,
                observed,
                isBase,
                variant.Energy,
                variant.Hunger,
                variant.Feps.Select(f => new FoodFep { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
                    .ToList()));
        }

        // An observation with neither FEPs nor hunger says nothing. Liquids produce
        // these: names are volume-normalized ("0.01 l of Cave Slime" -> "Cave Slime"),
        // so a tiny sip collapses onto the same food and its values round to zero.
        // Adopting one would replace real values with 0 across the board.
        candidates.RemoveAll(c => c.Total <= 0 && c.Hunger <= 0);
        if (candidates.Count == 0)
        {
            return new CanonicalOutcome(false, false);
        }

        candidates.Sort(CompareCandidates);
        var best = candidates[0];

        // Hunger is rounded to 2 decimals by the game client, so a food below 0.005
        // arrives as 0 — which would make FEP/hunger meaningless. Take the best
        // observation that has one instead, and keep the stored value if none does.
        var hunger = best.Hunger;
        var hungerFallback = false;
        if (hunger <= 0)
        {
            hungerFallback = true;
            var withHunger = candidates.FirstOrDefault(c => c.Hunger > 0);
            hunger = withHunger?.Hunger ?? food.Hunger;
        }

        var feps = CloneOrderedFeps(best.Feps);
        var changed = food.Energy != best.Energy
            || food.Hunger != hunger
            || !SameFeps(food.Feps, feps);

        food.Energy = best.Energy;
        food.Hunger = hunger;
        food.Feps = feps;
        food.ValueSource = best.Source;
        food.ValueWorld = best.World;

        return new CanonicalOutcome(changed, hungerFallback);
    }

    /// <summary>
    /// Promotes one incoming record to the food's canonical values when it outranks
    /// what is stored: a client upload beats an import beats the wiki, a newer world
    /// beats an older one, and within the same source and world the lowest FEP total
    /// wins (closest to base quality — the heuristic variations already use).
    /// </summary>
    private static bool TryPromoteCanonical(
        FoodEntity food, string source, string? genus, SourceFoodRecord record, List<FoodFep> feps)
    {
        var energy = (int)Math.Round(record.Energy);
        // Nothing usable, or an information-free record (a sip of a liquid, whose FEPs
        // and hunger both round to zero) — never let either overwrite stored values.
        if (feps.Sum(f => f.Value) <= 0 && record.Hunger <= 0)
        {
            return false;
        }

        var newRank = FoodValueSource.Rank(source);
        var currentRank = FoodValueSource.Rank(food.ValueSource);
        if (newRank < currentRank)
        {
            return false;
        }

        if (newRank == currentRank)
        {
            var newOrder = WorldOrder(genus);
            var currentOrder = WorldOrder(food.ValueWorld);
            if (newOrder < currentOrder)
            {
                return false;
            }

            if (newOrder == currentOrder)
            {
                var newTotal = feps.Sum(f => f.Value);
                var currentTotal = food.Feps.Sum(f => f.Value);
                if (newTotal <= 0 || (currentTotal > 0 && newTotal >= currentTotal))
                {
                    return false;
                }
            }
        }

        var hunger = record.Hunger > 0 ? record.Hunger : food.Hunger;
        var ordered = CloneOrderedFeps(feps);
        var changed = food.Energy != energy
            || food.Hunger != hunger
            || !SameFeps(food.Feps, ordered);

        food.Energy = energy;
        food.Hunger = hunger;
        food.Feps = ordered;
        food.ValueSource = source;
        food.ValueWorld = genus;

        return changed;
    }

    /// <summary>
    /// Copies FEP lines into fresh instances in display order. The copy matters: the
    /// same list is also assigned to a variant, and two EF owners must never share
    /// owned-entity instances.
    /// </summary>
    private static List<FoodFep> CloneOrderedFeps(IEnumerable<FoodFep> feps) =>
        feps.Select(f => new FoodFep { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
            .OrderBy(f => StatOrder.TryGetValue(f.Attribute, out var order) ? order : int.MaxValue)
            .ThenBy(f => f.Tier)
            .ToList();

    private static bool SameFeps(List<FoodFep> a, List<FoodFep> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Attribute, b[i].Attribute, StringComparison.OrdinalIgnoreCase)
                || a[i].Tier != b[i].Tier
                || a[i].Value != b[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    private static FoodWorldValueDto MapWorldValueDto(FoodVariantWorldValue value) => new()
    {
        Genus = value.Genus,
        Energy = value.Energy,
        Hunger = value.Hunger,
        Observed = value.Observed,
        Feps = value.Feps
            .Select(f => new FoodFepDto { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
            .ToList()
    };

    private static SourceFoodRecord ToSourceRecord(FoodUploadRecordDto upload) => new()
    {
        ItemName = upload.ItemName,
        ResourceName = upload.ResourceName,
        Energy = upload.Energy,
        Hunger = upload.Hunger,
        Feps = upload.Feps?
            .Select(f => new SourceFep { Name = f.Name, Value = f.Value })
            .ToList(),
        Ingredients = upload.Ingredients?
            .Select(i => new SourceIngredient { Name = i.Name, Percentage = i.Percentage })
            .ToList()
    };

    private async Task FlushVariantBatchAsync(List<FoodVariantEntity> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        _dbContext.FoodVariants.AddRange(batch);
        await _dbContext.SaveChangesAsync(ct);
        _dbContext.ChangeTracker.Clear();
        batch.Clear();
    }

    /// <summary>
    /// Wiki fields that describe the food — canonical recipe, cooking station, page URL,
    /// categories, and satiation groups. Applied on any name match, even when the page's
    /// hunger/energy are unusable and the numbers fall back to the game-data record.
    /// </summary>
    private static void ApplyWikiDescriptiveFields(FoodEntity entity, WikiPage page)
    {
        entity.WikiUrl = string.IsNullOrWhiteSpace(page.Url) ? null : page.Url.Trim();
        entity.RecipeText = CleanWikiRequirement(page, "objectsreq", salvageTruncatedLink: true);
        entity.CookingStation = CleanWikiRequirement(page, "producedby", salvageTruncatedLink: false);

        entity.Categories = (page.Categories ?? new List<string>())
            .Select(c => c?.Trim())
            .Where(c => !string.IsNullOrEmpty(c) && !CategoryBlocklist.Contains(c!))
            .Select(c => Capitalize(c!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        entity.SatiationGroups = new[] { "sat1", "sat2", "sat3" }
            .Select(key => TryGetMetaString(page, key, out var value) ? value : null)
            .Where(s => !string.IsNullOrEmpty(s)
                        && !s!.Contains('?')
                        && !s.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            .Select(s => Capitalize(s!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Turns a wiki requirements field (objectsreq/producedby) into a plain display line:
    /// "[[requires::Raw Meat]], [[requires::Edible Mushroom]] x2" → "Raw Meat, Edible Mushroom x2".
    /// Returns null when the value is missing, empty, or unsalvageable wiki markup —
    /// generic-ingredient pages carry "{{#ask:...}}" queries there, not recipes.
    /// </summary>
    private static string? CleanWikiRequirement(WikiPage page, string key, bool salvageTruncatedLink)
    {
        if (!TryGetMetaString(page, key, out var raw))
        {
            return null;
        }

        var text = OptionalMarkerRegex().Replace(raw, "optional: ");
        text = WikiLinkRegex().Replace(text, m =>
        {
            var display = m.Groups[2].Success ? m.Groups[2].Value.Trim() : string.Empty;
            return display.Length > 0 ? display : m.Groups[1].Value.Trim();
        });
        if (salvageTruncatedLink)
        {
            text = TruncatedTrailingLinkRegex().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value.Trim());
        }

        text = text.Replace("'''", string.Empty).Replace("''", string.Empty);
        text = WhitespaceRegex().Replace(text, " ").Trim().Trim(',').Trim();

        if (text.Length == 0
            || text.Length > 500
            || WikiEmptyValues.Contains(text)
            || WikiJunkNeedles.Any(needle => text.Contains(needle, StringComparison.Ordinal)))
        {
            return null;
        }

        return text;
    }

    /// <summary>
    /// Base-q10 values (hunger, energy, FEPs) from the wiki page. Last resort — used only
    /// for a food whose game record carries no values at all (see BuildFoodEntity).
    /// </summary>
    private static void ApplyWikiValues(FoodEntity entity, WikiPage page, decimal hunger, decimal energy)
    {
        entity.Hunger = hunger;
        entity.Energy = (int)Math.Round(energy);

        foreach (var (key, abbrev) in WikiStatKeys)
        {
            if (TryGetMetaDecimal(page, key, out var tier1) && tier1 > 0)
            {
                entity.Feps.Add(new FoodFep { Attribute = abbrev, Tier = 1, Value = tier1 });
            }

            if (TryGetMetaDecimal(page, key + "2", out var tier2) && tier2 > 0)
            {
                entity.Feps.Add(new FoodFep { Attribute = abbrev, Tier = 2, Value = tier2 });
            }
        }
    }

    /// <summary>Parses game-dump FEP names ("Strength +2") into (Attribute, Tier, Value), stat-ordered.</summary>
    private List<FoodFep> ParseDumpFeps(List<SourceFep>? feps, string foodName)
    {
        var parsed = new List<FoodFep>();

        foreach (var fep in feps ?? new List<SourceFep>())
        {
            var match = FepNameRegex().Match(fep.Name?.Trim() ?? string.Empty);
            if (match.Success
                && FullNameToAbbrev.TryGetValue(match.Groups[1].Value, out var abbrev)
                && int.TryParse(match.Groups[2].Value, out var tier))
            {
                parsed.Add(new FoodFep { Attribute = abbrev, Tier = tier, Value = fep.Value });
            }
            else
            {
                _logger.LogWarning("Unrecognized FEP name '{FepName}' on food '{Food}'", fep.Name, foodName);
            }
        }

        return parsed
            .OrderBy(f => StatOrder.TryGetValue(f.Attribute, out var order) ? order : int.MaxValue)
            .ThenBy(f => f.Tier)
            .ToList();
    }

    private static void AddError(CookbookImportResultDto result, string message)
    {
        if (result.Errors.Count < MaxErrors)
        {
            result.Errors.Add(message);
        }
        else if (result.Errors.Count == MaxErrors)
        {
            result.Errors.Add("(further errors omitted)");
        }
    }

    private static string Capitalize(string value) =>
        value.Length == 0 || char.IsUpper(value[0]) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static bool TryGetMetaString(WikiPage page, string key, out string value)
    {
        value = string.Empty;
        if (page.Metaobj == null || !page.Metaobj.TryGetValue(key, out var element))
        {
            return false;
        }

        var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
        return true;
    }

    private static bool TryGetMetaDecimal(WikiPage page, string key, out decimal value)
    {
        value = 0m;
        return TryGetMetaString(page, key, out var raw)
               && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static FoodDto MapToDto(FoodEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        ResourceName = entity.ResourceName,
        Energy = entity.Energy,
        Hunger = entity.Hunger,
        WikiUrl = entity.WikiUrl,
        RecipeText = entity.RecipeText,
        CookingStation = entity.CookingStation,
        ImportedAt = entity.ImportedAt,
        Categories = entity.Categories.ToList(),
        SatiationGroups = entity.SatiationGroups.ToList(),
        Worlds = entity.Worlds.ToList(),
        Feps = entity.Feps
            .Select(f => new FoodFepDto { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
            .ToList(),
        Ingredients = entity.Ingredients
            .Select(i => new FoodIngredientDto { Name = i.Name, Percentage = i.Percentage })
            .ToList()
    };

    // Source-file shapes (kept in the source vocabulary; translation to entities happens here).

    private sealed class SourceFoodRecord
    {
        public string? ItemName { get; set; }
        public string? ResourceName { get; set; }
        public decimal Hunger { get; set; }
        public decimal Energy { get; set; }
        public List<SourceFep>? Feps { get; set; }
        public List<SourceIngredient>? Ingredients { get; set; }
    }

    private sealed class SourceFep
    {
        public string? Name { get; set; }
        public decimal Value { get; set; }
    }

    private sealed class SourceIngredient
    {
        public string? Name { get; set; }
        public decimal Percentage { get; set; }
    }

    private sealed class WikiPage
    {
        public string? Title { get; set; }
        public string? Url { get; set; }
        public List<string>? Categories { get; set; }
        public Dictionary<string, JsonElement>? Metaobj { get; set; }
    }
}
