using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

public class ExternalUserProvisioner : IExternalUserProvisioner
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuditService _audit;
    private readonly ILogger<ExternalUserProvisioner> _logger;

    public ExternalUserProvisioner(
        UserManager<ApplicationUser> userManager,
        IAuditService audit,
        ILogger<ExternalUserProvisioner> logger)
    {
        _userManager = userManager;
        _audit = audit;
        _logger = logger;
    }

    public Task<ApplicationUser?> FindAsync(ExternalIdentity identity) =>
        _userManager.FindByLoginAsync(identity.Provider, identity.ProviderKey);

    public async Task<ApplicationUser> ProvisionAsync(ExternalIdentity identity)
    {
        var username = await ExternalUsernameFactory.MakeUniqueAsync(
            identity.DisplayName,
            async candidate => await _userManager.FindByNameAsync(candidate) != null);

        var user = new ApplicationUser
        {
            UserName = username,
            Email = string.Empty,
            CreatedAt = DateTime.UtcNow,
            RegistrationSource = RegistrationSourceFor(identity.Provider),
            // A Discord sign-in yields the real Discord username - better than the self-reported register field
            DiscordName = IsDiscord(identity.Provider) ? Truncate(identity.DisplayName, 32) : null
        };

        var created = await _userManager.CreateAsync(user);   // passwordless
        if (!created.Succeeded)
            throw new InvalidOperationException("Could not create account: " + string.Join("; ", created.Errors.Select(e => e.Description)));

        var login = await _userManager.AddLoginAsync(user, new UserLoginInfo(identity.Provider, identity.ProviderKey, identity.Provider));
        if (!login.Succeeded)
        {
            await _userManager.DeleteAsync(user);   // never leave an account nobody can sign in to
            throw new InvalidOperationException("Could not attach external login: " + string.Join("; ", login.Errors.Select(e => e.Description)));
        }

        await _audit.LogAsync(new AuditEntry
        {
            UserId = user.Id,
            Action = "ExternalUserProvisioned",
            EntityType = "User",
            EntityId = user.Id,
            NewValue = $"provider={identity.Provider}; username={username}"
        });

        _logger.LogInformation("Provisioned user {UserId} ({Username}) from {Provider}", user.Id, username, identity.Provider);
        return user;
    }

    public async Task<LinkOutcome> LinkAsync(ApplicationUser user, ExternalIdentity identity)
    {
        var owner = await _userManager.FindByLoginAsync(identity.Provider, identity.ProviderKey);
        if (owner != null && owner.Id != user.Id)
            return LinkOutcome.LinkedToAnotherAccount;
        if (owner != null)
            return LinkOutcome.Linked;   // already linked to this very account - idempotent

        var logins = await _userManager.GetLoginsAsync(user);
        if (logins.Any(l => string.Equals(l.LoginProvider, identity.Provider, StringComparison.OrdinalIgnoreCase)))
            return LinkOutcome.ProviderAlreadyLinked;

        var result = await _userManager.AddLoginAsync(user, new UserLoginInfo(identity.Provider, identity.ProviderKey, identity.Provider));
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not link external login: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        if (IsDiscord(identity.Provider) && string.IsNullOrWhiteSpace(user.DiscordName) && !string.IsNullOrWhiteSpace(identity.DisplayName))
        {
            user.DiscordName = Truncate(identity.DisplayName, 32);
            await _userManager.UpdateAsync(user);
        }

        await _audit.LogAsync(new AuditEntry
        {
            UserId = user.Id,
            Action = "ExternalAccountLinked",
            EntityType = "User",
            EntityId = user.Id,
            NewValue = $"provider={identity.Provider}"
        });

        _logger.LogInformation("User {UserId} linked {Provider}", user.Id, identity.Provider);
        return LinkOutcome.Linked;
    }

    public async Task<UnlinkOutcome> UnlinkAsync(ApplicationUser user, string provider)
    {
        var logins = await _userManager.GetLoginsAsync(user);
        var login = logins.FirstOrDefault(l => string.Equals(l.LoginProvider, provider, StringComparison.OrdinalIgnoreCase));
        if (login == null)
            return UnlinkOutcome.NotLinked;

        // Never strand an account: a password or another provider must remain
        var hasPassword = await _userManager.HasPasswordAsync(user);
        if (!hasPassword && logins.Count <= 1)
            return UnlinkOutcome.LastSignInMethod;

        var result = await _userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not unlink external login: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        await _audit.LogAsync(new AuditEntry
        {
            UserId = user.Id,
            Action = "ExternalAccountUnlinked",
            EntityType = "User",
            EntityId = user.Id,
            OldValue = $"provider={provider}"
        });

        return UnlinkOutcome.Unlinked;
    }

    private static bool IsDiscord(string provider) => string.Equals(provider, RegistrationSources.Discord, StringComparison.OrdinalIgnoreCase);

    private static string RegistrationSourceFor(string provider) =>
        RegistrationSources.All.FirstOrDefault(s => string.Equals(s, provider, StringComparison.OrdinalIgnoreCase)) ?? provider;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
