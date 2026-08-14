using HnHMapperServer.Core.Cookbook;

namespace HnHMapperServer.Tests;

/// <summary>
/// Unit tests for GameWorlds — the known-world registry behind cookbook world tagging
/// (genus normalization at ingest, display names and ordering in the /cookbook filter).
/// </summary>
public class GameWorldsTests
{
    [Fact]
    public void Normalize_TrimsWhitespace()
    {
        Assert.Equal("b7c199a4557503a8", GameWorlds.Normalize("  b7c199a4557503a8\t"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ReturnsNull_ForMissingValues(string? genus)
    {
        Assert.Null(GameWorlds.Normalize(genus));
    }

    [Fact]
    public void Normalize_ReturnsNull_ForOversizedValues()
    {
        Assert.Null(GameWorlds.Normalize(new string('a', GameWorlds.MaxGenusLength + 1)));
        Assert.NotNull(GameWorlds.Normalize(new string('a', GameWorlds.MaxGenusLength)));
    }

    [Theory]
    [InlineData("c646473983afec09", "W16")]
    [InlineData("b7c199a4557503a8", "W16.1")]
    [InlineData("fd63ddee958da329", "W16.2")]
    public void DisplayName_ResolvesKnownWorlds(string genus, string expected)
    {
        Assert.Equal(expected, GameWorlds.DisplayName(genus));
    }

    [Fact]
    public void DisplayName_ShortensUnknownHashes()
    {
        Assert.Equal("0123abcd…", GameWorlds.DisplayName("0123abcd4567ef89"));
        Assert.Equal("short", GameWorlds.DisplayName("short"));
    }

    [Fact]
    public void OrderOf_RanksKnownWorldsAboveUnknown()
    {
        Assert.True(GameWorlds.OrderOf("b7c199a4557503a8") > GameWorlds.OrderOf("c646473983afec09"));
        Assert.Equal(-1, GameWorlds.OrderOf("0123abcd4567ef89"));
    }

    [Fact]
    public void Known_HasUniqueGenusAndOrder()
    {
        Assert.Equal(GameWorlds.Known.Count, GameWorlds.Known.Select(w => w.Genus).Distinct().Count());
        Assert.Equal(GameWorlds.Known.Count, GameWorlds.Known.Select(w => w.Order).Distinct().Count());
    }
}
