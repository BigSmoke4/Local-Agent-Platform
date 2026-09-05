using System.Security.Claims;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

[Authorize]
public class ApiKeysController : Controller
{
    private readonly PlatformDbContext _db;
    private readonly ApiKeyService _apiKeyService;

    public ApiKeysController(PlatformDbContext db, ApiKeyService apiKeyService)
    {
        _db = db;
        _apiKeyService = apiKeyService;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var keys = await _db.ApiKeys
            .Where(k => k.OwnerUserId == CurrentUserId)
            .OrderByDescending(k => k.CreatedAtUtc)
            .ToListAsync(ct);
        return View(keys);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, CancellationToken ct)
    {
        var created = await _apiKeyService.CreateAsync(CurrentUserId, string.IsNullOrWhiteSpace(name) ? "API Key" : name, ct);
        TempData["NewApiKey"] = created.RawKey; // shown exactly once
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await _apiKeyService.RevokeAsync(id, ct);
        return RedirectToAction(nameof(Index));
    }
}
