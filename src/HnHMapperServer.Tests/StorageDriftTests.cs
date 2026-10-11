using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// The verification job's warning rule: sign-aware and quota-relative (default thresholds).
/// The first two cases are small overcounts that the old fixed 1 MB rule reported as "discrepancy" warnings.
/// </summary>
public class StorageDriftTests
{
    private static readonly StorageDriftThresholds Defaults = new();

    [Theory]
    [InlineData(440.34, 438.32, 1024, StorageDriftKind.None)]       // +2.02 < 5.12
    [InlineData(68.48, 67.11, 1024, StorageDriftKind.None)]         // +1.37
    [InlineData(444, 438, 1024, StorageDriftKind.Overcount)]        // +6 > 0.5% of 1024 = 5.12
    [InlineData(102.5, 100, 100, StorageDriftKind.Overcount)]       // +2.5 > the 2 MB floor
    [InlineData(101.9, 100, 100, StorageDriftKind.None)]            // +1.9 under the floor
    [InlineData(420, 438, 1024, StorageDriftKind.None)]             // -18 vs -20.48 (2% of 1024)
    [InlineData(400, 438, 1024, StorageDriftKind.Undercount)]       // -38
    [InlineData(85, 100, 100, StorageDriftKind.Undercount)]         // -15 past the 10 MB floor
    [InlineData(95, 100, 100, StorageDriftKind.None)]               // -5
    [InlineData(0, 0, 0, StorageDriftKind.None)]
    public void Classify_DefaultThresholds(double tracked, double disk, double quota, StorageDriftKind expected)
    {
        Assert.Equal(expected, StorageDrift.Classify(tracked, disk, quota, Defaults));
    }

    [Fact]
    public void Classify_CustomThresholds()
    {
        var strict = new StorageDriftThresholds(OvercountMinMB: 0.5, OvercountPercentOfQuota: 0,
            UndercountMinMB: 0.5, UndercountPercentOfQuota: 0);
        Assert.Equal(StorageDriftKind.Overcount, StorageDrift.Classify(101, 100, 1024, strict));
        Assert.Equal(StorageDriftKind.Undercount, StorageDrift.Classify(99, 100, 1024, strict));
        Assert.Equal(StorageDriftKind.None, StorageDrift.Classify(100.4, 100, 1024, strict));
    }
}
