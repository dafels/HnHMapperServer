using HnHMapperServer.Core.Enums;

namespace HnHMapperServer.Core.Models;

/// <summary>
/// Junction table linking users to tenants with roles.
/// Stores role as enum in code, string in database.
/// </summary>
public sealed class TenantUserEntity
{
    /// <summary>
    /// Auto-increment primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to Tenants
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to AspNetUsers
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    private string _roleString = string.Empty;

    /// <summary>
    /// Role enum value (TenantAdmin or TenantUser).
    /// Stored as string in database for compatibility.
    /// </summary>
    public TenantRole Role
    {
        get => string.IsNullOrEmpty(_roleString)
            ? TenantRole.TenantUser
            : Enum.Parse<TenantRole>(_roleString, ignoreCase: true);
        set => _roleString = value.ToString();
    }

    /// <summary>
    /// Internal property for EF Core to map to database column.
    /// DO NOT use this directly in application code - use Role property instead.
    /// </summary>
    public string RoleString
    {
        get => _roleString;
        set => _roleString = value;
    }

    /// <summary>
    /// ISO 8601 UTC timestamp when user joined tenant
    /// </summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>
    /// Whether this user is pending approval by a tenant admin
    /// </summary>
    public bool PendingApproval { get; set; }

    /// <summary>How this membership came to exist (see Constants.MembershipJoinSources). "Legacy" for rows that predate tracking.</summary>
    public string JoinSource { get; set; } = HnHMapperServer.Core.Constants.MembershipJoinSources.Legacy;

    /// <summary>The invitation that was redeemed to create this membership, when JoinSource is InviteLink.</summary>
    public int? InvitationId { get; set; }

    /// <summary>
    /// Navigation property to tenant permissions
    /// </summary>
    public ICollection<TenantPermissionEntity> Permissions { get; set; } = new List<TenantPermissionEntity>();
}
