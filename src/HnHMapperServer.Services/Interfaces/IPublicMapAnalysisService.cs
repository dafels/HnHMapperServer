using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Computes and persists the order-independent pre-merge analysis for a public map so a blind
/// admin can preview how sources will be woven into landmasses (and any conflicts) before
/// committing to a regeneration. Never renders or writes tiles.
/// </summary>
public interface IPublicMapAnalysisService
{
    /// <summary>Recompute the analysis from current source data and persist it. Null if the public
    /// map doesn't exist.</summary>
    Task<PublicMapAnalysisReportDto?> AnalyzeAsync(string publicMapId, CancellationToken cancellationToken = default);

    /// <summary>Return the last persisted analysis, or null if none has been computed yet.</summary>
    Task<PublicMapAnalysisReportDto?> GetStoredAsync(string publicMapId, CancellationToken cancellationToken = default);
}
