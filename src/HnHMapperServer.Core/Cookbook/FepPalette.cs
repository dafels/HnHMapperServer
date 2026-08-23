using System.Collections.Frozen;

namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// FEP stat colors and display names, shared by the cookbook page and the
/// notification bell's stat preview. Cediner hnh-food-book palette (FEPBar.vue):
/// muted tier-1, brighter tier-2 per stat. All colors are light pastels — pair
/// them with dark text (the cookbook uses #33322e) for ≥4.5:1 contrast.
/// </summary>
public static class FepPalette
{
    private static readonly FrozenDictionary<string, (string Tier1, string Tier2)> StatColors = new Dictionary<string, (string Tier1, string Tier2)>(StringComparer.OrdinalIgnoreCase)
    {
        ["STR"] = ("#BF9794", "#DF958F"),
        ["AGI"] = ("#9995B8", "#9991DC"),
        ["INT"] = ("#9DB7B9", "#97D6DC"),
        ["CON"] = ("#C29AB4", "#E193C5"),
        ["PER"] = ("#E4BF98", "#F2C28D"),
        ["CHA"] = ("#9BEEB1", "#8EF7AA"),
        ["DEX"] = ("#FEFDCC", "#FFFEA6"),
        ["WILL"] = ("#E4F38F", "#EEFF9E"),
        ["PSY"] = ("#C48DFD", "#C286FE")
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenDictionary<string, string> StatFullNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["STR"] = "Strength",
        ["AGI"] = "Agility",
        ["INT"] = "Intelligence",
        ["CON"] = "Constitution",
        ["PER"] = "Perception",
        ["CHA"] = "Charisma",
        ["DEX"] = "Dexterity",
        ["WILL"] = "Will",
        ["PSY"] = "Psyche"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hex color for a stat pill; unknown stats fall back to neutral gray.</summary>
    public static string Color(string attribute, int tier = 1) =>
        StatColors.TryGetValue(attribute, out var c) ? (tier >= 2 ? c.Tier2 : c.Tier1) : "#d8d8d8";

    /// <summary>Full stat name ("Strength") for tooltips; the abbreviation itself when unknown.</summary>
    public static string FullName(string attribute) =>
        StatFullNames.TryGetValue(attribute, out var full) ? full : attribute;
}
