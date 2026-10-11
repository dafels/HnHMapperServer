using System.Buffers.Binary;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Completeness check for WebP pyramid files. A pyramid file that is not a complete WebP counts
/// as missing everywhere: it is never served, cached or composed into a parent.
/// </summary>
/// <remarks>
/// Builds before the atomic tile writes overwrote tiles in place, and a write that
/// failed half-way (disk full, I/O error) left a zero-length or truncated file behind. Every
/// reader trusted any existing file, so such a file was served as an empty 200, composed into its
/// parent as a transparent hole, or made a force-regeneration delete a valid parent. The RIFF
/// header carries the payload size, so empty and truncated files are detectable from the bytes
/// already in hand, without decoding.
/// </remarks>
public static class WebpTileFile
{
    /// <summary>"RIFF" + little-endian payload size + "WEBP". Anything shorter is not a tile.</summary>
    public const int HeaderLength = 12;

    /// <summary>
    /// True when <paramref name="data"/> starts with a RIFF/WEBP header and is at least as long as
    /// the header says (trailing bytes are tolerated).
    /// </summary>
    public static bool IsComplete(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderLength
        && data[..4].SequenceEqual("RIFF"u8)
        && data.Slice(8, 4).SequenceEqual("WEBP"u8)
        && BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) + 8L <= data.Length;

    /// <summary>
    /// Reads a tile file, or returns null when it vanished after the caller's existence check
    /// (a regeneration in either process may delete it at any time; the data volume is shared).
    /// </summary>
    public static async Task<byte[]?> TryReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
