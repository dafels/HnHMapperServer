using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>ShippedIconScanner: the wwwroot/gfx scan behind the ShippedIcons manifest (temp dir).</summary>
public class ShippedIconScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hnh-icons-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or DirectoryNotFoundException) { }
    }

    private void Touch(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { _root }.Concat(relativeParts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1 });
    }

    [Fact]
    public void Scan_IndexesEveryPngUnderGfx_AsExtensionlessForwardSlashNames()
    {
        Touch("gfx", "invobjs", "lobster.png");
        Touch("gfx", "terobjs", "mm", "bushes", "holly.png");

        var icons = ShippedIconScanner.Scan(_root);

        Assert.True(icons.IsKnown);
        Assert.Equal(2, icons.Count);
        Assert.True(icons.MayContain("gfx/invobjs/lobster"));
        Assert.True(icons.MayContain("gfx/terobjs/mm/bushes/holly"));
    }

    [Fact]
    public void Scan_IgnoresNonPngAndFilesOutsideGfx()
    {
        Touch("gfx", "invobjs", "lobster.png");
        Touch("favicon.png");
        Touch("gfx", "invobjs", "notes.txt");

        var icons = ShippedIconScanner.Scan(_root);

        Assert.Equal(1, icons.Count);
        Assert.False(icons.MayContain("favicon"));
        Assert.False(icons.MayContain("gfx/invobjs/notes"));
    }

    [Fact]
    public void Scan_NullPath_ReturnsUnknown()
    {
        Assert.False(ShippedIconScanner.Scan(null).IsKnown);
    }

    [Fact]
    public void Scan_MissingPath_ReturnsUnknown()
    {
        var icons = ShippedIconScanner.Scan(Path.Combine(_root, "nope"));
        Assert.False(icons.IsKnown);
        Assert.True(icons.MayContain("gfx/x"));
    }

    [Fact]
    public void Scan_EmptyGfx_ReturnsUnknown()
    {
        Directory.CreateDirectory(Path.Combine(_root, "gfx"));
        Assert.False(ShippedIconScanner.Scan(_root).IsKnown);
    }
}
