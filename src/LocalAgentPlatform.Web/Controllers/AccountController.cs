using System.Security.Claims;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private readonly PlatformDbContext _db;
    public AccountController(PlatformDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Register()
    {
        // First-run convenience: if no users exist yet, registration is open.
        // Otherwise, only an already-authenticated user can create another account.
        var anyUsers = await _db.Users.AnyAsync();
        if (anyUsers && !(User.Identity?.IsAuthenticated ?? false)) return RedirectToAction(nameof(Login));
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(string userName, string password)
    {
        var anyUsers = await _db.Users.AnyAsync();
        if (anyUsers && !(User.Identity?.IsAuthenticated ?? false)) return RedirectToAction(nameof(Login));

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            ModelState.AddModelError("", "Username is required and password must be at least 8 characters.");
            return View();
        }

        if (await _db.Users.AnyAsync(u => u.UserName == userName))
        {
            ModelState.AddModelError("", "That username is already taken.");
            return View();
        }

        var user = new AppUser { UserName = userName, PasswordHash = PasswordHasher.Hash(password) };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string userName, string password, string? returnUrl)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName == userName);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            ModelState.AddModelError("", "Invalid username or password.");
            return View();
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName)
        }, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction("Index", "Dashboard");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }
}
