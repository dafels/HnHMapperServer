using HnHMapperServer.Core.Cookbook;

namespace HnHMapperServer.Tests;

/// <summary>
/// FoodIcons with the shipped-icon manifest: shipped icons stay local-first with the remote
/// fallback; icons known not to ship go straight to the game's renderer (they cost a local 404
/// on every render before); unusable names request nothing; an
/// unknown manifest keeps the old local-first behaviour.
/// </summary>
public class FoodIconsTests
{
    private static readonly ShippedIcons Shipped = ShippedIcons.FromNames(new[] { "gfx/invobjs/lobster" });

    [Fact]
    public void Src_ShippedIcon_IsLocal_WithRemoteOnErrorFallback()
    {
        Assert.Equal("/gfx/invobjs/lobster.png", FoodIcons.Src(Shipped, "gfx/invobjs/lobster"));
        Assert.Equal("this.onerror=null;this.src='https://www.havenandhearth.com/mt/r/gfx/invobjs/lobster';",
            FoodIcons.RemoteFallback(Shipped, "gfx/invobjs/lobster"));
        Assert.Contains("this.src='https://www.havenandhearth.com/mt/r/gfx/invobjs/lobster'",
            FoodIcons.RemoteFallbackOrHide(Shipped, "gfx/invobjs/lobster"));
    }

    [Theory]
    [InlineData("gfx/invobjs/wurst-s-swanneck")]
    [InlineData("gfx/invobjs/lobster-boiled")]
    [InlineData("gfx/invobjs/champignon-small")]
    [InlineData("gfx/invobjs/pansearedfish")]
    public void Src_UnshippedIcon_GoesStraightToRemote(string name)
    {
        Assert.Equal(FoodIcons.RemoteRendererBase + name, FoodIcons.Src(Shipped, name));
        Assert.Null(FoodIcons.RemoteFallback(Shipped, name));
        var hide = FoodIcons.RemoteFallbackOrHide(Shipped, name);
        Assert.DoesNotContain("src=", hide);
        Assert.Contains("display='none'", hide);
    }

    [Fact]
    public void Src_UnknownManifest_KeepsLocalFirst()
    {
        Assert.Equal("/gfx/invobjs/pansearedfish.png", FoodIcons.Src(ShippedIcons.Unknown, "gfx/invobjs/pansearedfish"));
        Assert.NotNull(FoodIcons.RemoteFallback(ShippedIcons.Unknown, "gfx/invobjs/pansearedfish"));
    }

    [Fact]
    public void Src_NormalizesBeforeLookup()
    {
        // Regression guard: an "f:" prefix is stripped before the shipped-icon lookup.
        Assert.Equal("/gfx/invobjs/lobster.png", FoodIcons.Src(Shipped, "f:gfx/invobjs/lobster"));
    }

    [Fact]
    public void Src_ConcatenatedName_StaysAWholeRemoteAttempt()
    {
        // Two resource paths run together: neither source resolves them; pinned until someone
        // decides whether to cut at the second "gfx/".
        const string name = "gfx/invobjs/meat-crustroastgfx/invobjs/meat-crab";
        Assert.Equal(FoodIcons.RemoteRendererBase + name, FoodIcons.Src(Shipped, name));
        Assert.Null(FoodIcons.RemoteFallback(Shipped, name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("f:")]
    public void Src_EmptyOrUnusableName_RequestsNothing(string? name)
    {
        Assert.Equal(string.Empty, FoodIcons.Src(Shipped, name));
        Assert.Equal(string.Empty, FoodIcons.Src(ShippedIcons.Unknown, name));
        Assert.Null(FoodIcons.RemoteFallback(Shipped, name));
        Assert.Contains("display='none'", FoodIcons.RemoteFallbackOrHide(Shipped, name));
    }

    [Theory]
    [InlineData("gfx/invobjs/lob'ster")]
    [InlineData("gfx/invobjs/lob\"ster")]
    [InlineData("gfx/invobjs/x');alert(1);('")]
    public void OnError_NeverContainsAQuoteFromTheName(string name)
    {
        foreach (var shipped in new[] { Shipped, ShippedIcons.Unknown, ShippedIcons.FromNames(Array.Empty<string>()) })
        {
            var src = FoodIcons.Src(shipped, name);
            Assert.DoesNotContain("'", src);
            Assert.DoesNotContain("\"", src);
            foreach (var handler in new[] { FoodIcons.RemoteFallback(shipped, name), FoodIcons.RemoteFallbackOrHide(shipped, name) })
            {
                if (handler == null) continue;
                // Only the handler's own quotes around the URL: 2 for RemoteFallback/OrHide's src
                // plus the 'none'/'1' literals; never an odd one coming from the name.
                Assert.Equal(0, handler.Count(c => c == '"'));
                Assert.Equal(0, handler.Count(c => c == '\'') % 2);
                Assert.DoesNotContain("alert(", handler);
            }
        }
    }
}
