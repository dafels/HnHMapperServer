using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Limits which formats ImageSharp will recognise when it decodes. The app only ever reads PNG (grid uploads,
/// .hmap and public-map tiles, game resources) and WebP (its own tile pyramid), but <c>Image.Load</c> detects
/// the format from the bytes, so a game client could send a TIFF, BMP, GIF... labelled as a grid PNG and reach
/// that decoder.
///
/// Why this matters: we stay on ImageSharp 3.1.x because every patched release (3.2.0+, 4.1.2+) refuses to
/// build without a Six Labors license key. The 2026-10-07 advisories fixed in 3.2.0 are mostly in the TIFF
/// decoder/encoder (GHSA-jjfr-hcj7-qf5w, GHSA-j9gm-c75j-xc9q, GHSA-v76p-62qx-wwq2, GHSA-wmxv-xphr-5c9g); with
/// no TIFF detector those code paths cannot be reached from uploaded bytes. Not covered: GHSA-gwg2-r3hj-4w44
/// (ICC profile parsing - PNG can carry one) and GHSA-j3p4-wp97-rph4 (HistogramEqualization, which this app
/// never calls). Moving to a patched version with a license key is the real fix.
/// </summary>
public static class ImageFormatPolicy
{
    /// <summary>Recognise only PNG and WebP input; any other format fails with UnknownImageFormatException.</summary>
    public static void RestrictDecodingToPngAndWebp(Configuration configuration)
    {
        configuration.ImageFormatsManager.ClearImageFormatDetectors();
        configuration.ImageFormatsManager.AddImageFormatDetector(new PngImageFormatDetector());
        configuration.ImageFormatsManager.AddImageFormatDetector(new WebpImageFormatDetector());
    }
}
