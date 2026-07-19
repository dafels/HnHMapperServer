using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
public class FoodCatalogService : IFoodCatalogService
{
    private const string WikiCacheKey = "cookbook:wiki";
    private const string RecipeIndexCacheKey = "cookbook:recipeindex";
    private const int MaxErrors = 50;
    private const int VariantBatchSize = 2000;
    private const int MaxSignatureLength = 1000;

    private readonly ApplicationDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly ITenantContextAccessor _tenantContext;
    private readonly ILogger<FoodCatalogService> _logger;

    /// <summary>Strips volume prefixes like "0.5 l of " so variants collapse onto one food.</summary>
    private static readonly Regex VolumePrefixRegex = new(
        @"^\s*\d+(?:\.\d+)?\s*(?:l|ml|g|kg|q)\s+of\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses game-dump FEP names like "Strength +2".</summary>
    private static readonly Regex FepNameRegex = new(@"^(\w+)\s*\+(\d)$", RegexOptions.Compiled);

    /// <summary>
    /// Mediawiki links like "[[requires::Raw Meat]]". For piped links the display text
    /// wins over the target ("[[requires::Category:Sharp Tools|Sharp Tool]]" → "Sharp Tool").
    /// </summary>
    private static readonly Regex WikiLinkRegex = new(
        @"\[\[(?:[a-zA-Z ]+::)?([^\]|]+?)(?:\|([^\]]*))?\]\]", RegexOptions.Compiled);

    /// <summary>
    /// Truncated trailing link ("..., [[requires::Wine" with no closing brackets).
    /// Only matched after a list separator (or at the start) so glued fragments stay rejected.
    /// </summary>
    private static readonly Regex TruncatedTrailingLinkRegex = new(
        @"(^|,\s*|:\s+|\bor\s+|\band\s+)\[\[(?:[a-zA-Z ]+::)?([^\]|{}]+)$", RegexOptions.Compiled);

    private static readonly Regex OptionalMarkerRegex = new(
        @"optional\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

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

    public async Task<List<FoodDto>> GetCatalogAsync(CancellationToken ct = default)
    {
        var tenantId = _tenantContext.GetCurrentTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return new List<FoodDto>();
        }

        var catalog = await _cache.GetOrCreateAsync(CatalogCacheKey(tenantId), async _ =>
        {
            // Query filters scope both sets to the current tenant.
            var foods = await _dbContext.Foods
                .AsNoTracking()
                .ToListAsync(ct);

            var variantCounts = await _dbContext.FoodVariants
                .GroupBy(v => v.FoodId)
                .Select(g => new { FoodId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.FoodId, g => g.Count, ct);

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
                    dto.VariantCount = variantCounts.GetValueOrDefault(f.Id);
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

        return new CookbookStatusDto
        {
            FoodCount = count,
            VariantCount = variantCount,
            LastImportedAt = lastImportedAt
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

        _logger.LogInformation(
            "Cookbook cleared for tenant {TenantId}: {Foods} foods, {Variants} variants removed",
            tenantId, result.Foods, result.Variants);

        return result;
    }

    public async Task<List<FoodVariantDto>> GetVariationsAsync(int foodId, CancellationToken ct = default)
    {
        // Query filter scopes to the current tenant, so foreign food ids return empty.
        var variants = await _dbContext.FoodVariants
            .AsNoTracking()
            .Where(v => v.FoodId == foodId)
            .ToListAsync(ct);

        return variants
            .Select(v => new FoodVariantDto
            {
                IngredientSignature = v.IngredientSignature,
                Energy = v.Energy,
                Hunger = v.Hunger,
                TimesSeen = v.TimesSeen,
                Feps = v.Feps
                    .Select(f => new FoodFepDto { Attribute = f.Attribute, Tier = f.Tier, Value = f.Value })
                    .ToList(),
                Ingredients = v.Ingredients
                    .Select(i => new FoodIngredientDto { Name = i.Name, Percentage = i.Percentage })
                    .ToList()
            })
            .OrderByDescending(v => v.Feps.Sum(f => f.Value))
            .ToList();
    }

    public async Task<CookbookImportResultDto> ImportAsync(
        Stream foodInfoJson, Stream? wikiJson, string tenantId, CancellationToken ct = default)
    {
        var result = new CookbookImportResultDto();

        List<SourceFoodRecord>? records;
        try
        {
            records = await JsonSerializer.DeserializeAsync<List<SourceFoodRecord>>(foodInfoJson, JsonOpts, ct);
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
            if (string.IsNullOrWhiteSpace(baseRecord.ResourceName))
            {
                result.Skipped++;
                AddError(result, $"'{name}': no resource path in any record");
                continue;
            }

            var entity = BuildFoodEntity(name, baseRecord, wiki, tenantId, importedAt, out var wikiMatched);
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
        result.Imported = entities.Count;

        _logger.LogInformation(
            "Cookbook import for tenant {TenantId}: {Imported} foods, {Variants} variants ({WikiMatched} wiki-matched, {Fallback} fallback, {Skipped} skipped) from {Records} source records",
            tenantId, result.Imported, result.Variants, result.WikiMatched, result.Fallback, result.Skipped, records.Count);

        return result;
    }

    public async Task<FoodUploadResultDto> IngestClientRecordsAsync(
        string tenantId, string? contributedByUserId, List<FoodUploadRecordDto> records, CancellationToken ct = default)
    {
        var result = new FoodUploadResultDto { Received = records.Count };
        var wiki = await GetBundledWikiAsync(ct);
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var upload in records)
        {
            if (string.IsNullOrWhiteSpace(upload.ItemName) || string.IsNullOrWhiteSpace(upload.ResourceName))
            {
                result.Skipped++;
                continue;
            }

            var source = ToSourceRecord(upload);
            var name = NormalizeName(source.ItemName!);
            var signature = ComputeSignature(source.Ingredients);

            var food = await _dbContext.Foods.IgnoreQueryFilters()
                .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.Name == name, ct);
            if (food == null)
            {
                food = BuildFoodEntity(name, source, wiki, tenantId, now, out _);
                food.ContributedBy = contributedByUserId;
                _dbContext.Foods.Add(food);
                await _dbContext.SaveChangesAsync(ct);
                result.NewFoods++;
                result.NewFoodNames.Add(name);
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
                    Feps = ParseDumpFeps(source.Feps, name),
                    Ingredients = MapIngredients(source.Ingredients)
                });
                result.NewVariants++;
                changed = true;
            }
            else
            {
                variant.TimesSeen++;
                // Keep the lowest observed FEP total as the representative record
                // (closest to base quality — same heuristic as the import).
                var newTotal = source.Feps?.Sum(f => f.Value) ?? 0m;
                var oldTotal = variant.Feps.Sum(f => f.Value);
                if (newTotal > 0 && (oldTotal == 0 || newTotal < oldTotal))
                {
                    variant.Feps = ParseDumpFeps(source.Feps, name);
                    variant.Hunger = source.Hunger;
                    variant.Energy = (int)Math.Round(source.Energy);
                }

                result.Duplicates++;
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
        }

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
        var name = VolumePrefixRegex.Replace(rawName.Trim(), string.Empty);
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
    /// Builds a food entity from a base record: wiki values (canonical base q10, categories,
    /// satiations) when the name matches a usable wiki page, dump values otherwise.
    /// </summary>
    private FoodEntity BuildFoodEntity(
        string name,
        SourceFoodRecord baseRecord,
        Dictionary<string, WikiPage>? wiki,
        string tenantId,
        DateTime importedAt,
        out bool wikiMatched)
    {
        var entity = new FoodEntity
        {
            TenantId = tenantId,
            Name = name,
            ResourceName = baseRecord.ResourceName!.Trim(),
            ImportedAt = importedAt,
            Ingredients = MapIngredients(baseRecord.Ingredients)
        };

        wikiMatched = false;
        if (wiki != null && wiki.TryGetValue(name, out var page) && page != null)
        {
            // Descriptive fields (recipe, station, url, groupings) apply on any name
            // match; base values only when the page's hunger+energy are usable.
            ApplyWikiDescriptiveFields(entity, page);
            if (TryGetMetaDecimal(page, "hunger", out var wikiHunger)
                && TryGetMetaDecimal(page, "energy", out var wikiEnergy))
            {
                ApplyWikiValues(entity, page, wikiHunger, wikiEnergy);
                wikiMatched = true;
            }
        }

        if (!wikiMatched)
        {
            ApplyDumpValues(entity, baseRecord, name);
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

        var text = OptionalMarkerRegex.Replace(raw, "optional: ");
        text = WikiLinkRegex.Replace(text, m =>
        {
            var display = m.Groups[2].Success ? m.Groups[2].Value.Trim() : string.Empty;
            return display.Length > 0 ? display : m.Groups[1].Value.Trim();
        });
        if (salvageTruncatedLink)
        {
            text = TruncatedTrailingLinkRegex.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value.Trim());
        }

        text = text.Replace("'''", string.Empty).Replace("''", string.Empty);
        text = WhitespaceRegex.Replace(text, " ").Trim().Trim(',').Trim();

        if (text.Length == 0
            || text.Length > 500
            || WikiEmptyValues.Contains(text)
            || WikiJunkNeedles.Any(needle => text.Contains(needle, StringComparison.Ordinal)))
        {
            return null;
        }

        return text;
    }

    /// <summary>Canonical base-q10 values (hunger, energy, FEPs) from the wiki page.</summary>
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

    /// <summary>Values straight from the game-data record (no usable wiki page).</summary>
    private void ApplyDumpValues(FoodEntity entity, SourceFoodRecord baseRecord, string name)
    {
        entity.Hunger = baseRecord.Hunger;
        entity.Energy = (int)Math.Round(baseRecord.Energy);
        entity.Feps.AddRange(ParseDumpFeps(baseRecord.Feps, name));
    }

    /// <summary>Parses game-dump FEP names ("Strength +2") into (Attribute, Tier, Value), stat-ordered.</summary>
    private List<FoodFep> ParseDumpFeps(List<SourceFep>? feps, string foodName)
    {
        var parsed = new List<FoodFep>();

        foreach (var fep in feps ?? new List<SourceFep>())
        {
            var match = FepNameRegex.Match(fep.Name?.Trim() ?? string.Empty);
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
