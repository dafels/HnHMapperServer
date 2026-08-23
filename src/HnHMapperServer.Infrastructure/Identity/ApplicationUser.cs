using HnHMapperServer.Core.Constants;
using Microsoft.AspNetCore.Identity;

namespace HnHMapperServer.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string? DiscordName { get; set; }

    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// The tenant the user last selected. Soft reference (no FK): the claims factory only honours it while it
    /// still points at an approved membership in an active tenant, and falls back deterministically otherwise.
    /// </summary>
    public string? ActiveTenantId { get; set; }

    /// <summary>
    /// How the account was created (see <see cref="RegistrationSources"/>). Set once at creation.
    /// </summary>
    public string RegistrationSource { get; set; } = RegistrationSources.Password;

    /// <summary>
    /// Last successful sign-in (password or external provider). Informational, for the superadmin overview.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }
}
