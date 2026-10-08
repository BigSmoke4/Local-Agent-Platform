using System.Security.Claims;
using System.Security.Cryptography;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Security;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    // Equalize the expensive password-hash work for unknown users to reduce username
    // enumeration by timing; this random dummy hash is never accepted or persisted.
    private static readonly string DummyPasswordHash = PasswordHasher.Hash(
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    private readonly PlatformDbContext _db;
    private readonly TotpService _totp;
    private readonly IDataProtector _mfaProtector;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public AccountController(
        PlatformDbContext db,
        TotpService totp,
        IDataProtectionProvider dataProtection,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _db = db;
        _totp = totp;
        _mfaProtector = dataProtection.CreateProtector("LocalAgentPlatform.MfaSecret.v1");
        _configuration = configuration;
        _environment = environment;
    }

    [HttpGet]
    public async Task<IActionResult> Register()
    {
        var anyUsers = await _db.Users.AnyAsync();
        if (anyUsers && (!(User.Identity?.IsAuthenticated ?? false) || !User.IsAdmin())) return RedirectToAction(nameof(Login));
        SetRegistrationViewState(anyUsers);
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(string userName, string password, string? bootstrapToken, CancellationToken ct)
    {
        // Serialize the bootstrap decision so two simultaneous first registrations
        // cannot both become administrators. This application targets PostgreSQL.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(764831245);", ct);
        var anyUsers = await _db.Users.AnyAsync(ct);
        if (anyUsers && (!(User.Identity?.IsAuthenticated ?? false) || !User.IsAdmin())) return Forbid();
        SetRegistrationViewState(anyUsers);

        // In production the first administrator must prove possession of a deployment
        // secret delivered out-of-band. Development stays convenient for loopback setup.
        if (!anyUsers && !_environment.IsDevelopment() &&
            !BootstrapTokenVerifier.Verify(_configuration["Security:BootstrapAdminToken"], bootstrapToken))
        {
            ModelState.AddModelError("", "A valid deployment bootstrap token is required before the first production administrator can be created. Configure Security__BootstrapAdminToken (at least 32 bytes) and retry.");
            return View();
        }

        var normalizedUserName = userName?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedUserName, @"^[a-z0-9_.-]{3,64}$") ||
            string.IsNullOrEmpty(password) || password.Length < 12 || password.Length > 256)
        {
            ModelState.AddModelError("", "Username must be 3–64 letters, digits, dots, underscores, or hyphens; password must be 12–256 characters.");
            return View();
        }

        if (await _db.Users.AnyAsync(u => u.UserName.ToLower() == normalizedUserName, ct))
        {
            ModelState.AddModelError("", "That username is already taken.");
            return View();
        }

        var recoveryCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var user = new AppUser
        {
            UserName = normalizedUserName,
            PasswordHash = PasswordHasher.Hash(password),
            Role = anyUsers ? "User" : "Admin",
            RecoveryCodeHash = PasswordHasher.Hash(recoveryCode)
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        TempData["RecoveryCode"] = recoveryCode;
        return RedirectToAction(nameof(RecoveryCode));
    }

    [HttpGet]
    public IActionResult RecoveryCode()
    {
        if (TempData["RecoveryCode"] is not string code) return RedirectToAction(nameof(Login));
        ViewBag.RecoveryCode = code;
        return View();
    }

    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(string userName, string password, string? mfaCode, string? returnUrl)
    {
        var normalizedUserName = userName is { Length: <= 256 } ? userName.Trim().ToLowerInvariant() : string.Empty;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName.ToLower() == normalizedUserName);
        if (password is null || password.Length > 256)
        {
            ModelState.AddModelError("", "Invalid username or password.");
            return View();
        }
        var passwordVerified = PasswordHasher.Verify(password, user?.PasswordHash ?? DummyPasswordHash);
        if (user is null || !passwordVerified)
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
            string secret;
            try { secret = _mfaProtector.Unprotect(user.MfaSecretProtected); }
            catch (CryptographicException)
            {
                ModelState.AddModelError("", "MFA account data could not be decrypted. Use account recovery.");
                return View();
            }
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
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> BeginMfa(CancellationToken ct)
    {
        var user = await _db.Users.FindAsync(new object[] { User.RequireUserId() }, ct);
        if (user is null) return NotFound();
        if (user.MfaEnabled)
        {
            TempData["SecurityError"] = "MFA is already enabled. Verify your password before disabling it, then set it up again.";
            return RedirectToAction(nameof(Security));
        }
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
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ConfirmMfa(string code, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync(new object[] { User.RequireUserId() }, ct);
        if (user?.MfaSecretProtected is null) return BadRequest("Start MFA setup first.");
        string secret;
        try { secret = _mfaProtector.Unprotect(user.MfaSecretProtected); }
        catch (CryptographicException)
        {
            TempData["SecurityError"] = "MFA setup data could not be decrypted. Start setup again.";
            user.MfaSecretProtected = null;
            user.MfaEnabled = false;
            await _db.SaveChangesAsync(ct);
            return RedirectToAction(nameof(Security));
        }
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
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> DisableMfa(string password, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync(new object[] { User.RequireUserId() }, ct);
        if (user is null || string.IsNullOrEmpty(password) || password.Length > 256 ||
            !PasswordHasher.Verify(password, user.PasswordHash)) return Forbid();
        user.MfaEnabled = false;
        user.MfaSecretProtected = null;
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Security));
    }

    [HttpGet]
    public IActionResult ResetPassword() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(string userName, string recoveryCode, string newPassword, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 12 || newPassword.Length > 256)
        {
            ModelState.AddModelError("", "New password must be 12–256 characters.");
            return View();
        }
        if (userName is not { Length: <= 256 } || recoveryCode is not { Length: 32 } ||
            recoveryCode.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f')))
        {
            ModelState.AddModelError("", "Invalid username or recovery code.");
            return View();
        }
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        // Recovery codes are single-use. The lock closes the race where two simultaneous
        // requests could both verify the old code before either rotates it.
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(764831246);", ct);
        var normalizedUserName = userName?.Trim().ToLowerInvariant() ?? string.Empty;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName.ToLower() == normalizedUserName, ct);
        var recoveryCodeVerified = PasswordHasher.Verify(recoveryCode, user?.RecoveryCodeHash ?? DummyPasswordHash);
        if (user?.RecoveryCodeHash is null || !recoveryCodeVerified)
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
        await transaction.CommitAsync(ct);
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

    private void SetRegistrationViewState(bool anyUsers)
    {
        ViewBag.RequiresBootstrapToken = !anyUsers && !_environment.IsDevelopment();
        ViewBag.BootstrapTokenConfigured = !string.IsNullOrWhiteSpace(_configuration["Security:BootstrapAdminToken"]);
    }
}
