using System.Diagnostics;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Api.BackgroundServices;

/// <summary>
/// Background service that handles queued and scheduled public map tile generation
/// Runs every 30 seconds to check for work
/// </summary>
public class PublicMapGenerationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PublicMapGenerationBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    public PublicMapGenerationBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<PublicMapGenerationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Randomized startup delay to prevent all services starting simultaneously
        var startupDelay = TimeSpan.FromSeconds(Random.Shared.Next(5, 30));
        _logger.LogInformation("Public Map Generation Service starting in {Delay:F1}s", startupDelay.TotalSeconds);
        await Task.Delay(startupDelay, stoppingToken);

        _logger.LogInformation("Public Map Generation Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueuedGenerationsAsync(stoppingToken);
                await ProcessScheduledGenerationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Public Map Generation Service");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("Public Map Generation Service stopped");
    }

    private async Task ProcessQueuedGenerationsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var generationService = scope.ServiceProvider.GetRequiredService<IPublicMapGenerationService>();

        // Process queued generations
        while (generationService.HasQueuedGenerations() && !stoppingToken.IsCancellationRequested)
        {
            var publicMapId = generationService.DequeueGeneration();
            if (publicMapId != null)
            {
                var sw = Stopwatch.StartNew();
                _logger.LogInformation("Processing queued generation for public map {PublicMapId}", publicMapId);

                var success = await generationService.StartGenerationAsync(publicMapId);

                sw.Stop();
                if (success)
                {
                    _logger.LogInformation("Queued generation for public map {PublicMapId} completed in {ElapsedMs}ms",
                        publicMapId, sw.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogWarning("Queued generation for public map {PublicMapId} failed after {ElapsedMs}ms",
                        publicMapId, sw.ElapsedMilliseconds);
                }
            }
        }
    }

    private async Task ProcessScheduledGenerationsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var generationService = scope.ServiceProvider.GetRequiredService<IPublicMapGenerationService>();

        // Find public maps that need scheduled regeneration
        var now = DateTime.UtcNow;

        var scheduledMaps = await dbContext.PublicMaps
            .Where(p => p.IsActive
                     && p.AutoRegenerate
                     && p.RegenerateIntervalMinutes.HasValue
                     && p.GenerationStatus != "running")
            .ToListAsync(stoppingToken);

        foreach (var publicMap in scheduledMaps)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            // Check if it's time to regenerate
            var intervalMinutes = publicMap.RegenerateIntervalMinutes ?? 60;
            var lastGenerated = publicMap.LastGeneratedAt ?? publicMap.CreatedAt;
            var nextGenerationTime = lastGenerated.AddMinutes(intervalMinutes);

            if (now >= nextGenerationTime)
            {
                // Check if not already running
                if (!await generationService.IsGenerationRunningAsync(publicMap.Id))
                {
                    var sw = Stopwatch.StartNew();
                    _logger.LogInformation("Starting scheduled generation for public map {PublicMapId} (interval: {Interval} minutes)",
                        publicMap.Id, intervalMinutes);

                    var success = await generationService.StartGenerationAsync(publicMap.Id);

                    sw.Stop();
                    if (success)
                    {
                        _logger.LogInformation("Scheduled generation for public map {PublicMapId} completed in {ElapsedMs}ms",
                            publicMap.Id, sw.ElapsedMilliseconds);
                    }
                    else
                    {
                        _logger.LogWarning("Scheduled generation for public map {PublicMapId} failed after {ElapsedMs}ms",
                            publicMap.Id, sw.ElapsedMilliseconds);
                    }
                }
            }
        }
    }
}
