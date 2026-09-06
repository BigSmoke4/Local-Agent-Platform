using System.Security.Claims;
using System.Security.Cryptography;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private readonly PlatformDbContext _db;
    private readonly TotpService _totp;
    private readonly IDataProtector _mfaProtector;

    public AccountController(PlatformDbContext db, TotpService totp, IDataProtectionProvider dataProtection)
    {
        _db = db;
        _totp = totp;
        _mfaProtector = dataProtection.CreateProtector("LocalAgentPlatform.MfaSecret.v1");
    }

    [HttpGet]
    public async Task<IActionResult> Register()
    {
        var anyUsers = await _db.Users.AnyAsync();
        if (anyUsers && (!(User.Identity?.IsAuthenticated ?? false) || !User.IsAdmin())) return RedirectToAction(nameof(Login));
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(string userName, string password)
    {
        var anyUsers = await _db.Users.AnyAsync();
        if (anyUsers && (!(User.Identity?.IsAuthenticated ?? false) || !User.IsAdmin())) return Forbid();

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password) || password.Length < 12)
        {
            ModelState.AddModelError("", "Username is required and password must be at least 12 characters.");
            return View();
        }

        if (await _db.Users.AnyAsync(u => u.UserName == userName))
        {
            ModelState.AddModelError("", "That username is already taken.");
            return View();
        }

        var recoveryCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var user = new AppUser
        {
            UserName = userName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            Role = anyUsers ? "User" : "Admin",
            RecoveryCodeHash = PasswordHasher.Hash(recoveryCode)
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        TempData["RecoveryCode"] = recoveryCode;
        return RedirectToAction(nameof(RecoveryCode));
    }

    [HttpGet]
    public IActionResult RecoveryCode()
    {
        if (TempData.Peek("RecoveryCode") is not string code) return RedirectToAction(nameof(Login));
        ViewBag.RecoveryCode = code;
        return View();
    }

    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string userName, string password, string? mfaCode, string? returnUrl)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName == userName);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            ModelState.AddModelError("", "Invalid username or password.");
            return View();
        }

        if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(user.MfaSecretProtected))
            {
                ModelState.AddModelError("", "MFA is enabled but the account secret is unavailable. Use account recovery.");
                return View();
            }
            var secret = _mfaProtector.Unprotect(user.MfaSecretProtected);
            if (!_totp.Verify(secret, mfaCode ?? string.Empty))
            {
                ModelState.AddModelError("", "A valid 6-digit authenticator code is required.");
                ViewBag.RequireMfa = true;
                return View();
            }
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim(ClaimTypes.Role, user.Role)
        }, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction("Index", "Dashboard");
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Security(CancellationToken ct)
    {
        var user = await _db.Users.FindAsync(new object[] { User.RequireUserId() }, ct);
        if (user is null) return NotFound();
        return View(user);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> BeginMfa(CancellationToken ct)
    {
        var user = await _db.Users.FindAsync(new object[] { User.RequireUserId() }, ct);
        if (user is null) return NotFound();
        var secret = _totp.GenerateSecret();
        user.MfaSecretProtected = _mfaProtector.Protect(secret);
        user.MfaEnabled = false;
        await _db.SaveChangesAsync(ct);
        TempData["MfaSecret"] = secret;
        return RedirectToAction(nameof(Security));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> ConfirmMfa(string code, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync(new object[] { User.RequireUserId() }, ct);
        if (user?.MfaSecretProtected is null) return BadRequest("Start MFA setup first.");
        var secret = _mfaProtector.Unprotect(user.MfaSecretProtected);
        if (!_totp.Verify(secret, code))
        {
            TempData["SecurityError"] = "Authenticator code was invalid.";
            return RedirectToAction(nameof(Security));
        }
        user.MfaEnabled = true;
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Security));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> DisableMfa(string password, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync(new object[] { User.RequireUserId() }, ct);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash)) return Forbid();
        user.MfaEnabled = false;
        user.MfaSecretProtected = null;
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Security));
    }

    [HttpGet]
    public IActionResult ResetPassword() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string userName, string recoveryCode, string newPassword, CancellationToken ct)
    {
        if (newPassword.Length < 12)
        {
            ModelState.AddModelError("", "New password must be at least 12 characters.");
            return View();
        }
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName == userName, ct);
        if (user?.RecoveryCodeHash is null || !PasswordHasher.Verify(recoveryCode, user.RecoveryCodeHash))
        {
            ModelState.AddModelError("", "Invalid username or recovery code.");
            return View();
        }

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.MfaEnabled = false;
        user.MfaSecretProtected = null;
        var replacement = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        user.RecoveryCodeHash = PasswordHasher.Hash(replacement);
        await _db.SaveChangesAsync(ct);
        TempData["RecoveryCode"] = replacement;
        return RedirectToAction(nameof(RecoveryCode));
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
