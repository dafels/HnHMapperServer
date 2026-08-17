using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Cross-tenant map integrity scan for superadmins: finds cells claimed by more than one grid id
/// (the fingerprint of coordinate-frame corruption — two world regions mapped onto the same
/// coordinates) and legacy placeholder ("0") grid rows. Read-only; repair is the wipe-region tool.
/// </summary>
public interface IMapIntegrityService
{
    Task<MapIntegrityReportDto> ScanAsync(CancellationToken ct = default);
}
