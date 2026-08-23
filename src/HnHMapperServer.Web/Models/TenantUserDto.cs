namespace HnHMapperServer.Web.Models;

/// <summary>
/// DTO for users in a tenant (for TenantUserManagement)
/// </summary>
public class TenantUserDto
{
    /// <summary>
    /// User ID
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Username
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Role in the tenant (TenantAdmin or TenantUser)
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// List of permissions (Map, Markers, Pointer, Upload, Writer)
    /// </summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// When the user joined the tenant
    /// </summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>
    /// Whether this is the current user
    /// </summary>
    public bool IsCurrentUser { get; set; }

    /// <summary>
    /// Number of tokens the user has created
    /// </summary>
    public int TokenCount { get; set; }

    /// <summary>How the membership was created (InviteLink, SelfCreated, AdminAssigned, Approved, Bootstrap, Legacy).</summary>
    public string JoinSource { get; set; } = string.Empty;

    /// <summary>"Password" and/or provider names ("Steam", "Discord").</summary>
    public List<string> SignInMethods { get; set; } = new();

    public string RegistrationSource { get; set; } = string.Empty;
}
