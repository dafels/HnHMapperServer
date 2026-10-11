using HnHMapperServer.Core.Cookbook;

namespace HnHMapperServer.Tests;

/// <summary>ShippedIcons: the pure shipped-icon manifest FoodIcons consults.</summary>
public class ShippedIconsTests
{
    [Fact]
    public void FromNames_MayContain_IsOrdinalCaseSensitive()
    {
        var icons = ShippedIcons.FromNames(new[] { "gfx/invobjs/lobster" });

        Assert.True(icons.IsKnown);
        Assert.True(icons.MayContain("gfx/invobjs/lobster"));
        Assert.False(icons.MayContain("gfx/invobjs/Lobster"));
        Assert.False(icons.MayContain("gfx/invobjs/lobster.png"));
    }

    [Fact]
    public void Unknown_IsNotKnown_AndMayContainAnything()
    {
        Assert.False(ShippedIcons.Unknown.IsKnown);
        Assert.Equal(0, ShippedIcons.Unknown.Count);
        Assert.True(ShippedIcons.Unknown.MayContain("gfx/anything"));
    }

    [Fact]
    public void FromNames_Count_MatchesDistinctNames()
    {
        Assert.Equal(2, ShippedIcons.FromNames(new[] { "gfx/a", "gfx/b", "gfx/a" }).Count);
    }
}
