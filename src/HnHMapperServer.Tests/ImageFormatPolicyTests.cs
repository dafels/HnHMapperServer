using HnHMapperServer.Services.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace HnHMapperServer.Tests;

/// <summary>
/// The decode policy both Program.cs files apply to Configuration.Default. Tested on fresh configurations:
/// Configuration.Clone() shares the ImageFormatsManager, so restricting a clone would restrict Default for every
/// other test in the process.
/// </summary>
public class ImageFormatPolicyTests
{
    private static Configuration Unrestricted() => new(
        new PngConfigurationModule(), new WebpConfigurationModule(),
        new TiffConfigurationModule(), new BmpConfigurationModule());

    private static Configuration Restricted()
    {
        var configuration = Unrestricted();
        ImageFormatPolicy.RestrictDecodingToPngAndWebp(configuration);
        return configuration;
    }

    private static byte[] Encode(IImageEncoder encoder)
    {
        using var image = new Image<Rgba32>(4, 4, new Rgba32(10, 20, 30, 255));
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    public static TheoryData<string> AllowedFormats => new() { "png", "webp" };
    public static TheoryData<string> RefusedFormats => new() { "tiff", "bmp" };

    private static IImageEncoder EncoderFor(string format) => format switch
    {
        "png" => new PngEncoder(),
        "webp" => new WebpEncoder(),
        "tiff" => new TiffEncoder(),
        "bmp" => new BmpEncoder(),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    [Theory]
    [MemberData(nameof(AllowedFormats))]
    public void Png_and_webp_still_decode(string format)
    {
        var options = new DecoderOptions { Configuration = Restricted() };

        using var image = Image.Load<Rgba32>(options, Encode(EncoderFor(format)));

        Assert.Equal(4, image.Width);
    }

    [Theory]
    [MemberData(nameof(RefusedFormats))]
    public void Other_formats_are_refused(string format)
    {
        var bytes = Encode(EncoderFor(format));
        var options = new DecoderOptions { Configuration = Restricted() };

        // Sanity: the same configuration without the policy does decode these bytes.
        using (Image.Load<Rgba32>(new DecoderOptions { Configuration = Unrestricted() }, bytes)) { }

        Assert.Throws<UnknownImageFormatException>(() => Image.Load<Rgba32>(options, bytes));
    }
}
