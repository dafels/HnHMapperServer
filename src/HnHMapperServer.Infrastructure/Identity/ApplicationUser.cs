using Microsoft.AspNetCore.Identity;

namespace HnHMapperServer.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string? DiscordName { get; set; }

    public DateTime? CreatedAt { get; set; }
}
