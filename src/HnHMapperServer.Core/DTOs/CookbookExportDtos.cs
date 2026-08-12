namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// Portable snapshot of one tenant's cookbook: every food with every recorded recipe
/// variation, world tags, per-world values, and contributor usernames. Produced by the
/// tenant-admin export endpoint and accepted back by the import endpoint (which tells
/// it apart from the raw game dump by the <see cref="Format"/> marker — the dump is a
/// JSON array, an export is an object). Contributors travel as usernames, not internal
/// user ids, so the file stays portable across servers; import re-resolves the names
/// against local accounts.
/// </summary>
public class CookbookExportDto
{
    public const string FormatMarker = "hnh-cookbook-export";
    public const int CurrentVersion = 1;

    /// <summary>
    /// Always <see cref="FormatMarker"/> in produced files. Deliberately NOT defaulted
    /// to the marker: import detection deserializes arbitrary object-rooted files into
    /// this type, and a default here would misidentify them as exports.
    /// </summary>
    public string Format { get; set; } = string.Empty;

    public int Version { get; set; }

    public DateTime ExportedAt { get; set; }

    public int FoodCount { get; set; }

    /// <summary>Recipe variations across all foods.</summary>
    public int VariantCount { get; set; }

    public List<CookbookExportFoodDto> Foods { get; set; } = new();
}

/// <summary>One food of a cookbook export, with all of its recorded recipe variations.</summary>
public class CookbookExportFoodDto
{
    /// <summary>Display name, volume-prefix normalized (unique per tenant).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full game resource path, e.g. "gfx/invobjs/autumnsteak".</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Canonical (all-worlds) energy restored when eaten.</summary>
    public int Energy { get; set; }

    /// <summary>Canonical (all-worlds) hunger cost per bite.</summary>
    public decimal Hunger { get; set; }

    public string? WikiUrl { get; set; }

    /// <summary>Canonical recipe line from the wiki, when one was matched.</summary>
    public string? RecipeText { get; set; }

    public string? CookingStation { get; set; }

    /// <summary>UTC timestamp when this food entered the catalog (preserved on import).</summary>
    public DateTime AddedAt { get; set; }

    /// <summary>Username of the player who discovered this food; NULL for admin imports.</summary>
    public string? ContributedBy { get; set; }

    public List<string> Categories { get; set; } = new();

    public List<string> SatiationGroups { get; set; } = new();

    /// <summary>Genus hashes of the game worlds this food was uploaded from (see GameWorlds).</summary>
    public List<string> Worlds { get; set; } = new();

    public List<FoodFepDto> Feps { get; set; } = new();

    public List<FoodIngredientDto> Ingredients { get; set; } = new();

    public List<CookbookExportVariantDto> Variants { get; set; } = new();
}

/// <summary>One recorded recipe variation of an exported food.</summary>
public class CookbookExportVariantDto
{
    /// <summary>
    /// Canonical ingredient-combination key. Exported (rather than recomputed on import)
    /// because panels/favorites pin variants by this signature — it must survive a
    /// roundtrip byte-identical.
    /// </summary>
    public string IngredientSignature { get; set; } = string.Empty;

    public int Energy { get; set; }

    public decimal Hunger { get; set; }

    /// <summary>How many source records had this exact ingredient combination.</summary>
    public int TimesSeen { get; set; }

    /// <summary>Usernames of everyone who uploaded this variation (empty for imported data).</summary>
    public List<string> Contributors { get; set; } = new();

    /// <summary>Genus hashes of the game worlds this exact variation was uploaded from.</summary>
    public List<string> Worlds { get; set; } = new();

    /// <summary>Per-world representative values (see FoodVariantEntity.WorldValues).</summary>
    public List<FoodWorldValueDto> WorldValues { get; set; } = new();

    public List<FoodFepDto> Feps { get; set; } = new();

    public List<FoodIngredientDto> Ingredients { get; set; } = new();
}
