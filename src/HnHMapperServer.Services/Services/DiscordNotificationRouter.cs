using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// The Discord channel a notification is delivered to. Each channel has its own
/// enable toggle and webhook URL on the tenant.
/// </summary>
public enum DiscordNotificationChannel
{
    /// <summary>Timer alerts and any other notification type (the tenant's main webhook).</summary>
    Timers,

    /// <summary>Cookbook food-discovery digests.</summary>
    Cookbook
}

/// <summary>
/// Pure routing rules for tenant Discord webhooks: which channel a notification type
/// belongs to, and which webhook URL (if any) that channel resolves to for a tenant.
/// </summary>
/// <remarks>
/// Timer notifications must always resolve to <see cref="TenantDto.DiscordWebhookUrl"/> —
/// that URL doubles as the HMAC signing key for map preview images, and the preview
/// endpoint validates against that column only. Cookbook notifications never generate
/// previews (their ActionType is NavigateToCookbook), so they are free to use a
/// different URL.
/// </remarks>
public static class DiscordNotificationRouter
{
    /// <summary>
    /// Maps a notification type to its Discord channel. Unknown/future types stay on
    /// the main (timers) channel, matching the pre-split behavior where every
    /// notification fired the tenant's single webhook.
    /// </summary>
    public static DiscordNotificationChannel GetChannel(string notificationType) =>
        notificationType == CookbookNotificationService.NotificationType
            ? DiscordNotificationChannel.Cookbook
            : DiscordNotificationChannel.Timers;

    /// <summary>
    /// Resolves the webhook URL a notification on <paramref name="channel"/> should be
    /// sent to for this tenant. The cookbook channel falls back to the timer webhook
    /// when it has no URL of its own — but only while the timer channel is itself
    /// live, so disabling Discord entirely never resurrects cookbook pings.
    /// </summary>
    /// <param name="requireEnabled">
    /// When false, the per-channel enable toggles are ignored and only URL presence
    /// matters — used by the webhook test endpoint, which lets admins test a saved URL
    /// before switching the channel on.
    /// </param>
    /// <returns>The webhook URL to send to, or null when nothing should be sent.</returns>
    public static string? ResolveWebhookUrl(
        TenantDto tenant,
        DiscordNotificationChannel channel,
        bool requireEnabled = true)
    {
        var timersUrl = requireEnabled && !tenant.DiscordNotificationsEnabled
            ? null
            : NonBlank(tenant.DiscordWebhookUrl);

        if (channel == DiscordNotificationChannel.Timers)
        {
            return timersUrl;
        }

        if (requireEnabled && !tenant.DiscordCookbookNotificationsEnabled)
        {
            return null;
        }

        return NonBlank(tenant.DiscordCookbookWebhookUrl) ?? timersUrl;
    }

    private static string? NonBlank(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url;
}
