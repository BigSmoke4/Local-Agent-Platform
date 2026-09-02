using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;

namespace Platform.Web.Controllers;

/// <summary>
/// Real UI over the existing /api/models registry — same PlatformDbContext
/// query, rendered as a page instead of JSON.
/// </summary>
[Authorize]
[Route("model-manager")]
public class ModelManagerController : Controller
{
    private readonly PlatformDbContext _db;

    public ModelManagerController(PlatformDbContext db)
    {
        _db = db;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var models = await _db.Models.OrderByDescending(m => m.RegisteredAtUtc).ToListAsync(ct);
        return View("~/Views/Models/Index.cshtml", models);
    }
}
