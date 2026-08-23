using HnHMapperServer.Services.Interfaces;

namespace HnHMapperServer.Web.Security;

/// <summary>One enabled external sign-in provider (authentication scheme name + what the button says).</summary>
public sealed record ExternalAuthProvider(string Scheme, string DisplayName)
{
    public string ChallengeUrl(string? invite = null, string? returnUrl = null)
    {
        var url = $"/auth/{Scheme.ToLowerInvariant()}/challenge";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(invite)) query.Add($"invite={Uri.EscapeDataString(invite)}");
        if (!string.IsNullOrEmpty(returnUrl)) query.Add($"returnUrl={Uri.EscapeDataString(returnUrl)}");
        return query.Count == 0 ? url : $"{url}?{string.Join('&', query)}";
    }

    public string LinkUrl => $"/auth/{Scheme.ToLowerInvariant()}/link";
}

/// <summary>
/// The external providers currently switched on by the superadmin (SuperAdmin → Sign-in &amp; onboarding).
/// Reads the live settings snapshot, so buttons and routes appear/disappear the moment settings are saved.
/// </summary>
public sealed class ExternalAuthProviders
{
    public const string SteamScheme = "Steam";
    public const string DiscordScheme = "Discord";

    private static readonly ExternalAuthProvider Steam = new(SteamScheme, "Steam");
    private static readonly ExternalAuthProvider Discord = new(DiscordScheme, "Discord");

    private readonly AuthSettingsCache _cache;

    public ExternalAuthProviders(AuthSettingsCache cache)
    {
        _cache = cache;
    }

    public IReadOnlyList<ExternalAuthProvider> Enabled
    {
        get
        {
            var settings = _cache.Current;
            if (settings == null)
                return Array.Empty<ExternalAuthProvider>();

            var list = new List<ExternalAuthProvider>(2);
            if (settings.SteamActive) list.Add(Steam);
            if (settings.DiscordActive) list.Add(Discord);
            return list;
        }
    }

    public bool Any => Enabled.Count > 0;

    public ExternalAuthProvider? Find(string? scheme) =>
        scheme == null ? null : Enabled.FirstOrDefault(p => string.Equals(p.Scheme, scheme, StringComparison.OrdinalIgnoreCase));
}
