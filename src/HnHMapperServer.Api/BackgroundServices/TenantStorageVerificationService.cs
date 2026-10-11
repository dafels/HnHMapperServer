using System.Diagnostics;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that periodically resets every active tenant's tracked storage usage to the
/// filesystem total (the authoritative reconciliation) and warns only when the drift it corrected
/// could matter: an overcount above max(2 MB, 0.5% of quota) or an undercount below
/// -max(10 MB, 2% of quota) (StorageDrift; keys StorageVerification:*). Runs every 6 hours.
/// </summary>
public class TenantStorageVerificationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TenantStorageVerificationService> _logger;
    private readonly string _gridStorage;
    private readonly TimeSpan _verificationInterval;
    private readonly TimeSpan _firstRunDelay;
    private readonly StorageDriftThresholds _thresholds;

    public TenantStorageVerificationService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<TenantStorageVerificationService> _logger)
    {
        _serviceProvider = serviceProvider;
        this._logger = _logger;
        _gridStorage = configuration["GridStorage"] ?? "map";

        // Default: 6 hours (configurable)
        var intervalHours = configuration.GetValue<int>("StorageVerification:IntervalHours", 6);
        _verificationInterval = TimeSpan.FromHours(intervalHours);

        // Let the system stabilize after startup before the first scan (default 1 hour).
        _firstRunDelay = TimeSpan.FromMinutes(configuration.GetValue<double>("StorageVerification:StartupDelayMinutes", 60));

        var defaults = new StorageDriftThresholds();
        _thresholds = new StorageDriftThresholds(
            configuration.GetValue("StorageVerification:OvercountWarnMinMB", defaults.OvercountMinMB),
            configuration.GetValue("StorageVerification:OvercountWarnPercentOfQuota", defaults.OvercountPercentOfQuota),
            configuration.GetValue("StorageVerification:UndercountWarnMinMB", defaults.UndercountMinMB),
            configuration.GetValue("StorageVerification:UndercountWarnPercentOfQuota", defaults.UndercountPercentOfQuota));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Randomized startup delay to prevent all services starting simultaneously
        var startupDelay = TimeSpan.FromSeconds(Random.Shared.Next(0, 60));
        _logger.LogInformation("TenantStorageVerificationService starting in {Delay:F1}s", startupDelay.TotalSeconds);
        await Task.Delay(startupDelay, stoppingToken);

        _logger.LogInformation("TenantStorageVerificationService started. Interval: {Interval}, first run in {FirstRun}",
            _verificationInterval, _firstRunDelay);

        await Task.Delay(_firstRunDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await VerifyAllTenantsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during storage verification");
            }

            // Wait for next verification cycle
            await Task.Delay(_verificationInterval, stoppingToken);
        }

        _logger.LogInformation("TenantStorageVerificationService stopped");
    }

    private async Task VerifyAllTenantsAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Storage verification job started");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var quotaService = scope.ServiceProvider.GetRequiredService<IStorageQuotaService>();

        // Ids only: nothing tracked, so no stale snapshot of a tenant row lives in this scope.
        var tenantIds = await db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.IsActive)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        _logger.LogDebug("Starting storage verification for {Count} active tenants", tenantIds.Count);

        int overcounts = 0, undercounts = 0;
        (string? TenantId, double DriftMB) maxOver = (null, 0), maxUnder = (null, 0);

        foreach (var tenantId in tenantIds)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var r = await quotaService.ReconcileStorageUsageAsync(tenantId, _gridStorage);
                if (r.DriftMB > maxOver.DriftMB) maxOver = (tenantId, r.DriftMB);
                if (r.DriftMB < maxUnder.DriftMB) maxUnder = (tenantId, r.DriftMB);

                switch (StorageDrift.Classify(r.PreviousMB, r.CurrentMB, r.QuotaMB, _thresholds))
                {
                    case StorageDriftKind.Overcount:
                        overcounts++;
                        _logger.LogWarning(
                            "Storage overcount for tenant {TenantId}: tracked {TrackedMB:F2}MB vs disk {DiskMB:F2}MB (+{DriftMB:F2}MB, quota {QuotaMB}MB) - a write path charges bytes that are not on disk",
                            tenantId, r.PreviousMB, r.CurrentMB, r.DriftMB, r.QuotaMB);
                        break;
                    case StorageDriftKind.Undercount:
                        undercounts++;
                        _logger.LogWarning(
                            "Storage undercount for tenant {TenantId}: tracked {TrackedMB:F2}MB vs disk {DiskMB:F2}MB ({DriftMB:F2}MB, quota {QuotaMB}MB) - a write path is not charged",
                            tenantId, r.PreviousMB, r.CurrentMB, r.DriftMB, r.QuotaMB);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify storage for tenant {TenantId}", tenantId);
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "Storage verification job completed in {ElapsedMs}ms for {Count} tenants: {Overcounts} overcount, {Undercounts} undercount warning(s); " +
            "max overcount {MaxOverMB:F2}MB ({MaxOverTenant}), max undercount {MaxUnderMB:F2}MB ({MaxUnderTenant})",
            sw.ElapsedMilliseconds, tenantIds.Count, overcounts, undercounts,
            maxOver.DriftMB, maxOver.TenantId ?? "-", maxUnder.DriftMB, maxUnder.TenantId ?? "-");
    }
}
