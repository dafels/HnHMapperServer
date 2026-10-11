using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// Grid ids come from game clients and become pool file names. ValidatePath alone only keeps a
/// path inside the tenant directory: "../5/0/1_1" stays inside it while naming another map's
/// imported file, which an upload would overwrite, a repair would point a row at and a map delete
/// would remove.
/// </summary>
public class TenantFilePathServiceGridIdTests
{
    [Theory]
    [InlineData("1234567890")]
    [InlineData("-42")]
    [InlineData("grid-a")]
    [InlineData("s1")]
    public void PlainIds_AreSafe_AndMapIntoThePool(string gridId)
    {
        Assert.True(TenantFilePathService.IsSafeGridId(gridId));
        Assert.Equal(Path.Combine("tenants", "t", "grids", $"{gridId}.png"),
            new TenantFilePathService().GetGridRelativePath("t", gridId));
    }

    [Theory]
    [InlineData("../5/0/1_1")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:evil")]
    [InlineData("nul\0byte")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PathLikeIds_AreRejected(string? gridId)
    {
        Assert.False(TenantFilePathService.IsSafeGridId(gridId));
        Assert.Null(TenantFilePathService.PoolRelativePath("t", gridId ?? string.Empty));
    }

    [Fact]
    public void GetGridPaths_ThrowForAnUnsafeId()
    {
        var paths = new TenantFilePathService();

        Assert.Throws<UnauthorizedAccessException>(() => paths.GetGridRelativePath("t", "../5/0/1_1"));
        Assert.Throws<UnauthorizedAccessException>(() => paths.GetGridFilePath("t", "../5/0/1_1", "map"));
    }
}
