namespace HnHMapperServer.Services.Services;

/// <summary>Which way the tracked storage figure is off from the disk, if it matters.</summary>
public enum StorageDriftKind
{
    None,
    /// <summary>Tracked above disk: a write path charges bytes that are not on disk (can 413 a tenant early).</summary>
    Overcount,
    /// <summary>Tracked below disk: a write path is not charged (expected up to the WebP pyramid's growth).</summary>
    Undercount
}

/// <summary>
/// When a reconciliation's drift deserves a warning. Sign-aware: only an overcount can hurt
/// (premature 413s near the quota), so it gets a tight limit relative to the quota; an undercount
/// is expected (the WebP pyramid is reconciled-only) and gets a loose one. Each limit is the
/// larger of a floor in MB and a share of the quota. Config keys StorageVerification:*.
/// </summary>
public sealed record StorageDriftThresholds(
    double OvercountMinMB = 2,
    double OvercountPercentOfQuota = 0.5,
    double UndercountMinMB = 10,
    double UndercountPercentOfQuota = 2);

/// <summary>Pure classifier for the 6-hourly storage verification (testable without the Api).</summary>
public static class StorageDrift
{
    public static StorageDriftKind Classify(double trackedMB, double diskMB, double quotaMB, StorageDriftThresholds thresholds)
    {
        var drift = trackedMB - diskMB;
        if (drift > Math.Max(thresholds.OvercountMinMB, quotaMB * thresholds.OvercountPercentOfQuota / 100))
            return StorageDriftKind.Overcount;
        if (-drift > Math.Max(thresholds.UndercountMinMB, quotaMB * thresholds.UndercountPercentOfQuota / 100))
            return StorageDriftKind.Undercount;
        return StorageDriftKind.None;
    }
}
