using HnHMapperServer.Core.Cookbook;

namespace HnHMapperServer.Tests;

/// <summary>
/// Unit tests for FepFilterParser — extraction of cediner-style threshold expressions
/// ("str&gt;50%", "int2&gt;15") from the cookbook search text.
/// </summary>
public class FepFilterParserTests
{
    [Fact]
    public void Parses_PercentCondition()
    {
        var (conditions, residual) = FepFilterParser.Parse("str>50%");

        var c = Assert.Single(conditions);
        Assert.Equal(FepFilterKey.Stat, c.Key);
        Assert.Equal("STR", c.Attribute);
        Assert.Null(c.Tier);
        Assert.Equal(FepFilterOp.Gt, c.Op);
        Assert.Equal(50m, c.Value);
        Assert.True(c.IsPercent);
        Assert.Equal(string.Empty, residual);
    }

    [Fact]
    public void Parses_TieredAbsoluteCondition()
    {
        var (conditions, residual) = FepFilterParser.Parse("int2>15");

        var c = Assert.Single(conditions);
        Assert.Equal(FepFilterKey.Stat, c.Key);
        Assert.Equal("INT", c.Attribute);
        Assert.Equal(2, c.Tier);
        Assert.Equal(FepFilterOp.Gt, c.Op);
        Assert.Equal(15m, c.Value);
        Assert.False(c.IsPercent);
        Assert.Equal(string.Empty, residual);
    }

    [Theory]
    [InlineData("total>30", FepFilterKey.Total)]
    [InlineData("hunger<2", FepFilterKey.Hunger)]
    [InlineData("energy>=150", FepFilterKey.Energy)]
    [InlineData("eff>=3.5", FepFilterKey.Eff)]
    public void Parses_NonStatKeys(string input, FepFilterKey expectedKey)
    {
        var (conditions, residual) = FepFilterParser.Parse(input);

        var c = Assert.Single(conditions);
        Assert.Equal(expectedKey, c.Key);
        Assert.Null(c.Attribute);
        Assert.Null(c.Tier);
        Assert.False(c.IsPercent);
        Assert.Equal(string.Empty, residual);
    }

    [Theory]
    [InlineData("str>50", FepFilterOp.Gt)]
    [InlineData("str>=50", FepFilterOp.Ge)]
    [InlineData("str<50", FepFilterOp.Lt)]
    [InlineData("str<=50", FepFilterOp.Le)]
    [InlineData("str=50", FepFilterOp.Eq)]
    [InlineData("str==50", FepFilterOp.Eq)]
    public void Parses_AllOperators(string input, FepFilterOp expectedOp)
    {
        var (conditions, _) = FepFilterParser.Parse(input);

        Assert.Equal(expectedOp, Assert.Single(conditions).Op);
    }

    [Theory]
    [InlineData("str > 50 %")]
    [InlineData("str >50%")]
    [InlineData("str> 50 %")]
    [InlineData("STR>50%")]
    [InlineData("Str>50%")]
    public void Parses_WhitespaceAndCaseVariants(string input)
    {
        var (conditions, residual) = FepFilterParser.Parse(input);

        var c = Assert.Single(conditions);
        Assert.Equal("STR", c.Attribute);
        Assert.True(c.IsPercent);
        Assert.Equal(50m, c.Value);
        Assert.Equal(string.Empty, residual);
    }

    [Fact]
    public void Parses_DecimalValue()
    {
        var (conditions, _) = FepFilterParser.Parse("eff>=3.5");

        Assert.Equal(3.5m, Assert.Single(conditions).Value);
    }

    [Fact]
    public void Parses_WillAsFourLetterKey()
    {
        var (conditions, _) = FepFilterParser.Parse("will>=10");

        Assert.Equal("WILL", Assert.Single(conditions).Attribute);
    }

    [Fact]
    public void Parses_PercentOnTieredStat()
    {
        var (conditions, _) = FepFilterParser.Parse("int2>25%");

        var c = Assert.Single(conditions);
        Assert.Equal("INT", c.Attribute);
        Assert.Equal(2, c.Tier);
        Assert.True(c.IsPercent);
    }

    [Fact]
    public void Parses_CommaChain()
    {
        var (conditions, residual) = FepFilterParser.Parse("str>50%, int2>15");

        Assert.Equal(2, conditions.Count);
        Assert.Equal("STR", conditions[0].Attribute);
        Assert.Equal("INT", conditions[1].Attribute);
        Assert.Equal(2, conditions[1].Tier);
        Assert.Equal(string.Empty, residual);
    }

    [Fact]
    public void MixedText_ConditionsExtracted_ResidualKeepsSearchTerms()
    {
        var (conditions, residual) = FepFilterParser.Parse("meat str>50% int2>15 stew");

        Assert.Equal(2, conditions.Count);
        Assert.Equal("meat stew", residual);
    }

    [Fact]
    public void MixedText_SeparatorCommasCollapse()
    {
        var (conditions, residual) = FepFilterParser.Parse("meat, str>50% stew");

        Assert.Single(conditions);
        Assert.Equal("meat stew", residual);
    }

    [Theory]
    [InlineData("stx>50")]     // unknown key
    [InlineData("hunger>50%")] // % on a non-stat key
    [InlineData("total2>5")]   // tier on a non-stat key
    [InlineData("straw")]      // stat prefix inside a word, no operator
    [InlineData("int21>5")]    // two-digit tier
    [InlineData("beefstr>5")]  // key not at a word boundary
    [InlineData("str>50x")]    // trailing junk breaks the token
    [InlineData("str>")]       // no value
    [InlineData("int")]        // bare key without operator
    public void InvalidTokens_StayInResidual_ByteIdentical(string input)
    {
        var (conditions, residual) = FepFilterParser.Parse(input);

        Assert.Empty(conditions);
        Assert.Equal(input, residual);
    }

    [Fact]
    public void InvalidToken_NextToValidCondition_StaysInResidual()
    {
        var (conditions, residual) = FepFilterParser.Parse("hunger>50% str>10");

        var c = Assert.Single(conditions);
        Assert.Equal("STR", c.Attribute);
        Assert.Equal("hunger>50%", residual);
    }

    [Theory]
    [InlineData("roast  pork ")] // odd whitespace preserved when nothing was extracted
    [InlineData("meat, cheese")]
    public void PlainSearch_ReturnsInputByteIdentical(string input)
    {
        var (conditions, residual) = FepFilterParser.Parse(input);

        Assert.Empty(conditions);
        Assert.Equal(input, residual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_ReturnsEmpty(string? input)
    {
        var (conditions, residual) = FepFilterParser.Parse(input);

        Assert.Empty(conditions);
        Assert.Equal(string.Empty, residual);
    }

    [Fact]
    public void Spans_LocateRawTextInOriginalString()
    {
        const string input = "meat str>50%  int2 >= 15, stew";

        var (conditions, _) = FepFilterParser.Parse(input);

        Assert.Equal(2, conditions.Count);
        foreach (var c in conditions)
        {
            Assert.Equal(c.RawText, input.Substring(c.Start, c.Length));
        }
    }
}
