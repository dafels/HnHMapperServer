using HnHMapperServer.Core.Models;

namespace HnHMapperServer.Core.Interfaces;

public interface IMarkerRepository
{
    Task<Marker?> GetMarkerAsync(int markerId);
    Task<Marker?> GetMarkerByKeyAsync(string key);
    Task<List<Marker>> GetAllMarkersAsync();
    Task SaveMarkerAsync(Marker marker, string key);
    Task DeleteMarkerAsync(string key);
    Task<int> GetNextMarkerIdAsync();

    /// <summary>
    /// Efficiently saves multiple markers in a single transaction.
    /// Only inserts markers that don't already exist (by key).
    /// </summary>
    /// <returns>Number of markers actually inserted</returns>
    Task<int> SaveMarkersBatchAsync(List<(Marker marker, string key)> markers);

    /// <summary>
    /// Gets all markers for a specific tenant (explicit filtering for background services).
    /// Use this when you need tenant-filtered queries without HTTP context.
    /// </summary>
    Task<List<Marker>> GetMarkersByTenantAsync(string tenantId);

    /// <summary>
    /// Updates readiness status for multiple markers in a single transaction.
    /// More efficient than calling SaveMarkerAsync in a loop.
    /// </summary>
    /// <returns>Number of markers actually updated</returns>
    Task<int> BatchUpdateReadinessAsync(List<(int markerId, bool ready, long maxReady, long minReady)> updates, string tenantId);
}
