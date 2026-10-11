using System.Diagnostics;
using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that removes stale character positions every 10 seconds
/// Matches the Go implementation: cleanChars() goroutine
/// Multi-tenancy: Cleans stale characters for all active tenants
/// </summary>
public class CharacterCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CharacterCleanupService> _logger;

    public CharacterCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<CharacterCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Randomized startup delay to prevent all services starting simultaneously
        var startupDelay = TimeSpan.FromSeconds(Random.Shared.Next(0, 60));
        _logger.LogInformation("Character Cleanup Service starting in {Delay:F1}s", startupDelay.TotalSeconds);
        await Task.Delay(startupDelay, stoppingToken);

        _logger.LogInformation("Character Cleanup Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                _logger.LogDebug("Character cleanup job started");

                using var scope = _scopeFactory.CreateScope();
                var characterService = scope.ServiceProvider.GetRequiredService<ICharacterService>();
                var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

                // Get all active tenants
                var tenants = await tenantService.GetAllTenantsAsync();

                // Remove stale characters (older than 10 seconds) for each tenant
                foreach (var tenant in tenants.Where(t => t.IsActive))
                {
                    characterService.CleanupStaleCharacters(TimeSpan.FromSeconds(10), tenant.Id);
                }

                sw.Stop();
                _logger.LogDebug("Character cleanup job completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Error in character cleanup service after {ElapsedMs}ms", sw.ElapsedMilliseconds);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("Character Cleanup Service stopped");
    }
}
