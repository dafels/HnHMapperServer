using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Proactive integrity monitoring: runs the cross-tenant map integrity scan on an interval and
/// logs a WARNING summary whenever something is wrong (contested cells, placeholder rows, or
/// orphaned storage), so problems surface in the log/observability stack without anyone opening
/// the Map Integrity tab. Detection only — repair stays a human decision in the superadmin panel.
/// </summary>
public class MapIntegrityCheckService : BackgroundService
{
    private const string LogPrefix = "[IntegrityCheck]";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MapIntegrityCheckService> _logger;

    public MapIntegrityCheckService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<MapIntegrityCheckService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("IntegrityCheck:Enabled", true))
        {
            _logger.LogInformation("{Prefix} disabled via configuration", LogPrefix);
            return;
        }

        var intervalHours = _configuration.GetValue("IntegrityCheck:IntervalHours", 24.0);

        // Let startup work (tile scans, migrations) settle before the first pass.
        await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);

        _logger.LogInformation("{Prefix} started (every {Hours:F0}h)", LogPrefix, intervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var integrityService = scope.ServiceProvider.GetRequiredService<IMapIntegrityService>();
                var report = await integrityService.ScanAsync(stoppingToken);

                if (report.IsClean)
                {
                    _logger.LogInformation(
                        "{Prefix} clean: {Tenants} tenants, {Grids} grids, no issues",
                        LogPrefix, report.TenantsScanned, report.TotalGrids);
                }
                else
                {
                    var reclaimableMB = report.OrphanStorage
                        .Sum(o => (o.DeadMapDirectoryBytes + o.UnreferencedGridFileBytes)) / 1024.0 / 1024.0;
                    var worstContested = report.ContestedMaps.FirstOrDefault();

                    _logger.LogWarning(
                        "{Prefix} ISSUES FOUND: {ContestedMaps} map(s) with contested cells{WorstContested}, " +
                        "{Placeholders} placeholder row(s), {OrphanTenants} tenant(s) with orphaned storage " +
                        "(~{ReclaimMB:F0} MB reclaimable). Repair via SuperAdmin -> Map Integrity.",
                        LogPrefix,
                        report.ContestedMaps.Count,
                        worstContested != null
                            ? $" (worst: map {worstContested.MapId} '{worstContested.MapName}' with {worstContested.ContestedCellCount})"
                            : string.Empty,
                        report.PlaceholderRows.Count,
                        report.OrphanStorage.Count,
                        reclaimableMB);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Prefix} scan failed", LogPrefix);
            }

            await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
        }
    }
}
