using Microsoft.AspNetCore.Identity;

namespace Platform.Web.Models;

public class ApplicationUser : IdentityUser
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
