namespace HnHMapperServer.Web.Models;

/// <summary>
/// DTO for users who are registered but not assigned to any tenant
/// </summary>
public class UnassignedUserDto
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? DiscordName { get; set; }
    public DateTime? RegisteredAt { get; set; }
}
