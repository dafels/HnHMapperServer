namespace HnHMapperServer.Core.Constants;

/// <summary>
/// How an account was created. Stored once in AspNetUsers.RegistrationSource and never changed afterwards
/// (a Steam/Discord login linked later does not turn a password account into an external one).
/// </summary>
public static class RegistrationSources
{
    public const string Password = "Password";
    public const string Steam = "Steam";
    public const string Discord = "Discord";

    public static readonly IReadOnlyList<string> All = new[] { Password, Steam, Discord };
}

/// <summary>
/// How a membership row (TenantUsers) came to exist. Shown to admins as "joined via ...".
/// </summary>
public static class MembershipJoinSources
{
    /// <summary>Redeemed a shareable invite link (the invitation id is recorded alongside).</summary>
    public const string InviteLink = "InviteLink";
    /// <summary>Created the tenant through the self-service flow and became its first admin.</summary>
    public const string SelfCreated = "SelfCreated";
    /// <summary>A superadmin assigned the user to the tenant.</summary>
    public const string AdminAssigned = "AdminAssigned";
    /// <summary>A tenant admin approved a legacy pending registration.</summary>
    public const string Approved = "Approved";
    /// <summary>Seeded by the bootstrap admin routine.</summary>
    public const string Bootstrap = "Bootstrap";
    /// <summary>Row predates join-source tracking (back-filled by migration).</summary>
    public const string Legacy = "Legacy";
}
