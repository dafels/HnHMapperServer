using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// Pure matching/counting kernel for the cookbook's ingredient-selection facet — the
/// "what can I cook from my larder" filter over recorded recipe variations.
///
/// Matching is EXACT ingredient-name equality, case-insensitive: recorded ingredient
/// names are canonical game item names (measured collision-free across ~49k recipes),
/// so anything fuzzier could only cause false bridging. Generic-term bridging
/// ("mushroom" finding Chantrelles via the canonical recipe text) is deliberately the
/// free-text search's job, not this kernel's.
///
/// Contract: selection sets passed in must be created with
/// StringComparer.OrdinalIgnoreCase — set membership supplies the case-insensitivity.
/// </summary>
public static class IngredientFilter
{
    /// <summary>
    /// One recipe's ingredient list measured against a selection: how many DISTINCT
    /// recipe ingredients are in the selection (<see cref="Hits"/>), how many are not
    /// (<see cref="Missing"/>), and — exactly when one is missing — its name
    /// (<see cref="SoleMissing"/>: "adding this ingredient unlocks the recipe").
    /// </summary>
    public readonly record struct ScanResult(int Hits, int Missing, string? SoleMissing);

    /// <summary>
    /// One pass over the recipe's ingredients (cost independent of selection size).
    /// A name listed more than once on the same recipe counts once.
    /// </summary>
    public static ScanResult Scan(IReadOnlyList<FoodIngredientDto> ingredients, IReadOnlySet<string> selected)
    {
        var hits = 0;
        var missing = 0;
        string? soleMissing = null;
        for (var i = 0; i < ingredients.Count; i++)
        {
            var name = ingredients[i].Name;
            if (SeenBefore(ingredients, i, name))
            {
                continue;
            }

            if (selected.Contains(name))
            {
                hits++;
            }
            else
            {
                missing++;
                soleMissing = missing == 1 ? name : null;
            }
        }

        return new ScanResult(hits, missing, soleMissing);
    }

    /// <summary>
    /// Whether one recipe passes the selection. An empty selection filters nothing.
    /// Default mode ("contains all"): the recipe contains EVERY selected ingredient
    /// (AND). <paramref name="onlyMode"/> ("only these" — the strict larder): the
    /// recipe's ENTIRE recorded ingredient list lies inside the selection. Recipes with
    /// NO recorded ingredients never match an active selection in either mode: a
    /// missing list means the combination is unknown, not that it needs nothing — an
    /// empty list must not pass a larder check.
    /// </summary>
    public static bool Matches(IReadOnlyList<FoodIngredientDto> ingredients, IReadOnlySet<string> selected, bool onlyMode)
    {
        if (selected.Count == 0)
        {
            return true;
        }

        if (onlyMode)
        {
            return ingredients.Count > 0 && Scan(ingredients, selected).Missing == 0;
        }

        return Scan(ingredients, selected).Hits == selected.Count;
    }

    /// <summary>
    /// Adds one recipe's DISTINCT ingredient names into a per-name recipe counter
    /// (a name repeated within the recipe counts once; blank names are skipped).
    /// Shared by <see cref="BuildVocabulary"/> and the live facet counting.
    /// </summary>
    public static void CountDistinctNames(IReadOnlyList<FoodIngredientDto> ingredients, Dictionary<string, int> counts)
    {
        for (var i = 0; i < ingredients.Count; i++)
        {
            var name = ingredients[i].Name;
            if (name.Length == 0 || SeenBefore(ingredients, i, name))
            {
                continue;
            }

            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
    }

    /// <summary>
    /// Frequency-ranked ingredient vocabulary: name → number of recipes using it,
    /// most-common first, ties alphabetical. This ranking is the data analysis the
    /// ingredient picker presents.
    /// </summary>
    public static List<KeyValuePair<string, int>> BuildVocabulary(IEnumerable<IReadOnlyList<FoodIngredientDto>> recipes)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ingredients in recipes)
        {
            CountDistinctNames(ingredients, counts);
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Prior-index distinct guard — recipe lists hold a handful of names; no allocation.</summary>
    private static bool SeenBefore(IReadOnlyList<FoodIngredientDto> ingredients, int index, string name)
    {
        for (var j = 0; j < index; j++)
        {
            if (string.Equals(ingredients[j].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
