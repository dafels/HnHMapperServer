using HnHMapperServer.Infrastructure.Identity;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>Identity as reported by an external provider after a successful sign-in round-trip.</summary>
public sealed record ExternalIdentity(string Provider, string ProviderKey, string? DisplayName);

public enum LinkOutcome
{
    Linked,
    /// <summary>This external identity is already attached to a different account.</summary>
    LinkedToAnotherAccount,
    /// <summary>The account already has this provider linked (possibly a different identity).</summary>
    ProviderAlreadyLinked
}

public enum UnlinkOutcome
{
    Unlinked,
    NotLinked,
    /// <summary>Refused: it is the account's only way to sign in (no password, no other provider).</summary>
    LastSignInMethod
}

/// <summary>
/// Finds, creates, links and unlinks accounts for external sign-in providers (Steam, Discord). Lives in the
/// Services project so it can be tested against a real UserManager over SQLite; the Web process drives it
/// from the provider callback.
/// </summary>
public interface IExternalUserProvisioner
{
    Task<ApplicationUser?> FindAsync(ExternalIdentity identity);

    /// <summary>Creates a passwordless account for a first-time external sign-in and attaches the login.</summary>
    Task<ApplicationUser> ProvisionAsync(ExternalIdentity identity);

    Task<LinkOutcome> LinkAsync(ApplicationUser user, ExternalIdentity identity);

    Task<UnlinkOutcome> UnlinkAsync(ApplicationUser user, string provider);
}
