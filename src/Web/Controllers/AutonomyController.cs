using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Web.Models;
using Platform.Web.Services.Autonomy;

namespace Platform.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/settings/autonomy")]
public class AutonomyController : ControllerBase
{
    private readonly AutonomyService _autonomy;

    public AutonomyController(AutonomyService autonomy)
    {
        _autonomy = autonomy;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var userId = User.Identity?.Name ?? "anonymous";
        var level = await _autonomy.GetLevelAsync(userId, ct);
        return Ok(new { level = level.ToString() });
    }

    public record SetAutonomyRequest(AutonomyLevel Level);

    [HttpPost]
    public async Task<IActionResult> Set(SetAutonomyRequest request, CancellationToken ct)
    {
        var userId = User.Identity?.Name ?? "anonymous";
        await _autonomy.SetLevelAsync(userId, request.Level, ct);
        return Ok(new { level = request.Level.ToString() });
    }
}
