namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service interface for generating public map tiles
/// </summary>
public interface IPublicMapGenerationService
{
    /// <summary>
    /// Start tile generation for a public map
    /// </summary>
    /// <param name="publicMapId">The public map ID</param>
    /// <returns>True if generation started, false if already running</returns>
    Task<bool> StartGenerationAsync(string publicMapId);

    /// <summary>
    /// Check if a generation is currently running for a public map
    /// </summary>
    Task<bool> IsGenerationRunningAsync(string publicMapId);

    /// <summary>
    /// Queue a public map for generation (used by background service)
    /// </summary>
    void QueueGeneration(string publicMapId);

    /// <summary>
    /// Get the next queued public map ID, or null if queue is empty
    /// </summary>
    string? DequeueGeneration();

    /// <summary>
    /// Check if there are any queued generations
    /// </summary>
    bool HasQueuedGenerations();
}
