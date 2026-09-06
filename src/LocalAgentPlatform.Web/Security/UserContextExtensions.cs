using System.Security.Claims;

namespace LocalAgentPlatform.Web.Security;

public static class UserContextExtensions
{
    public static Guid RequireUserId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : throw new UnauthorizedAccessException("Authenticated user id claim is missing or invalid.");
    }

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole("Admin");
}
