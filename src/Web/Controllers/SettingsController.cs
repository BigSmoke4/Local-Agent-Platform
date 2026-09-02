using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Web.Models;
using Platform.Web.Services.Autonomy;

namespace Platform.Web.Controllers;

/// <summary>
/// Real UI over AutonomyService — same enforcement point used by
/// AgentVerificationController's repair gate, not a separate/decorative
/// toggle.
/// </summary>
[Authorize]
[Route("settings")]
public class SettingsController : Controller
{
    private readonly AutonomyService _autonomy;

    public SettingsController(AutonomyService autonomy)
    {
        _autonomy = autonomy;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = User.Identity?.Name ?? "anonymous";
        var level = await _autonomy.GetLevelAsync(userId, ct);
        return View("~/Views/Settings/Index.cshtml", level);
    }

    [HttpPost("autonomy")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAutonomy(AutonomyLevel level, CancellationToken ct)
    {
        var userId = User.Identity?.Name ?? "anonymous";
        await _autonomy.SetLevelAsync(userId, level, ct);
        return RedirectToAction(nameof(Index));
    }
}
