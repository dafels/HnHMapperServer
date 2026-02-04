using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service interface for managing the HMap source library
/// </summary>
public interface IHmapSourceService
{
    /// <summary>
    /// Upload a new HMap source file to the library
    /// </summary>
    /// <param name="hmapStream">The HMap file stream</param>
    /// <param name="fileName">Original filename</param>
    /// <param name="name">Display name (defaults to filename if null)</param>
    /// <param name="uploadedBy">User ID who uploaded</param>
    /// <returns>The created HMap source</returns>
    Task<HmapSourceDto> UploadAsync(Stream hmapStream, string fileName, string? name, string? uploadedBy);

    /// <summary>
    /// Get all HMap sources in the library
    /// </summary>
    Task<List<HmapSourceDto>> GetAllAsync();

    /// <summary>
    /// Get a specific HMap source by ID
    /// </summary>
    Task<HmapSourceDto?> GetAsync(int id);

    /// <summary>
    /// Delete an HMap source and its file
    /// </summary>
    Task DeleteAsync(int id);

    /// <summary>
    /// Analyze an HMap source file and update cached analysis data
    /// </summary>
    Task<HmapSourceAnalysisDto> AnalyzeAsync(int sourceId);

    /// <summary>
    /// Re-analyze all HMap sources
    /// </summary>
    Task ReanalyzeAllAsync();

    /// <summary>
    /// Check if an HMap source exists
    /// </summary>
    Task<bool> ExistsAsync(int id);

    /// <summary>
    /// Get the file path for an HMap source
    /// </summary>
    Task<string?> GetFilePathAsync(int id);

    /// <summary>
    /// Check if an HMap source is used by any public maps
    /// </summary>
    Task<bool> IsSourceInUseAsync(int id);

    /// <summary>
    /// Get public maps using a specific HMap source
    /// </summary>
    Task<List<string>> GetPublicMapsUsingSourceAsync(int id);
}
