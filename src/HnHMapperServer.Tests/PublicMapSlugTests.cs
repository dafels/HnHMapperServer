using HnHMapperServer.Core.PublicMaps;

namespace HnHMapperServer.Tests;

/// <summary>
/// The public-map slug rule. It was duplicated in PublicMapService and in the create
/// dialog's live preview; both now call <see cref="PublicMapSlug"/>, so the rule is worth
/// pinning down — a change here silently changes the URL every public map gets.
/// </summary>
public class PublicMapSlugTests
{
    [Theory]
    [InlineData("Northwind Village", "northwind-village")]
    [InlineData("Gooner's grove", "gooner-s-grove")]
    [InlineData("UPPER Case Name", "upper-case-name")]
    [InlineData("already-slugged", "already-slugged")]
    public void Generate_NormalNames_LowercasesAndHyphenates(string input, string expected)
    {
        Assert.Equal(expected, PublicMapSlug.Generate(input));
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData(null)]
    public void Generate_BlankName_ReturnsFallback(string? input)
    {
        Assert.Equal(PublicMapSlug.Fallback, PublicMapSlug.Generate(input!));
    }

    [Fact]
    public void Generate_CollapsesHyphenRunsAndTrimsEdges()
    {
        // "!!! Map --- Two !!!" -> hyphens for every invalid char, then collapsed and trimmed.
        Assert.Equal("map-two", PublicMapSlug.Generate("!!! Map --- Two !!!"));
    }

    [Fact]
    public void Generate_ShortResult_IsPadded()
    {
        // Under three characters would make an awkward URL, so it gets a prefix.
        Assert.Equal("map-ab", PublicMapSlug.Generate("ab"));
    }

    [Fact]
    public void Generate_LongResult_IsTruncatedTo50WithoutTrailingHyphen()
    {
        var slug = PublicMapSlug.Generate(new string('a', 40) + " " + new string('b', 40));

        Assert.True(slug.Length <= 50, $"expected <= 50 chars, got {slug.Length}");
        Assert.DoesNotContain("--", slug);
        Assert.False(slug.EndsWith('-'), "a truncated slug must not end on a hyphen");
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        // Both the server and the dialog preview call this; they must agree every time.
        const string name = "Bear Valley Outpost #3";
        Assert.Equal(PublicMapSlug.Generate(name), PublicMapSlug.Generate(name));
    }
}
