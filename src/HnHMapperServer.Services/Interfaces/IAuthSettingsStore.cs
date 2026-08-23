using System.Security.Claims;
using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Process-wide snapshot of the sign-in/onboarding settings. Singleton in both Web and API. The Web process
/// subscribes to <see cref="Changed"/> to add/remove authentication schemes at runtime. Only the Web process
/// holds decrypted secrets in this snapshot (see <see cref="AuthSettingsStoreOptions.DecryptSecrets"/>).
/// </summary>
public sealed class AuthSettingsCache
{
    private readonly object _lock = new();
    private AuthSettings? _current;
    private DateTime _loadedAt;

    /// <summary>Last loaded settings, or null before the first load.</summary>
    public AuthSettings? Current
    {
        get { lock (_lock) { return _current; } }
    }

    public DateTime LoadedAt
    {
        get { lock (_lock) { return _loadedAt; } }
    }

    public event Action<AuthSettings>? Changed;

    public void Set(AuthSettings settings, bool raiseChanged)
    {
        lock (_lock)
        {
            _current = settings;
            _loadedAt = DateTime.UtcNow;
        }
        if (raiseChanged)
            Changed?.Invoke(settings);
    }
}

/// <summary>Per-process behaviour of the store.</summary>
public sealed class AuthSettingsStoreOptions
{
    /// <summary>
    /// Decrypt the Steam key / Discord secret into the process cache. Only the Web process needs them (to configure
    /// the sign-in handlers); the API process runs with this off and never holds plaintext secrets in memory.
    /// </summary>
    public bool DecryptSecrets { get; set; }
}

/// <summary>
/// Reads/writes the sign-in and onboarding settings (SuperAdmin → Sign-in). Values live in the global Config
/// rows; secrets are encrypted with the DataProtection key ring both services share. Deployment configuration
/// only supplies the defaults used while no row has been saved yet.
///
/// Authorization is enforced HERE, not just in the UI: <see cref="GetViewAsync"/> and <see cref="SaveAsync"/>
/// verify the caller is a SuperAdmin both by claim and against the database, and throw
/// <see cref="UnauthorizedAccessException"/> otherwise. Ordinary callers only ever get <see cref="AuthPolicy"/>,
/// which contains no secrets.
/// </summary>
public interface IAuthSettingsStore
{
    /// <summary>Secrets-free flags for any caller (login/register pages, API endpoints, provisioning).</summary>
    Task<AuthPolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads the settings into the process cache (startup / TTL refresh). No secrets are returned.</summary>
    Task WarmAsync(CancellationToken cancellationToken = default);

    /// <summary>SuperAdmin only: flags, ids and "configured" markers for the settings page — never the secrets.</summary>
    Task<AuthSettingsView> GetViewAsync(ClaimsPrincipal caller, CancellationToken cancellationToken = default);

    /// <summary>
    /// SuperAdmin only: applies an update, writes the rows, refreshes the cache (raising Changed) and audits the
    /// change under the caller's own user id (taken from the principal, never from the request).
    /// </summary>
    Task<AuthSettingsView> SaveAsync(AuthSettingsUpdate update, ClaimsPrincipal caller, CancellationToken cancellationToken = default);
}
