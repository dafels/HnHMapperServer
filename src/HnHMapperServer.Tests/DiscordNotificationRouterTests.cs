using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// Pure routing rules for the two Discord channels: cookbook digests go to the
/// cookbook webhook (falling back to the timer webhook only while that channel is
/// live), everything else — including unknown future types — stays on the timer
/// webhook, which doubles as the HMAC signing key for map previews and must never
/// be swapped out for timer notifications.
/// </summary>
public class DiscordNotificationRouterTests
{
    private const string TimersUrl = "https://discord.com/api/webhooks/1/timers";
    private const string CookbookUrl = "https://discord.com/api/webhooks/2/cookbook";

    private static TenantDto Tenant(
        bool timersEnabled = false, string? timersUrl = null,
        bool cookbookEnabled = false, string? cookbookUrl = null) => new()
    {
        Id = "t1",
        DiscordNotificationsEnabled = timersEnabled,
        DiscordWebhookUrl = timersUrl,
        DiscordCookbookNotificationsEnabled = cookbookEnabled,
        DiscordCookbookWebhookUrl = cookbookUrl
    };

    [Theory]
    [InlineData("MarkerTimerExpired", DiscordNotificationChannel.Timers)]
    [InlineData("StandaloneTimerExpired", DiscordNotificationChannel.Timers)]
    [InlineData("TimerPreExpiryWarning", DiscordNotificationChannel.Timers)]
    [InlineData("CookbookFoodAdded", DiscordNotificationChannel.Cookbook)]
    [InlineData("SomeFutureType", DiscordNotificationChannel.Timers)]
    public void GetChannel_MapsTypeToChannel(string type, DiscordNotificationChannel expected)
    {
        Assert.Equal(expected, DiscordNotificationRouter.GetChannel(type));
    }

    [Fact]
    public void Timers_EnabledWithUrl_ResolvesUrl()
    {
        var tenant = Tenant(timersEnabled: true, timersUrl: TimersUrl);
        Assert.Equal(TimersUrl, DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Timers));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Timers_EnabledWithBlankUrl_ResolvesNull(string? url)
    {
        var tenant = Tenant(timersEnabled: true, timersUrl: url);
        Assert.Null(DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Timers));
    }

    [Fact]
    public void Timers_DisabledWithUrl_ResolvesNull()
    {
        var tenant = Tenant(timersEnabled: false, timersUrl: TimersUrl);
        Assert.Null(DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Timers));
    }

    [Fact]
    public void Cookbook_EnabledWithOwnUrl_ResolvesCookbookUrl_NeverTimers()
    {
        var tenant = Tenant(timersEnabled: true, timersUrl: TimersUrl, cookbookEnabled: true, cookbookUrl: CookbookUrl);
        Assert.Equal(CookbookUrl, DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Cookbook));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Cookbook_EnabledWithBlankOwnUrl_FallsBackToTimersUrl(string? cookbookUrl)
    {
        var tenant = Tenant(timersEnabled: true, timersUrl: TimersUrl, cookbookEnabled: true, cookbookUrl: cookbookUrl);
        Assert.Equal(TimersUrl, DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Cookbook));
    }

    [Fact]
    public void Cookbook_FallbackRequiresTimersChannelEnabled()
    {
        // Timers URL saved but timers channel switched off → the fallback must not
        // resurrect cookbook pings through a disabled channel.
        var tenant = Tenant(timersEnabled: false, timersUrl: TimersUrl, cookbookEnabled: true, cookbookUrl: null);
        Assert.Null(DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Cookbook));
    }

    [Fact]
    public void Cookbook_EnabledWithBothUrlsBlank_ResolvesNull()
    {
        var tenant = Tenant(timersEnabled: true, cookbookEnabled: true);
        Assert.Null(DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Cookbook));
    }

    [Fact]
    public void Cookbook_DisabledWithOwnUrl_ResolvesNull()
    {
        var tenant = Tenant(timersEnabled: true, timersUrl: TimersUrl, cookbookEnabled: false, cookbookUrl: CookbookUrl);
        Assert.Null(DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Cookbook));
    }

    [Fact]
    public void Cookbook_IndependentOfTimersToggle_WhenOwnUrlSet()
    {
        // Requirement: cookbook pings work while the timer channel is fully off.
        var tenant = Tenant(timersEnabled: false, timersUrl: null, cookbookEnabled: true, cookbookUrl: CookbookUrl);
        Assert.Equal(CookbookUrl, DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Cookbook));
    }

    [Fact]
    public void RequireEnabledFalse_IgnoresToggles_ForBothChannels()
    {
        // The test endpoint semantics: a saved URL is testable before the channel is on.
        var tenant = Tenant(timersEnabled: false, timersUrl: TimersUrl, cookbookEnabled: false, cookbookUrl: CookbookUrl);
        Assert.Equal(TimersUrl, DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Timers, requireEnabled: false));
        Assert.Equal(CookbookUrl, DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Cookbook, requireEnabled: false));
    }

    [Fact]
    public void RequireEnabledFalse_CookbookFallback_UsesTimersUrlEvenWhenDisabled()
    {
        var tenant = Tenant(timersEnabled: false, timersUrl: TimersUrl, cookbookEnabled: false, cookbookUrl: null);
        Assert.Equal(TimersUrl, DiscordNotificationRouter.ResolveWebhookUrl(tenant, DiscordNotificationChannel.Cookbook, requireEnabled: false));
    }
}
