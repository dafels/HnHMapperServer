# Discord Integration Guide

Complete guide for setting up timer and cookbook notifications in Discord for HavenMap.

---

## Overview

HavenMap can send beautiful, rich-formatted notifications to Discord when timers expire or approach expiry, and when new foods are discovered in the cookbook. Each tenant configures two independent channels — **timer alerts** and **cookbook discoveries** — each with its own enable toggle and webhook URL, so they can post to two different Discord channels (or share one).

**Supported Notifications:**
- ⏰ Pre-expiry warnings at 4 intervals: 1 day, 4 hours, 1 hour, 10 minutes
- 🔔 Timer expiration notifications
- 🍳 Cookbook food-discovery digests (separate channel, optional)
- 📍 Clickable map links that navigate directly to marker locations
- 🎨 Marker icons displayed as thumbnails

---

## Discord Setup (One-Time per Channel)

### Step 1: Create a Discord Channel

1. Open your Discord server
2. Create a new text channel (or use an existing one)
   - Recommended name: `#havenmap-timers` or `#map-notifications`
3. Ensure you have "Manage Webhooks" permission in this channel

### Step 2: Create a Webhook

1. **Right-click** the channel → **Edit Channel**
2. Navigate to **Integrations** tab (left sidebar)
3. Click **Create Webhook** or **View Webhooks**
4. Click **New Webhook** button

### Step 3: Configure the Webhook

1. **Name**: Give it a recognizable name
   - Example: `HavenMap Notifications`
2. **Icon** (Optional): Upload a custom avatar for the bot
3. **Channel**: Verify it's pointing to the correct channel
4. Click **Copy Webhook URL** button
   - The URL will look like: `https://discord.com/api/webhooks/1234567890/abcdefghijklmnopqrstuvwxyz`
5. Click **Save Changes**

⚠️ **Keep this URL secret!** Anyone with the webhook URL can post messages to your channel.

---

## Application Setup

### Step 1: Access Admin Panel

1. Log in to HavenMap with an admin account
2. Navigate to **Admin** page (top navigation)
3. Click the **Settings** tab

### Step 2: Configure Discord Integration

1. Scroll to the **Discord Integration** section — it has two subsections: **Timer alerts** and **Cookbook discoveries**
2. Toggle **Enable timer notifications** to **ON**
3. Paste your webhook URL into the **Timer Webhook URL** field
   - Make sure it starts with `https://discord.com/api/webhooks/`
4. (Optional) Toggle **Enable cookbook notifications** to **ON** and paste a second webhook URL into the **Cookbook Webhook URL** field to route food discoveries to a different channel
   - Leave the cookbook URL blank to send cookbook notifications to the timer webhook
   - Toggle it **OFF** to silence cookbook Discord pings entirely
5. Click **Save Discord Settings**
6. Click **Test Timer Webhook** (and **Test Cookbook Webhook** if enabled)
   - Check your Discord channel(s) for a test message
   - If successful, you'll see: "✅ Test Notification - Your Discord webhook is configured correctly!"

---

## Notification Types

### 1. Pre-Expiry Warnings

The system sends 4 warnings before a timer expires:

| Warning | Time Before | Priority | Color | Emoji |
|---------|-------------|----------|-------|-------|
| First   | 1 day       | Normal   | Blue  | 📅    |
| Second  | 4 hours     | Normal   | Blue  | ⏰    |
| Third   | 1 hour      | High     | Orange| ⏱️    |
| Fourth  | 10 minutes  | Urgent   | Red   | ⚠️    |

**Example Message:**
```
📅 Tree Stump - 1 day remaining
Timer will expire in approximately 1 day
```

### 2. Timer Expired

Sent when a timer reaches its expiry time:

| Type | Priority | Color | Emoji |
|------|----------|-------|-------|
| Expired | Normal | Blue | 🔔 |

**Example Message:**
```
🔔 Tree Stump is ready!
Resource is ready to be harvested
```

### 3. Rich Embed Features

All notifications include:
- **Clickable Title**: Click to navigate directly to the marker on the map
- **Thumbnail**: Displays the marker's icon (top-right corner)
- **Map Preview**: 400x400px composite image showing 4x4 grid of tiles around the marker (full-width)
- **Marker Indicator**: Red crosshair pin showing exact marker location on the preview
- **Timestamp**: Shows when the notification was created
- **Color Coding**: Visual priority indication (Blue/Orange/Red)
- **Footer**: "HavenMap Notification"

**Map Preview Details:**
- Shows 4x4 grid of map tiles (400x400 pixels total)
- Marker is centered in the preview
- Red crosshair (+) marks exact marker position
- Provides visual context of surrounding area
- Automatically generated for all marker-based notifications
- Preview images cached for 7 days

---

## How the System Works

### Background Processing

- **Timer Check Service** runs every 30 seconds
- Checks all active timers for all tenants
- Sends notifications immediately when thresholds are reached
- Prevents duplicate warnings using `TimerWarnings` tracking table

### Warning Logic

For each timer, the system checks if the remaining time matches any warning interval (±30 seconds tolerance):

```
Current Time -> Timer Ready Time
         ^
         |
    Check Points:
    - 1440 minutes (1 day)
    - 240 minutes (4 hours)
    - 60 minutes (1 hour)
    - 10 minutes
    - 0 minutes (expired)
```

Each warning is sent **only once** per timer. If a timer is updated/reset, warning history is cleared and warnings will be sent again.

### Notification Flow

1. **Timer Check Service** detects timer threshold
2. Creates notification in database
3. Sends to Discord webhook (fire-and-forget, non-blocking)
4. Records warning in `TimerWarnings` table
5. Broadcasts real-time update via Server-Sent Events (SSE)

---

## Testing

### Creating Test Timers

To test the notification system:

1. **For 10-minute warning**:
   - Create a timer that expires in 11 minutes
   - Wait ~60 seconds
   - You should receive the ⚠️ 10-minute warning

2. **For 1-hour warning**:
   - Create a timer that expires in 61 minutes
   - Wait ~60 seconds
   - You should receive the ⏱️ 1-hour warning

3. **For immediate expiry**:
   - Create a timer that expires in 1 minute
   - Wait ~60 seconds
   - You should receive the 🔔 expiration notification

### Expected Behavior

- **Markers**: Notification includes clickable link and icon thumbnail
- **Standalone Timers**: Notification shows title and description
- **Multiple Warnings**: Each warning sent only once per timer
- **Tenant Isolation**: Only timers for your tenant trigger notifications to your webhook

---

## Troubleshooting

### No Notifications Appearing

**Check 1: Discord Integration Enabled**
- Admin → Settings → Discord Integration
- Verify toggle is **ON**
- Verify webhook URL is correct

**Check 2: Test Connection**
- Click "Test Connection" button
- If test fails, webhook URL may be invalid

**Check 3: Timer Check Service Running**
- Check application logs for: "Timer Check Service started"
- Service should log: "Processed X expired timers and Y pre-expiry warnings"

**Check 4: Webhook URL Format**
- Must start with: `https://discord.com/api/webhooks/`
- Must contain webhook ID and token
- No trailing slashes or extra characters

**Check 5: Discord Permissions**
- Webhook must have permission to post in channel
- Channel must not be deleted or archived

### Test Message Works, But Timer Notifications Don't

**Check Timer Ready Time**
- Verify timer has correct expiry time (UTC)
- Use database viewer: Admin → Database → Timers table
- Check `ReadyAt` column is in the future

**Check Warning Already Sent**
- Database viewer → TimerWarnings table
- Look for entries with your timer's ID
- If warning exists, it won't be sent again

**Check Logs**
- Application logs should show:
  - "Pre-expiry warning sent for timer {ID}"
  - "Timer {ID} expired"
- If missing, timer may not be in correct state

### Duplicate Notifications

**Cause**: Database migration didn't apply properly
- `TimerWarnings` table missing
- Warnings not being tracked

**Fix**:
1. Check database schema includes `TimerWarnings` table
2. Restart application to apply migrations
3. Clear any stuck warnings: Delete from `TimerWarnings` table

### Webhook Rate Limiting

Discord webhooks have rate limits:
- **30 requests per minute** per webhook
- **Burst limit**: 5 requests per 5 seconds

If you have many timers expiring simultaneously, some notifications may be delayed or dropped.

**Solutions**:
- Stagger timer creation times
- Use multiple webhooks for different timer types (requires code changes)

---

## Security Considerations

### Webhook URL Security

⚠️ **CRITICAL**: Your webhook URL is a secret credential!

**If compromised, attackers can:**
- Post spam messages to your Discord channel
- Impersonate the HavenMap bot
- Send @everyone/@here mentions

**Best Practices:**
1. **Never share** the webhook URL publicly
2. **Never commit** webhook URLs to version control
3. **Regenerate** webhook if URL is leaked (Discord → Edit Webhook → Copy Webhook URL)
4. **Restrict access** to admin panel (only trusted users)
5. **Use HTTPS** for HavenMap deployment (prevents URL interception)

### Database Security

Webhook URLs are stored in the `Tenants` table:
- Columns: `DiscordWebhookUrl` (timer alerts) and `DiscordCookbookWebhookUrl` (cookbook discoveries)
- Tenant-isolated (each tenant has their own webhooks)
- Protected by ASP.NET Core authentication/authorization

**Production Recommendations:**
- Encrypt database at rest
- Use secure file permissions for `grids.db`
- Regular database backups

---

## Advanced Configuration

### Multiple Discord Channels

Each tenant has **two independent channels**, configured in Admin → Settings → Discord Integration:

- **Timer alerts** (`MarkerTimerExpired`, `StandaloneTimerExpired`, `TimerPreExpiryWarning`) — the
  tenant's main webhook (`DiscordWebhookUrl` + `DiscordNotificationsEnabled`).
- **Cookbook discoveries** (`CookbookFoodAdded`) — its own toggle and webhook
  (`DiscordCookbookWebhookUrl` + `DiscordCookbookNotificationsEnabled`). When the cookbook webhook
  URL is blank, cookbook notifications fall back to the timer webhook — but only while the timer
  channel is itself enabled and configured. Toggling cookbook off silences cookbook pings entirely.

Point the two webhooks at different Discord channels to separate the streams. Both settings are
saved together via `PUT /api/tenants/{tenantId}/discord-settings` — note the endpoint **overwrites
all four fields**, so raw API callers must always send the full state (the admin UI does).
`POST /api/tenants/{tenantId}/discord-test?channel=timers|cookbook` sends a channel-appropriate
test message to whichever URL that channel would actually use.

For more channels than that (e.g. per-group timers), the older workarounds still apply: a Discord
bot that forwards messages, or separate tenants.

### Custom Notification Filtering

Routing lives in one place: `HnHMapperServer.Services/Services/DiscordNotificationRouter.cs`.
`GetChannel` maps a notification type to a channel (unknown types go to the timer channel) and
`ResolveWebhookUrl` picks the webhook URL for a tenant, or `null` to send nothing.
`NotificationService.CreateAsync` calls both, so custom filtering (e.g. skipping
`TimerPreExpiryWarning`) belongs in the router rather than in `NotificationService`.

### Base URL Configuration

For clickable map links to work correctly, the application needs to know its public URL.

**Configuration**: `appsettings.json` or environment variable
```json
{
  "Discord": {
    "BaseUrl": "https://your-domain.com"
  }
}
```

**Fallback**: Uses `Kestrel:Endpoints:Http:Url` if not specified.

**Docker**: Set environment variable in `docker-compose.yml`:
```yaml
services:
  api:
    environment:
      - Discord__BaseUrl=https://map.yourdomain.com
```

---

## FAQ

### Q: Can I test the webhook URL before saving?
**A**: Yes! Click the "Test Connection" button. A test message will be sent to your Discord channel immediately.

### Q: Will I get spammed with notifications?
**A**: No. Each warning is sent **only once** per timer. You'll receive exactly 5 notifications per timer (4 warnings + 1 expiry).

### Q: Can I disable specific warning levels?
**A**: Not through the UI. You would need to modify `TimerCheckService.cs` and remove intervals from the `WARNING_INTERVALS` array.

### Q: Do notifications work for standalone timers without markers?
**A**: Yes! Standalone timers receive the same warnings, but without map links or icon thumbnails.

### Q: What happens if Discord is down?
**A**: Notifications fail silently (fire-and-forget). In-app notifications still work normally. Discord notifications will not be retried.

### Q: Can I customize the message format?
**A**: Yes, by modifying `DiscordWebhookService.cs` → `BuildEmbedAsync()` method. You can change colors, emojis, fields, and formatting.

### Q: How do I disable Discord notifications temporarily?
**A**: Admin → Settings → Toggle "Enable timer notifications" (and/or "Enable cookbook notifications") to **OFF**. Webhook URLs remain saved.

### Q: Can cookbook discoveries go to a different channel than timer alerts?
**A**: Yes — the Cookbook discoveries subsection has its own webhook URL. Set it to a webhook from a second Discord channel. Leaving it blank sends cookbook notifications to the timer webhook; toggling it off disables them.

### Q: Can multiple users receive notifications?
**A**: Yes! All users with access to the Discord channel will see notifications. Notifications are sent to the channel (via webhook), not to individual users.

### Q: How do I change which channel receives notifications?
**A**: Edit the webhook in Discord (or create a new one) and update the webhook URL in HavenMap admin settings.

### Q: Why don't I see map previews in Discord?
**A**: Map previews are only generated for marker-based notifications (not standalone timers). If you still don't see them:
1. Check application logs for: "Added map preview to Discord notification"
2. Verify tiles exist for the marker's location (preview needs tiles to composite)
3. Check preview directory exists: `map/previews/{tenantId}/`
4. Preview generation failures are non-critical and won't block notifications

### Q: Can I disable map previews?
**A**: Not through the UI currently. To disable, comment out the preview generation code in `DiscordWebhookService.cs` lines 294-376.

### Q: How much storage do map previews use?
**A**: Each preview is ~40-60KB. With 7-day retention and frequent notifications, expect ~1-5MB per tenant. Previews are automatically cleaned up after 7 days.

---

## Example Screenshots

### Discord Test Notification
```
┌─────────────────────────────────────────────────┐
│ HavenMap Notifications        BOT   Today 3:42 PM │
├─────────────────────────────────────────────────┤
│ ✅ Test Notification                            │
│                                                  │
│ Your Discord webhook is configured correctly!   │
│ You will receive notifications here when timers │
│ expire.                                         │
│                                                  │
│ HavenMap Discord Integration       Just now     │
└─────────────────────────────────────────────────┘
```

### Timer Expiry Notification (with marker)
```
┌─────────────────────────────────────────────────┐
│ HavenMap Notifications        BOT   Today 4:15 PM │
├─────────────────────────────────────────────────┤
│ 🔔 Tree Stump is ready!                  [ICON] │
│                                          [IMG]  │
│ Resource is ready to be harvested                │
│                                                  │
│ ┌───────────────────────────────────────────┐   │
│ │         [MAP PREVIEW IMAGE]               │   │
│ │    4x4 grid of tiles with red + marker   │   │
│ │           (400x400 pixels)                │   │
│ └───────────────────────────────────────────┘   │
│                                                  │
│ Click title or image to view on map             │
│                                                  │
│ HavenMap Notification          4:15 PM          │
└─────────────────────────────────────────────────┘
```

### Pre-Expiry Warning
```
┌─────────────────────────────────────────────────┐
│ HavenMap Notifications        BOT   Today 2:30 PM │
├─────────────────────────────────────────────────┤
│ ⏱️ Apple Tree - 1 hour remaining         [ICON] │
│                                          [IMG]  │
│ Timer will expire in approximately 1 hour       │
│                                                  │
│ Click title to view on map                      │
│                                                  │
│ HavenMap Notification          2:30 PM          │
└─────────────────────────────────────────────────┘
```

---

## Technical Details

### API Endpoints

**Update Discord Settings** (Tenant Admin only)
```
PUT /api/tenants/{tenantId}/discord-settings
Authorization: Cookie (admin)

Body:
{
  "enabled": true,
  "webhookUrl": "https://discord.com/api/webhooks/..."
}
```

**Test Discord Webhook** (Tenant Admin only)
```
POST /api/tenants/{tenantId}/discord-test
Authorization: Cookie (admin)
```

### Database Schema

**Tenants Table**
```sql
ALTER TABLE Tenants ADD COLUMN DiscordWebhookUrl TEXT NULL;
ALTER TABLE Tenants ADD COLUMN DiscordNotificationsEnabled INTEGER NOT NULL DEFAULT 0;
```

**TimerWarnings Table**
```sql
CREATE TABLE TimerWarnings (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TimerId INTEGER NOT NULL,
    WarningMinutes INTEGER NOT NULL,
    SentAt TEXT NOT NULL,
    FOREIGN KEY (TimerId) REFERENCES Timers(Id) ON DELETE CASCADE,
    UNIQUE(TimerId, WarningMinutes)
);
```

### Background Service

**TimerCheckService**
- Runs every 30 seconds
- Checks all active tenants
- Processes timers needing warnings or expiry notifications
- Logs: "Processed {count} expired timers and {count} pre-expiry warnings"

### Dependencies

- **Microsoft.Extensions.Http**: HttpClient factory for webhook requests
- **System.Text.Json**: JSON serialization for embed payload
- **Discord API**: Webhook endpoint (no bot token required)
- **SixLabors.ImageSharp**: Image composition for map previews

### Map Preview System

**Preview Generation:**
- Triggered automatically for all marker-based notifications
- Loads 4x4 grid of tiles (16 total) from database
- Composites tiles into 400x400px PNG image
- Draws red crosshair (+) at exact marker coordinates
- Saves to tenant-isolated directory: `map/previews/{tenantId}/`
- Returns preview ID for URL construction

**Preview Serving:**
- Endpoint: `GET /map/preview/{previewId}`
- Public access (no authentication required for Discord)
- Preview ID format: `{timestamp}_{mapId}_{coordX}_{coordY}.png`
- Cached by Discord and browsers (7-day expiration)
- ETag and Last-Modified headers for efficient caching

**Preview Cleanup:**
- Background service runs every 6 hours
- Deletes preview images older than 7 days
- Removes empty tenant preview directories
- Logged at: "Preview cleanup completed: deleted {count} old preview images"

**Performance:**
- Generation time: ~100ms per notification
- Memory usage: ~10MB per preview (16 tiles loaded)
- File size: 40-60KB per preview PNG
- Non-blocking async operation (fire-and-forget)
- No impact on notification delivery speed

---

## Support

### Logs Location

**Development**: Console output via Serilog
**Production**: Docker logs via `docker logs api` or `docker logs web`

**Relevant Log Messages:**
```
[Information] Successfully sent Discord notification {NotificationId} to webhook
[Warning] Failed to send Discord notification {NotificationId}. Status: {StatusCode}
[Error] HTTP error sending Discord notification {NotificationId}
[Information] Pre-expiry warning sent for timer {TimerId}
```

### Common Error Messages

**"Discord webhook URL is empty, skipping notification"**
- Webhook URL not configured or invalid
- Check admin settings

**"Failed to send Discord notification: Timeout"**
- Discord API is slow/down
- Network connectivity issue
- Check HTTP timeout settings (default: 10 seconds)

**"Failed to send Discord notification: 404 Not Found"**
- Webhook URL is invalid or webhook was deleted
- Regenerate webhook in Discord

**"Failed to send Discord notification: 429 Too Many Requests"**
- Rate limit exceeded (>30 requests/minute)
- Reduce timer notification frequency

---

## Changelog

### v1.1 (2025-11-21)
- **Map Preview Images**: Automatic 400x400px preview generation for marker notifications
- Shows 4x4 grid of tiles centered on marker location
- Red crosshair indicator at exact marker position
- Public preview endpoint: `GET /map/preview/{id}`
- 7-day preview retention with automatic cleanup
- ~100ms generation time per notification

### v1.0 (2025-11-21)
- Initial Discord integration implementation
- Multi-level timer warnings (1 day, 4 hours, 1 hour, 10 minutes)
- Rich embed formatting with icons and clickable links
- Tenant-isolated webhook configuration
- Test connection feature
- Fire-and-forget notification delivery

---

## Related Documentation

- [CLAUDE.md](CLAUDE.md) - Complete project documentation
- [API_SPECIFICATION.md](API_SPECIFICATION.md) - API endpoint details
- [DEPLOYMENT.md](DEPLOYMENT.md) - Production deployment guide
- [Discord Webhook Documentation](https://discord.com/developers/docs/resources/webhook)

---

**Last Updated**: 2025-11-21
**Author**: HavenMap Development Team
