namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service for managing tenant storage quotas.
///
/// Contract: <c>Tenants.CurrentStorageMB</c> is kept incrementally by single atomic delta
/// UPDATEs (<see cref="IncrementStorageUsageAsync"/>), each charging the change in bytes on
/// disk (new size minus the size of the file it replaced), and reset to the truth by
/// <see cref="ReconcileStorageUsageAsync"/> (a full scan of <c>tenants/{t}/</c>), which the
/// 6-hourly verification job and every admin teardown run. The WebP pyramid
/// (<c>tenants/{t}/large/**</c>) is reconciled-only: its writes are never charged and its deletes
/// never credited, because every delete path is followed by uncharged regeneration.
/// </summary>
public interface IStorageQuotaService
{
    /// <summary>
    /// Checks if a tenant has enough quota for an upload that grows its storage by
    /// <paramref name="sizeMB"/> (pass the growth, not the file size, for a replacement).
    /// Reads the row, never a tracked copy.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="sizeMB">Size to check in MB</param>
    /// <returns>True if upload is allowed, false if over quota</returns>
    Task<bool> CheckQuotaAsync(string tenantId, double sizeMB);

    /// <summary>
    /// Adds <paramref name="sizeMB"/> (may be negative) to the tenant's usage in ONE atomic UPDATE,
    /// clamped at 0. Callers pass the change in bytes on disk (new minus replaced). The UPDATE
    /// neither refreshes a tenant entity tracked in the same scope nor saves the caller's pending
    /// changes. Throws when the tenant does not exist.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="sizeMB">Size to add in MB (negative to subtract)</param>
    Task IncrementStorageUsageAsync(string tenantId, double sizeMB);

    /// <summary>
    /// <see cref="IncrementStorageUsageAsync"/> with the sign flipped.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="sizeMB">Size to subtract in MB</param>
    Task DecrementStorageUsageAsync(string tenantId, double sizeMB);

    /// <summary>
    /// Gets the current storage usage for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>Current usage in MB</returns>
    Task<double> GetCurrentUsageAsync(string tenantId);

    /// <summary>
    /// Gets the storage quota limit for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>Quota limit in MB</returns>
    Task<double> GetQuotaLimitAsync(string tenantId);

    /// <summary>
    /// Recalculates storage usage from the filesystem and writes it to the database
    /// (<see cref="ReconcileStorageUsageAsync"/> without the drift report).
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="gridStorage">Base grid storage path</param>
    /// <returns>Recalculated usage in MB</returns>
    Task<double> RecalculateStorageUsageAsync(string tenantId, string gridStorage);

    /// <summary>
    /// The authoritative reset: reads the tracked value fresh from the row, scans every file under
    /// <c>tenants/{t}/</c>, sets the absolute value atomically (and on a tenant entity tracked in
    /// this scope, left Unchanged), writes .storage.json and logs one Information line with the
    /// signed drift. Never warns — classifying the drift is the caller's job (StorageDrift).
    /// Throws when the tenant does not exist.
    /// </summary>
    Task<StorageReconciliation> ReconcileStorageUsageAsync(string tenantId, string gridStorage);
}

/// <summary>
/// One reconciliation: the tracked value just before it (<paramref name="PreviousMB"/>), the disk
/// total it was reset to (<paramref name="CurrentMB"/>), the quota and the files counted.
/// </summary>
public sealed record StorageReconciliation(double PreviousMB, double CurrentMB, int QuotaMB, int FileCount)
{
    /// <summary>Tracked minus disk: positive = overcount (a write path charges bytes that are not on disk).</summary>
    public double DriftMB => PreviousMB - CurrentMB;
}
