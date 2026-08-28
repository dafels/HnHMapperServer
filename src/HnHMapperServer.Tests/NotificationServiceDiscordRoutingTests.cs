using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// End-to-end check that CreateAsync's fire-and-forget Discord block actually routes
/// through DiscordNotificationRouter: cookbook digests hit the cookbook webhook, timer
/// alerts hit the main webhook, and a disabled cookbook channel sends nothing. Uses a
/// real ServiceCollection-backed scope factory (the other suites pass a bare mock and
/// deliberately let the block NRE-swallow), with a TaskCompletionSource in the webhook
/// mock to await the fire-and-forget send deterministically.
/// </summary>
public class NotificationServiceDiscordRoutingTests : IDisposable
{
    private const string TenantId = "discord-route-a";
    private const string TimersUrl = "https://discord.com/api/webhooks/1/timers";
    private const string CookbookUrl = "https://discord.com/api/webhooks/2/cookbook";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;

    public NotificationServiceDiscordRoutingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-discord-route-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        _db.Tenants.Add(new TenantEntity
        {
            Id = TenantId,
            Name = TenantId,
            StorageQuotaMB = 1024,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
        _db.SaveChanges();
    }

    private sealed record Harness(
        NotificationService Service,
        Mock<IDiscordWebhookService> Discord,
        TaskCompletionSource<string> SentUrl,
        TaskCompletionSource<bool> TenantLoaded);

    private Harness BuildHarness(TenantDto tenant)
    {
        var sentUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tenantLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tenantService = new Mock<ITenantService>();
        tenantService
            .Setup(s => s.GetTenantAsync(TenantId))
            .Callback(() => tenantLoaded.TrySetResult(true))
            .ReturnsAsync(tenant);

        var discord = new Mock<IDiscordWebhookService>();
        discord
            .Setup(d => d.SendNotificationAsync(It.IsAny<NotificationDto>(), It.IsAny<string>()))
            .Callback<NotificationDto, string>((_, url) => sentUrl.TrySetResult(url))
            .ReturnsAsync(true);

        var services = new ServiceCollection();
        services.AddScoped(_ => tenantService.Object);
        services.AddScoped(_ => discord.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var service = new NotificationService(
            _db,
            Mock.Of<ILogger<NotificationService>>(),
            scopeFactory,
            new UpdateNotificationService());

        return new Harness(service, discord, sentUrl, tenantLoaded);
    }

    private static TenantDto RoutingTenant(bool cookbookEnabled, string? cookbookUrl) => new()
    {
        Id = TenantId,
        DiscordNotificationsEnabled = true,
        DiscordWebhookUrl = TimersUrl,
        DiscordCookbookNotificationsEnabled = cookbookEnabled,
        DiscordCookbookWebhookUrl = cookbookUrl
    };

    private static CreateNotificationDto Notification(string type) => new()
    {
        TenantId = TenantId,
        Type = type,
        Title = "t",
        Message = "m"
    };

    [Fact]
    public async Task CookbookNotification_SendsToCookbookWebhook()
    {
        var h = BuildHarness(RoutingTenant(cookbookEnabled: true, cookbookUrl: CookbookUrl));

        await h.Service.CreateAsync(Notification("CookbookFoodAdded"));

        var url = await h.SentUrl.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CookbookUrl, url);
    }

    [Fact]
    public async Task TimerNotification_SendsToTimersWebhook_EvenWithCookbookUrlSet()
    {
        var h = BuildHarness(RoutingTenant(cookbookEnabled: true, cookbookUrl: CookbookUrl));

        await h.Service.CreateAsync(Notification("MarkerTimerExpired"));

        var url = await h.SentUrl.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimersUrl, url);
    }

    [Fact]
    public async Task CookbookNotification_WithBlankCookbookUrl_FallsBackToTimersWebhook()
    {
        var h = BuildHarness(RoutingTenant(cookbookEnabled: true, cookbookUrl: null));

        await h.Service.CreateAsync(Notification("CookbookFoodAdded"));

        var url = await h.SentUrl.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimersUrl, url);
    }

    [Fact]
    public async Task CookbookNotification_WithCookbookDisabled_SendsNothing()
    {
        var h = BuildHarness(RoutingTenant(cookbookEnabled: false, cookbookUrl: CookbookUrl));

        await h.Service.CreateAsync(Notification("CookbookFoodAdded"));

        // The routing decision happens right after the tenant loads; give the
        // fire-and-forget block a beat past that point, then assert no send.
        await h.TenantLoaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        h.Discord.Verify(
            d => d.SendNotificationAsync(It.IsAny<NotificationDto>(), It.IsAny<string>()),
            Times.Never);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch (IOException)
            {
                // Temp file cleanup is best-effort.
            }
        }
    }
}
