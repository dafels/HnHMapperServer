using AspNet.Security.OAuth.Discord;
using AspNet.Security.OpenId.Steam;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HnHMapperServer.Web.Security;

/// <summary>
/// Turns the superadmin's sign-in settings into live authentication schemes without a restart.
///
/// Both provider handlers are registered at startup (AddSteam / AddDiscord) so their options pipeline,
/// post-configuration and DI are in place; this class then ADDS or REMOVES the scheme from the
/// <see cref="IAuthenticationSchemeProvider"/> according to the settings, and evicts the cached options so the
/// next handler instance re-reads keys/secrets through <see cref="DynamicAuthOptionsConfigurator"/>.
/// Removing the scheme matters: the authentication middleware initialises every registered remote handler on
/// every request, and an OAuth handler with an empty ClientId throws during that initialisation.
/// </summary>
public sealed class DynamicAuthSchemeManager
{
    private readonly IAuthenticationSchemeProvider _schemes;
    private readonly IOptionsMonitorCache<SteamAuthenticationOptions> _steamOptions;
    private readonly IOptionsMonitorCache<DiscordAuthenticationOptions> _discordOptions;
    private readonly AuthSettingsCache _cache;
    private readonly ILogger<DynamicAuthSchemeManager> _logger;
    private readonly object _lock = new();

    public DynamicAuthSchemeManager(
        IAuthenticationSchemeProvider schemes,
        IOptionsMonitorCache<SteamAuthenticationOptions> steamOptions,
        IOptionsMonitorCache<DiscordAuthenticationOptions> discordOptions,
        AuthSettingsCache cache,
        ILogger<DynamicAuthSchemeManager> logger)
    {
        _schemes = schemes;
        _steamOptions = steamOptions;
        _discordOptions = discordOptions;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Applies the current settings and keeps following changes (call once at startup).</summary>
    public void Start(AuthSettings initial)
    {
        Apply(initial);
        _cache.Changed += Apply;
    }

    public void Apply(AuthSettings settings)
    {
        lock (_lock)
        {
            // Evict cached options first so a re-added scheme picks up the new key/secret
            _steamOptions.TryRemove(SteamAuthenticationDefaults.AuthenticationScheme);
            _discordOptions.TryRemove(DiscordAuthenticationDefaults.AuthenticationScheme);

            Toggle(SteamAuthenticationDefaults.AuthenticationScheme, SteamAuthenticationDefaults.DisplayName,
                typeof(SteamAuthenticationHandler), settings.SteamActive);
            Toggle(DiscordAuthenticationDefaults.AuthenticationScheme, DiscordAuthenticationDefaults.DisplayName,
                typeof(DiscordAuthenticationHandler), settings.DiscordActive);
        }
    }

    private void Toggle(string scheme, string displayName, Type handlerType, bool enabled)
    {
        var existing = _schemes.GetSchemeAsync(scheme).GetAwaiter().GetResult();
        if (enabled && existing == null)
        {
            _schemes.TryAddScheme(new AuthenticationScheme(scheme, displayName, handlerType));
            _logger.LogInformation("External sign-in enabled: {Scheme}", scheme);
        }
        else if (!enabled && existing != null)
        {
            _schemes.RemoveScheme(scheme);
            _logger.LogInformation("External sign-in disabled: {Scheme}", scheme);
        }
    }
}

/// <summary>
/// Feeds the superadmin-managed key/secret into the provider options every time they are (re)built.
/// Runs after the static AddSteam/AddDiscord configuration because it is registered later.
/// </summary>
public sealed class DynamicAuthOptionsConfigurator :
    IConfigureNamedOptions<SteamAuthenticationOptions>,
    IConfigureNamedOptions<DiscordAuthenticationOptions>
{
    private const string Placeholder = "unconfigured";
    private readonly AuthSettingsCache _cache;

    public DynamicAuthOptionsConfigurator(AuthSettingsCache cache)
    {
        _cache = cache;
    }

    public void Configure(string? name, SteamAuthenticationOptions options)
    {
        if (name != SteamAuthenticationDefaults.AuthenticationScheme) return;
        var key = _cache.Current?.SteamApplicationKey;
        options.ApplicationKey = string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public void Configure(SteamAuthenticationOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, DiscordAuthenticationOptions options)
    {
        if (name != DiscordAuthenticationDefaults.AuthenticationScheme) return;
        var settings = _cache.Current;
        // Placeholders keep OAuthOptions.Validate() happy while the scheme is disabled (it is then never
        // challenged); real values arrive as soon as the superadmin saves them.
        options.ClientId = string.IsNullOrWhiteSpace(settings?.DiscordClientId) ? Placeholder : settings!.DiscordClientId!;
        options.ClientSecret = string.IsNullOrWhiteSpace(settings?.DiscordClientSecret) ? Placeholder : settings!.DiscordClientSecret!;
    }

    public void Configure(DiscordAuthenticationOptions options) => Configure(Options.DefaultName, options);
}
