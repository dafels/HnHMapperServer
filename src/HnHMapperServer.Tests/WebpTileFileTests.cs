using HnHMapperServer.Services.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace HnHMapperServer.Tests;

/// <summary>
/// The completeness check every WebP pyramid reader applies: empty and truncated files (residue
/// of in-place writes interrupted by a full disk) must count as missing, while
/// every tile the encoder produces passes.
/// </summary>
public class WebpTileFileTests
{
    private static byte[] EncodeTile()
    {
        using var img = new Image<Rgba32>(400, 400, new Rgba32(90, 140, 60, 255));
        using var ms = new MemoryStream();
        img.SaveAsWebp(ms, new WebpEncoder { Quality = 85 });
        return ms.ToArray();
    }

    [Fact]
    public void IsComplete_AcceptsEncodedTile_AndTrailingBytes()
    {
        var tile = EncodeTile();

        Assert.True(WebpTileFile.IsComplete(tile));
        Assert.True(WebpTileFile.IsComplete(tile.Concat(new byte[] { 0, 0, 0 }).ToArray()));
    }

    [Fact]
    public void IsComplete_RejectsEmptyShortAndTruncatedFiles()
    {
        var tile = EncodeTile();

        Assert.False(WebpTileFile.IsComplete(Array.Empty<byte>()));
        Assert.False(WebpTileFile.IsComplete(tile[..11]));
        Assert.False(WebpTileFile.IsComplete(tile[..40]));
        Assert.False(WebpTileFile.IsComplete(tile[..^1]));
        Assert.False(WebpTileFile.IsComplete(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void IsComplete_RejectsOtherFormatsAndOversizedHeader()
    {
        using var img = new Image<Rgba32>(4, 4);
        using var png = new MemoryStream();
        img.SaveAsPng(png);
        Assert.False(WebpTileFile.IsComplete(png.ToArray()));

        // RIFF/WEBP header claiming 1000 payload bytes on a 16-byte file.
        var header = "RIFF"u8.ToArray()
            .Concat(BitConverter.GetBytes(1000u))
            .Concat("WEBP"u8.ToArray())
            .Concat(new byte[4])
            .ToArray();
        Assert.False(WebpTileFile.IsComplete(header));
    }

    [Fact]
    public async Task TryReadAsync_ReturnsNullForVanishedFiles_AndBytesOtherwise()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hnh-webpfile-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(await WebpTileFile.TryReadAsync(Path.Combine(dir, "missing.webp")));
            Assert.Null(await WebpTileFile.TryReadAsync(Path.Combine(dir, "no-such-dir", "x.webp")));

            var path = Path.Combine(dir, "tile.webp");
            await File.WriteAllBytesAsync(path, new byte[] { 7, 8, 9 });
            Assert.Equal(new byte[] { 7, 8, 9 }, await WebpTileFile.TryReadAsync(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
