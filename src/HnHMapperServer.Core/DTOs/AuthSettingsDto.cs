namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// Sign-in and onboarding settings, managed by the superadmin in the web UI (SuperAdmin → Sign-in) and stored
/// as global config rows with the secrets encrypted. Deployment configuration only seeds the defaults.
/// Instances carrying decrypted secrets exist ONLY inside the process that needs them (the Web process, to
/// configure the Steam/Discord handlers) — every other consumer works with <see cref="AuthPolicy"/> or
/// <see cref="AuthSettingsView"/>, which never contain secrets.
/// </summary>
public class AuthSettings
{
    /// <summary>Anyone may create a password account without an invite link.</summary>
    public bool SelfRegistrationEnabled { get; set; } = true;

    /// <summary>Any signed-in player may create a map (tenant) and becomes its admin.</summary>
    public bool SelfServiceTenantsEnabled { get; set; } = true;

    /// <summary>
    /// Only accounts with a verified external identity (Steam / Discord sign-in or a linked provider) may create
    /// maps. Password-only accounts must join with an invite link or be assigned by a superadmin. Default on.
    /// </summary>
    public bool SelfServiceTenantsRequireExternalIdentity { get; set; } = true;

    /// <summary>Storage quota (MB) for self-created maps. Never client-supplied.</summary>
    public int SelfServiceDefaultQuotaMB { get; set; } = 1024;

    /// <summary>Maximum number of maps one player may administer.</summary>
    public int SelfServiceMaxOwnedTenants { get; set; } = 3;

    public bool SteamEnabled { get; set; }

    /// <summary>Steam Web API key (decrypted) — null in processes that do not load secrets.</summary>
    public string? SteamApplicationKey { get; set; }

    /// <summary>A Steam key is stored (known even when secrets are not decrypted in this process).</summary>
    public bool SteamKeyConfigured { get; set; }

    public bool DiscordEnabled { get; set; }
    public string? DiscordClientId { get; set; }

    /// <summary>Discord client secret (decrypted) — null in processes that do not load secrets.</summary>
    public string? DiscordClientSecret { get; set; }

    /// <summary>A Discord secret is stored (known even when secrets are not decrypted in this process).</summary>
    public bool DiscordSecretConfigured { get; set; }

    /// <summary>Steam sign-in is offered to players (the key is optional).</summary>
    public bool SteamActive => SteamEnabled;

    /// <summary>Discord sign-in is offered to players (needs client id + stored secret).</summary>
    public bool DiscordActive => DiscordEnabled
                                 && !string.IsNullOrWhiteSpace(DiscordClientId)
                                 && DiscordSecretConfigured;

    public AuthSettings Clone() => (AuthSettings)MemberwiseClone();

    public AuthPolicy ToPolicy() => new()
    {
        SelfRegistrationEnabled = SelfRegistrationEnabled,
        SelfServiceTenantsEnabled = SelfServiceTenantsEnabled,
        SelfServiceTenantsRequireExternalIdentity = SelfServiceTenantsRequireExternalIdentity,
        SelfServiceDefaultQuotaMB = SelfServiceDefaultQuotaMB,
        SelfServiceMaxOwnedTenants = SelfServiceMaxOwnedTenants,
        SteamSignInEnabled = SteamActive,
        DiscordSignInEnabled = DiscordActive
    };
}

/// <summary>
/// The secrets-free subset every ordinary caller (login/register pages, API endpoints, provisioning) works with.
/// </summary>
public sealed class AuthPolicy
{
    public bool SelfRegistrationEnabled { get; set; } = true;
    public bool SelfServiceTenantsEnabled { get; set; } = true;
    public bool SelfServiceTenantsRequireExternalIdentity { get; set; } = true;
    public int SelfServiceDefaultQuotaMB { get; set; } = 1024;
    public int SelfServiceMaxOwnedTenants { get; set; } = 3;
    public bool SteamSignInEnabled { get; set; }
    public bool DiscordSignInEnabled { get; set; }
}

/// <summary>
/// What the superadmin UI sends back. Secrets: null/blank = keep the stored value; the Clear flags remove it.
/// </summary>
public class AuthSettingsUpdate
{
    public bool SelfRegistrationEnabled { get; set; }
    public bool SelfServiceTenantsEnabled { get; set; }
    public bool SelfServiceTenantsRequireExternalIdentity { get; set; } = true;
    public int SelfServiceDefaultQuotaMB { get; set; }
    public int SelfServiceMaxOwnedTenants { get; set; }

    public bool SteamEnabled { get; set; }
    public string? SteamApplicationKey { get; set; }
    public bool ClearSteamApplicationKey { get; set; }

    public bool DiscordEnabled { get; set; }
    public string? DiscordClientId { get; set; }
    public string? DiscordClientSecret { get; set; }
    public bool ClearDiscordClientSecret { get; set; }
}

/// <summary>Settings as shown to the superadmin: flags and ids, but never the secrets themselves.</summary>
public class AuthSettingsView
{
    public bool SelfRegistrationEnabled { get; set; }
    public bool SelfServiceTenantsEnabled { get; set; }
    public bool SelfServiceTenantsRequireExternalIdentity { get; set; }
    public int SelfServiceDefaultQuotaMB { get; set; }
    public int SelfServiceMaxOwnedTenants { get; set; }

    public bool SteamEnabled { get; set; }
    public bool SteamApplicationKeyConfigured { get; set; }
    public bool SteamActive { get; set; }

    public bool DiscordEnabled { get; set; }
    public string? DiscordClientId { get; set; }
    public bool DiscordClientSecretConfigured { get; set; }
    public bool DiscordActive { get; set; }

    public static AuthSettingsView From(AuthSettings s) => new()
    {
        SelfRegistrationEnabled = s.SelfRegistrationEnabled,
        SelfServiceTenantsEnabled = s.SelfServiceTenantsEnabled,
        SelfServiceTenantsRequireExternalIdentity = s.SelfServiceTenantsRequireExternalIdentity,
        SelfServiceDefaultQuotaMB = s.SelfServiceDefaultQuotaMB,
        SelfServiceMaxOwnedTenants = s.SelfServiceMaxOwnedTenants,
        SteamEnabled = s.SteamEnabled,
        SteamApplicationKeyConfigured = s.SteamKeyConfigured,
        SteamActive = s.SteamActive,
        DiscordEnabled = s.DiscordEnabled,
        DiscordClientId = s.DiscordClientId,
        DiscordClientSecretConfigured = s.DiscordSecretConfigured,
        DiscordActive = s.DiscordActive
    };
}
