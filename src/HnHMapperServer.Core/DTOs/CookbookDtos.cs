namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// A food in the cookbook catalog with FEP values, ingredients, and wiki-sourced grouping.
/// </summary>
public class FoodDto
{
    public int Id { get; set; }

    /// <summary>Display name, e.g. "Autumn Steak".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full game resource path, e.g. "gfx/invobjs/autumnsteak".</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Energy restored when eaten (game percent points).</summary>
    public int Energy { get; set; }

    /// <summary>Hunger cost per bite (fractional values are meaningful).</summary>
    public decimal Hunger { get; set; }

    /// <summary>ringofbrodgar.com page URL, when the food matched a wiki page.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>
    /// Canonical recipe from the wiki ("Raw Meat, Salad Greens, Edible Mushroom x2,
    /// optional: Spices"). NULL when the wiki has no usable recipe — the UI then falls
    /// back to <see cref="Ingredients"/> labeled as a recorded combination.
    /// </summary>
    public string? RecipeText { get; set; }

    /// <summary>Cooking station from the wiki ("Frying Pan and Fire"). NULL when unknown.</summary>
    public string? CookingStation { get; set; }

    /// <summary>Wiki categories, e.g. ["Meat Dishes"]. Empty when no wiki match.</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>In-game satiation groups the food drains, e.g. ["Meat", "Mushrooms"].</summary>
    public List<string> SatiationGroups { get; set; } = new();

    public List<FoodFepDto> Feps { get; set; } = new();

    public List<FoodIngredientDto> Ingredients { get; set; } = new();

    /// <summary>Number of distinct recorded recipe variations (1 = only the headline recipe).</summary>
    public int VariantCount { get; set; }

    /// <summary>Username of the player whose client upload added this food; NULL for imports.</summary>
    public string? ContributedByName { get; set; }

    /// <summary>UTC timestamp when this food entered the catalog.</summary>
    public DateTime ImportedAt { get; set; }
}

/// <summary>
/// A user's named food collection (chips of food names). Favorites is a regular
/// panel flagged IsFavorites; shared panels are visible read-only tenant-wide.
/// </summary>
public class FoodPanelDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsShared { get; set; }

    public bool IsFavorites { get; set; }

    /// <summary>True when the requesting user owns this panel (can edit it).</summary>
    public bool IsOwn { get; set; }

    /// <summary>Owner's username (shown on shared panels).</summary>
    public string OwnerName { get; set; } = string.Empty;

    public List<FoodPanelItemDto> Items { get; set; } = new();
}

public class FoodPanelItemDto
{
    public int ItemId { get; set; }

    public string FoodName { get; set; } = string.Empty;

    /// <summary>Empty = the whole food; otherwise the pinned recipe variant's signature.</summary>
    public string IngredientSignature { get; set; } = string.Empty;

    /// <summary>Ingredient list label for variant items, NULL for whole foods.</summary>
    public string? Label { get; set; }

    public int Position { get; set; }

    // Live display data resolved against the current catalog at read time
    // (items reference foods by name, so this goes stale-and-back across re-imports).

    /// <summary>False when the food (or the specific variant) is not in the catalog right now.</summary>
    public bool Resolved { get; set; }

    public decimal Hunger { get; set; }

    public int Energy { get; set; }

    public List<FoodFepDto> Feps { get; set; } = new();
}

public class CreateFoodPanelDto
{
    public string Name { get; set; } = string.Empty;
}

public class UpdateFoodPanelDto
{
    public string? Name { get; set; }

    public bool? IsShared { get; set; }
}

/// <summary>Add a food — or one specific recipe variant — to a panel / toggle it in Favorites.</summary>
public class PanelItemRequestDto
{
    public string FoodName { get; set; } = string.Empty;

    /// <summary>Empty/null = the whole food; otherwise a variant's ingredient signature.</summary>
    public string? IngredientSignature { get; set; }
}

/// <summary>Remove one panel item by its id.</summary>
public class PanelItemRemoveDto
{
    public int ItemId { get; set; }
}

/// <summary>Full desired item order for a panel (item ids in display order).</summary>
public class PanelOrderDto
{
    public List<int> ItemIds { get; set; } = new();
}

public class FavoriteToggleResultDto
{
    public string FoodName { get; set; } = string.Empty;

    public string IngredientSignature { get; set; } = string.Empty;

    public bool IsFavorite { get; set; }

    /// <summary>The Favorites panel (created on first toggle) so the UI can refresh it.</summary>
    public FoodPanelDto? Panel { get; set; }
}

/// <summary>
/// One recorded recipe variation of a food: a distinct ingredient combination and the
/// values observed for it. Fetched lazily per food.
/// </summary>
public class FoodVariantDto
{
    /// <summary>Canonical ingredient-combination key (used for variant favorites/panels).</summary>
    public string IngredientSignature { get; set; } = string.Empty;

    public int Energy { get; set; }

    public decimal Hunger { get; set; }

    /// <summary>How many source records had this exact ingredient combination.</summary>
    public int TimesSeen { get; set; }

    public List<FoodFepDto> Feps { get; set; } = new();

    public List<FoodIngredientDto> Ingredients { get; set; } = new();
}

/// <summary>
/// One entry of the wiki recipe index: a craftable item name with its canonical recipe
/// line. Covers ALL wiki pages — including intermediates that are not eaten foods, like
/// "Unbaked Meatpie" — so the UI can expand recipe ingredients recursively.
/// </summary>
public class RecipeIndexEntryDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Plain recipe line, e.g. "Water (0.5 liters), Any Flour (0.35 kg), Raw Meat x2, Egg".</summary>
    public string Recipe { get; set; } = string.Empty;

    /// <summary>Cooking station, when known.</summary>
    public string? Station { get; set; }
}

/// <summary>
/// One FEP line of a food at base quality (q10).
/// </summary>
public class FoodFepDto
{
    /// <summary>Stat abbreviation: STR, AGI, INT, CON, PER, CHA, DEX, WILL, PSY.</summary>
    public string Attribute { get; set; } = string.Empty;

    /// <summary>1 for +1 FEPs, 2 for +2 FEPs.</summary>
    public int Tier { get; set; }

    public decimal Value { get; set; }
}

/// <summary>
/// One ingredient of a food's recipe.
/// </summary>
public class FoodIngredientDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>How much of the recipe this ingredient fills, 0-100.</summary>
    public decimal Percentage { get; set; }
}

/// <summary>
/// Current state of the cookbook catalog (tenant-admin status view).
/// </summary>
public class CookbookStatusDto
{
    public int FoodCount { get; set; }

    /// <summary>Recorded recipe variations across all foods.</summary>
    public int VariantCount { get; set; }

    /// <summary>UTC timestamp of the last import, NULL when no data imported yet.</summary>
    public DateTime? LastImportedAt { get; set; }
}

/// <summary>
/// Outcome of clearing a tenant's cookbook (rows removed).
/// </summary>
public class CookbookClearResultDto
{
    public int Foods { get; set; }

    public int Variants { get; set; }
}

/// <summary>
/// One food record uploaded by a game client (Hurricane "cookbook integration",
/// KamiClient autofood, or Amber-style batches). Field names match the client JSON.
/// </summary>
public class FoodUploadRecordDto
{
    public string? ItemName { get; set; }

    public string? ResourceName { get; set; }

    public decimal Energy { get; set; }

    public decimal Hunger { get; set; }

    /// <summary>Sent by KamiClient only; currently stored as-recorded (not used for normalization).</summary>
    public decimal? Quality { get; set; }

    /// <summary>Game world identifier some clients send; ignored.</summary>
    public string? Genus { get; set; }

    public List<FoodUploadIngredientDto>? Ingredients { get; set; }

    public List<FoodUploadFepDto>? Feps { get; set; }
}

public class FoodUploadIngredientDto
{
    public string? Name { get; set; }

    public decimal Percentage { get; set; }
}

public class FoodUploadFepDto
{
    /// <summary>Full FEP name as the game shows it, e.g. "Strength +2".</summary>
    public string? Name { get; set; }

    public decimal Value { get; set; }
}

/// <summary>
/// Outcome of a client food upload batch.
/// </summary>
public class FoodUploadResultDto
{
    public int Received { get; set; }

    public int NewFoods { get; set; }

    public int NewVariants { get; set; }

    /// <summary>Records whose ingredient combination was already known (TimesSeen bumped).</summary>
    public int Duplicates { get; set; }

    /// <summary>Records without a usable name/resource.</summary>
    public int Skipped { get; set; }

    /// <summary>Names of foods newly created by this batch (for the tenant notification digest).</summary>
    public List<string> NewFoodNames { get; set; } = new();
}

/// <summary>
/// Outcome of a cookbook import (wipe-and-replace).
/// </summary>
public class CookbookImportResultDto
{
    /// <summary>Number of foods written to the catalog.</summary>
    public int Imported { get; set; }

    /// <summary>Number of recipe variations written across all foods.</summary>
    public int Variants { get; set; }

    /// <summary>Foods whose values came from a matching wiki page.</summary>
    public int WikiMatched { get; set; }

    /// <summary>Foods without a usable wiki page (values from the game-data dump).</summary>
    public int Fallback { get; set; }

    /// <summary>Foods dropped because they could not be mapped.</summary>
    public int Skipped { get; set; }

    public List<string> Errors { get; set; } = new();
}
