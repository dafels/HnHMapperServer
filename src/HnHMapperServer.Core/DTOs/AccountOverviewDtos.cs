namespace HnHMapperServer.Core.DTOs;

/// <summary>Query for the superadmin accounts overview (GET /api/superadmin/accounts).</summary>
public class AccountOverviewQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    /// <summary>Username or Discord name contains (case-insensitive).</summary>
    public string? Search { get; set; }

    /// <summary>Sign-in method filter: "Password", "Steam", "Discord".</summary>
    public string? Method { get; set; }

    /// <summary>Registration source filter: "Password", "Steam", "Discord".</summary>
    public string? Source { get; set; }

    /// <summary>Only accounts with no approved membership in an active tenant.</summary>
    public bool NoTenant { get; set; }

    /// <summary>Only accounts with an approved membership in this tenant.</summary>
    public string? TenantId { get; set; }
}

public class AccountMembershipDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime JoinedAt { get; set; }
    public bool Pending { get; set; }
    public bool TenantActive { get; set; }

    /// <summary>See Constants.MembershipJoinSources.</summary>
    public string JoinSource { get; set; } = string.Empty;
    public int? InvitationId { get; set; }
}

public class AccountOverviewDto
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? DiscordName { get; set; }
    public DateTime? CreatedAt { get; set; }

    /// <summary>How the account was created: "Password", "Steam", "Discord".</summary>
    public string RegistrationSource { get; set; } = string.Empty;

    /// <summary>Every way the account can sign in today: "Password" and/or provider names.</summary>
    public List<string> SignInMethods { get; set; } = new();

    /// <summary>Provider → provider user id (SteamID64 claimed-id URL, Discord snowflake). Superadmin-only data.</summary>
    public Dictionary<string, string> ExternalIds { get; set; } = new();

    public DateTime? LastLoginAt { get; set; }
    public bool IsSuperAdmin { get; set; }
    public List<AccountMembershipDto> Memberships { get; set; } = new();
}

public class AccountOverviewSummaryDto
{
    public int Total { get; set; }
    public Dictionary<string, int> ByRegistrationSource { get; set; } = new();
    public Dictionary<string, int> BySignInMethod { get; set; } = new();
    public int WithoutTenant { get; set; }
}

public class AccountOverviewPageDto
{
    public List<AccountOverviewDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public AccountOverviewSummaryDto Summary { get; set; } = new();
}
