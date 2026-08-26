using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Runs the one-time cookbook value repair shortly after startup: every food's headline
/// values are re-derived from the game-client observations already stored for it, undoing
/// the era when the bundled wiki dump outranked the client. A marker row makes later
/// starts a no-op, so this costs one query once the repair has run.
/// </summary>
public class CookbookValueBackfillService : BackgroundService
{
    private const string LogPrefix = "[CookbookValueBackfill]";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CookbookValueBackfillService> _logger;

    public CookbookValueBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<CookbookValueBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let migrations and the startup tile scans settle first; the repair rewrites
        // ~1k food rows per tenant and there is no hurry about it.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var backfill = scope.ServiceProvider.GetRequiredService<ICookbookValueBackfill>();
            var result = await backfill.RunOnceAsync(stoppingToken);

            if (result.AlreadyApplied)
            {
                return;
            }

            _logger.LogInformation(
                "{Prefix} repaired {Updated} of {Foods} foods across {Tenants} tenants "
                + "({Failed} failed, marker written: {Marker})",
                LogPrefix, result.Updated, result.Foods, result.TenantsProcessed,
                result.TenantsFailed, result.MarkerWritten);
        }
        catch (OperationCanceledException)
        {
            // Shutting down before the repair finished — it runs again on the next start.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} failed; it will be retried on the next start", LogPrefix);
        }
    }
}
