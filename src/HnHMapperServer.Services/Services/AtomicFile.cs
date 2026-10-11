namespace HnHMapperServer.Services.Services;

/// <summary>
/// File helpers for tile files that readers may be serving while they are replaced.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes a tile so readers only ever see the old file or the complete new one: the bytes go
    /// to a temp file in the same directory, then a rename replaces the target (atomic on Linux;
    /// a reader holding the old file keeps reading the old inode). Overwriting in place let a
    /// viewer request landing mid-write read a truncated WebP, and a write that failed half-way
    /// destroyed the only copy of a grid PNG. The temp name does not end in .webp/.png, so pyramid
    /// enumeration/deletion and the integrity scan's *.png listings never pick it up.
    /// The target directory must exist.
    /// </summary>
    public static async Task WriteAllBytesAsync(string path, byte[] bytes)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    // Windows refuses to replace a file another handle has open; retry briefly.
                    await Task.Delay(25 * (attempt + 1));
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Size of the file on disk, 0 when it does not exist (or vanished meanwhile). What a
    /// replacement must subtract so the storage quota moves by the change on disk.
    /// </summary>
    public static long LengthOrZero(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
