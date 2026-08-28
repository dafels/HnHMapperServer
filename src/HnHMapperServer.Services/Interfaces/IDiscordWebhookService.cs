using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Service for sending notifications to Discord via webhooks.
/// </summary>
public interface IDiscordWebhookService
{
    /// <summary>
    /// Send a notification to Discord via webhook.
    /// </summary>
    /// <param name="notification">The notification to send</param>
    /// <param name="webhookUrl">Discord webhook URL</param>
    /// <returns>True if successful, false otherwise</returns>
    Task<bool> SendNotificationAsync(NotificationDto notification, string webhookUrl);

    /// <summary>
    /// Test a Discord webhook URL by sending a test message.
    /// </summary>
    /// <param name="webhookUrl">Discord webhook URL to test</param>
    /// <param name="channel">Which channel's wording the test message uses</param>
    /// <returns>True if test was successful, false otherwise</returns>
    Task<bool> TestWebhookAsync(string webhookUrl, DiscordNotificationChannel channel = DiscordNotificationChannel.Timers);
}
