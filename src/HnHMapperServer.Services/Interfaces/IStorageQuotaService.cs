namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service for managing tenant storage quotas
/// </summary>
public interface IStorageQuotaService
{
    /// <summary>
    /// Checks if a tenant has enough quota for an upload
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="sizeMB">Size to check in MB</param>
    /// <returns>True if upload is allowed, false if over quota</returns>
    Task<bool> CheckQuotaAsync(string tenantId, double sizeMB);

    /// <summary>
    /// Increments tenant storage usage (atomic operation)
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="sizeMB">Size to add in MB</param>
    Task IncrementStorageUsageAsync(string tenantId, double sizeMB);

    /// <summary>
    /// Decrements tenant storage usage (atomic operation)
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
    /// Recalculates storage usage from filesystem and updates database
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="gridStorage">Base grid storage path</param>
    /// <returns>Recalculated usage in MB</returns>
    Task<double> RecalculateStorageUsageAsync(string tenantId, string gridStorage);

    /// <summary>
    /// Updates storage usage from pre-calculated values (avoids duplicate filesystem scan).
    /// Use this when you've already calculated the directory size to avoid redundant I/O.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="gridStorage">Base grid storage path (for writing .storage.json)</param>
    /// <param name="totalSizeBytes">Pre-calculated total size in bytes</param>
    /// <param name="fileCount">Pre-calculated file count</param>
    Task UpdateStorageFromCalculationAsync(string tenantId, string gridStorage, long totalSizeBytes, int fileCount);
}
