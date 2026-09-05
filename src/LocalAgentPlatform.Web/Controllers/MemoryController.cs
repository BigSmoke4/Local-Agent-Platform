using LocalAgentPlatform.Modules.Memory.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

public class MemoryController : Controller
{
    private readonly PlatformDbContext _db;
    private readonly MemoryWriteService _writeService;

    public MemoryController(PlatformDbContext db, MemoryWriteService writeService)
    {
        _db = db;
        _writeService = writeService;
    }

    public async Task<IActionResult> Index(Guid? repositoryId, CancellationToken ct)
    {
        var entries = await _db.MemoryEntries
            .Where(m => repositoryId == null || m.RepositoryId == repositoryId)
            .OrderByDescending(m => m.CreatedAtUtc)
            .Take(100)
            .ToListAsync(ct);

        return View(new MemoryIndexViewModel
        {
            Entries = entries,
            Repositories = await _db.Repositories.OrderBy(r => r.LocalPath).ToListAsync(ct),
            SelectedRepositoryId = repositoryId
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(
        string scope, string title, string content, string? tags, Guid? repositoryId, double baseImportance, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
        {
            TempData["MemoryError"] = "Title and content are required.";
            return RedirectToAction(nameof(Index), new { repositoryId });
        }

        Guid? projectId = null;
        if (repositoryId is not null)
        {
            var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
            projectId = repo?.ProjectId;
        }

        await _writeService.AddManualAsync(scope, title, content, tags, repositoryId, projectId, baseImportance, ct);
        return RedirectToAction(nameof(Index), new { repositoryId });
    }
}

public class MemoryIndexViewModel
{
    public IReadOnlyList<MemoryEntry> Entries { get; set; } = Array.Empty<MemoryEntry>();
    public IReadOnlyList<Repository> Repositories { get; set; } = Array.Empty<Repository>();
    public Guid? SelectedRepositoryId { get; set; }
}
