using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// AtomicFile: the temp-file + rename write extracted from LargeTileService (now also used for
/// grid PNGs and legacy zoom tiles) and the size probe the storage accounting subtracts.
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hnh-atomic-test-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception e) when (e is IOException or DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task WriteAllBytesAsync_ReplacesExistingContent_LeavesNoTmp()
    {
        var path = Path.Combine(_dir, "tile.png");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5 });

        await AtomicFile.WriteAllBytesAsync(path, new byte[] { 9, 8 });

        Assert.Equal(new byte[] { 9, 8 }, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task WriteAllBytesAsync_NewFile_IsCreated()
    {
        var path = Path.Combine(_dir, "new.png");
        await AtomicFile.WriteAllBytesAsync(path, new byte[] { 7 });
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WriteAllBytesAsync_TargetDirectoryMissing_Throws_NoTmpLeft()
    {
        var missingDir = Path.Combine(_dir, "missing");
        await Assert.ThrowsAnyAsync<IOException>(() =>
            AtomicFile.WriteAllBytesAsync(Path.Combine(missingDir, "x.png"), new byte[] { 1 }));
        Assert.False(Directory.Exists(missingDir));
        Assert.Empty(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void LengthOrZero_MissingFile_ReturnsZero_ExistingFile_ReturnsLength()
    {
        var path = Path.Combine(_dir, "sized.bin");
        Assert.Equal(0, AtomicFile.LengthOrZero(path));

        File.WriteAllBytes(path, new byte[1234]);
        Assert.Equal(1234, AtomicFile.LengthOrZero(path));

        Assert.Equal(0, AtomicFile.LengthOrZero(Path.Combine(_dir, "no", "such", "dir.bin")));
    }
}
