using HnHMapperServer.Core.Cookbook;
using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Tests;

/// <summary>
/// Pure kernel behind the cookbook's ingredient-selection facet: exact-name matching,
/// "contains all" vs strict "only these" semantics, the zero-ingredient policy, near-miss
/// (SoleMissing) bookkeeping, and the frequency-ranked vocabulary.
/// </summary>
public class IngredientFilterTests
{
    private static List<FoodIngredientDto> Recipe(params string[] names) =>
        names.Select(n => new FoodIngredientDto { Name = n, Percentage = 100m / Math.Max(1, names.Length) }).ToList();

    private static HashSet<string> Selection(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Scan_CountsDistinctHitsAndMissing()
    {
        var result = IngredientFilter.Scan(Recipe("Salt", "Pork", "Chives"), Selection("Salt", "Chives", "Butter"));

        Assert.Equal(2, result.Hits);
        Assert.Equal(1, result.Missing);
        Assert.Equal("Pork", result.SoleMissing);
    }

    [Fact]
    public void Scan_DuplicateNameOnOneRecipe_CountsOnce()
    {
        // A recipe listing an ingredient twice (real data: 50%+50% splits) must not
        // double-count it as hit or missing.
        var asHit = IngredientFilter.Scan(Recipe("Salt", "Salt"), Selection("Salt"));
        Assert.Equal(1, asHit.Hits);
        Assert.Equal(0, asHit.Missing);

        var asMissing = IngredientFilter.Scan(Recipe("Pork", "pork", "Salt"), Selection("Salt"));
        Assert.Equal(1, asMissing.Hits);
        Assert.Equal(1, asMissing.Missing);
        Assert.Equal("Pork", asMissing.SoleMissing);
    }

    [Fact]
    public void Scan_IsCaseInsensitive()
    {
        var result = IngredientFilter.Scan(Recipe("SALT", "chives"), Selection("salt", "Chives"));

        Assert.Equal(2, result.Hits);
        Assert.Equal(0, result.Missing);
    }

    [Fact]
    public void Scan_SoleMissingClearsWhenSecondMissingAppears()
    {
        var result = IngredientFilter.Scan(Recipe("Pork", "Butter", "Salt"), Selection("Salt"));

        Assert.Equal(2, result.Missing);
        Assert.Null(result.SoleMissing);
    }

    [Fact]
    public void Matches_EmptySelection_MatchesEverything()
    {
        Assert.True(IngredientFilter.Matches(Recipe("Pork"), Selection(), onlyMode: false));
        Assert.True(IngredientFilter.Matches(Recipe(), Selection(), onlyMode: false));
        Assert.True(IngredientFilter.Matches(Recipe(), Selection(), onlyMode: true));
    }

    [Fact]
    public void Matches_DefaultMode_RequiresEverySelectedIngredient()
    {
        var selection = Selection("Salt", "Chives");

        // All selected present (extras allowed) matches; a partial hit doesn't.
        Assert.True(IngredientFilter.Matches(Recipe("Salt", "Chives", "Pork"), selection, onlyMode: false));
        Assert.False(IngredientFilter.Matches(Recipe("Salt", "Pork"), selection, onlyMode: false));
        Assert.False(IngredientFilter.Matches(Recipe("Pork"), selection, onlyMode: false));
    }

    [Fact]
    public void Matches_OnlyMode_RequiresRecipeSubsetOfSelection()
    {
        var larder = Selection("Salt", "Chives", "Pickled Onions");

        // Entire recipe covered by the larder — matches, even without using all of it.
        Assert.True(IngredientFilter.Matches(Recipe("Salt", "Chives"), larder, onlyMode: true));
        // One ingredient outside the larder — cannot cook it.
        Assert.False(IngredientFilter.Matches(Recipe("Salt", "Pork"), larder, onlyMode: true));
    }

    [Fact]
    public void Matches_ZeroIngredientRecipe_NeverMatchesAnActiveSelection()
    {
        var selection = Selection("Salt");

        // Default: contains none of the selection. Strict: unknown ingredients are
        // not "none" — an empty list must not pass a larder check.
        Assert.False(IngredientFilter.Matches(Recipe(), selection, onlyMode: false));
        Assert.False(IngredientFilter.Matches(Recipe(), selection, onlyMode: true));
    }

    [Fact]
    public void CountDistinctNames_SkipsBlanksAndDuplicates()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IngredientFilter.CountDistinctNames(Recipe("Salt", "salt", "", "Pork"), counts);
        IngredientFilter.CountDistinctNames(Recipe("Salt"), counts);

        Assert.Equal(2, counts["Salt"]);
        Assert.Equal(1, counts["Pork"]);
        Assert.Equal(2, counts.Count);
    }

    [Fact]
    public void BuildVocabulary_RanksByRecipeCountThenName()
    {
        var vocab = IngredientFilter.BuildVocabulary(new[]
        {
            Recipe("Salt", "Pork"),
            Recipe("Salt", "Chives", "chives"), // duplicate within one recipe counts once
            Recipe("Chives"),
            Recipe("Apple")
        });

        Assert.Equal(
            new[] { ("Chives", 2), ("Salt", 2), ("Apple", 1), ("Pork", 1) },
            vocab.Select(kv => (kv.Key, kv.Value)).ToArray());
    }
}
