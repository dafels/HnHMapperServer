using HnHMapperServer.Core.Models;

namespace HnHMapperServer.Services.Interfaces;

public interface IMarkerService
{
    /// <summary>
    /// Gets all markers with grid location information for frontend
    /// </summary>
    Task<List<FrontendMarker>> GetAllFrontendMarkersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates marker readiness times
    /// </summary>
    Task UpdateMarkerReadyTimeAsync(string gridId, int x, int y, long maxReady, long minReady);

    /// <summary>
    /// Updates marker ready status
    /// </summary>
    Task UpdateMarkerAsync(string gridId, int x, int y, string name, string image, bool ready);

    /// <summary>
    /// Bulk uploads markers
    /// </summary>
    Task BulkUploadMarkersAsync(List<(string GridId, int X, int Y, string Name, string Image)> markers);

    /// <summary>
    /// Deletes markers
    /// </summary>
    Task DeleteMarkersAsync(List<(string GridId, int X, int Y)> markers);

    /// <summary>
    /// Hides a marker
    /// </summary>
    Task HideMarkerAsync(int markerId);

    /// <summary>
    /// Deletes a marker by ID
    /// </summary>
    Task DeleteMarkerByIdAsync(int markerId);

    /// <summary>
    /// Updates readiness for all markers (background task)
    /// </summary>
    Task UpdateReadinessOnMarkersAsync(string tenantId);

    /// <summary>
    /// Cleans up orphaned markers (markers with missing grids).
    /// Returns the count of markers deleted.
    /// </summary>
    Task<int> CleanupOrphanedMarkersAsync(string tenantId);
}
