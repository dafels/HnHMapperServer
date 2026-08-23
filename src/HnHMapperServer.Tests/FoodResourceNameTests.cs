using HnHMapperServer.Core.Cookbook;

namespace HnHMapperServer.Tests;

/// <summary>
/// Unit tests for FoodResourceName — the sanitizer that canonicalizes the game-resource
/// name a food is stored under, both on ingestion and when the cookbook table and the
/// notification bell build icon URLs from rows that were ingested before it existed.
/// </summary>
public class FoodResourceNameTests
{
    [Fact]
    public void Normalize_StripsScheme_ForTheReportedBrokenIcon()
    {
        // The live 404: /f:gfx/invobjs/leaf-brassica.png
        Assert.Equal(
            "gfx/invobjs/leaf-brassica",
            FoodResourceName.Normalize("f:gfx/invobjs/leaf-brassica"));
    }

    [Theory]
    [InlineData("f:gfx/invobjs/leaf-fig")]
    [InlineData("F:gfx/invobjs/leaf-fig")]
    [InlineData("file:gfx/invobjs/leaf-fig")]
    [InlineData("File:gfx/invobjs/leaf-fig")]
    [InlineData("FILE:gfx/invobjs/leaf-fig")]
    [InlineData("  f: gfx/invobjs/leaf-fig  ")]
    [InlineData("f:file:gfx/invobjs/leaf-fig")]
    public void Normalize_StripsPrefixes_CaseInsensitivelyAndRepeatedly(string stored)
    {
        Assert.Equal("gfx/invobjs/leaf-fig", FoodResourceName.Normalize(stored));
    }

    [Theory]
    [InlineData("gfx/invobjs/leaf-fig")]
    [InlineData("gfx/invobjs/adderhide-blood")]
    [InlineData("gfx/terobjs/items/coal")]
    public void Normalize_LeavesCleanNamesUntouched(string stored)
    {
        Assert.Equal(stored, FoodResourceName.Normalize(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("f:")]
    [InlineData("file:  ")]
    public void Normalize_ReturnsEmpty_ForMissingOrPrefixOnlyValues(string? stored)
    {
        Assert.Equal(string.Empty, FoodResourceName.Normalize(stored));
    }

    [Fact]
    public void Normalize_StripsLeadingSlashes()
    {
        // "/" + "//evil.example/x" would be a protocol-relative URL to a foreign origin.
        Assert.Equal("gfx/invobjs/leaf-fig", FoodResourceName.Normalize("//gfx/invobjs/leaf-fig"));
        Assert.Equal("gfx/invobjs/leaf-fig", FoodResourceName.Normalize("f:/gfx/invobjs/leaf-fig"));
    }

    [Theory]
    [InlineData("gfx/../../etc/passwd")]
    [InlineData("gfx/invobjs/..")]
    public void Normalize_RejectsDotSegments(string stored)
    {
        Assert.Equal(string.Empty, FoodResourceName.Normalize(stored));
    }

    [Fact]
    public void Normalize_DropsCharactersThatCouldEscapeTheOnErrorScript()
    {
        // Resource names come from game-client uploads, and the icon fallbacks
        // interpolate them into a JS string literal inside an onerror attribute.
        var sanitized = FoodResourceName.Normalize("gfx/x';alert(1);'");

        Assert.DoesNotContain("'", sanitized);
        Assert.Equal("gfx/xalert1", sanitized);
    }

    [Theory]
    [InlineData("gfx/x\"y")]
    [InlineData("gfx/x\\y")]
    [InlineData("gfx/x<y>")]
    [InlineData("gfx/x y")]
    public void Normalize_DropsQuotesBackslashesAnglesAndSpaces(string stored)
    {
        Assert.Equal("gfx/xy", FoodResourceName.Normalize(stored));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = FoodResourceName.Normalize("f:gfx/invobjs/leaf-brassica");
        Assert.Equal(once, FoodResourceName.Normalize(once));
    }

    [Fact]
    public void Normalize_PreservesEveryShippedIconName()
    {
        // The safe-set filter must be lossless for real data: every icon shipped under
        // wwwroot/gfx is a bare path, so normalizing one must be a no-op.
        var shipped = new[]
        {
            "gfx/invobjs/leaf-conkertree",
            "gfx/invobjs/adderhide-frame",
            "gfx/invobjs/animalfat-r",
            "gfx/invobjs/fourleafclover",
            "gfx/invobjs/perfectautumnleaf"
        };

        foreach (var name in shipped)
        {
            Assert.Equal(name, FoodResourceName.Normalize(name));
        }
    }
}
