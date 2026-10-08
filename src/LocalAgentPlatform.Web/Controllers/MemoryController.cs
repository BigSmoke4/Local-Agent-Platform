using LocalAgentPlatform.Modules.Memory.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

public class MemoryController : Controller
{
    private readonly PlatformDbContext _db;
    private readonly MemoryWriteService _writeService;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public MemoryController(PlatformDbContext db, MemoryWriteService writeService, IWorkspaceRootPolicy workspacePolicy)
    {
        _db = db;
        _writeService = writeService;
        _workspacePolicy = workspacePolicy;
    }

    public async Task<IActionResult> Index(Guid? repositoryId, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var repositories = await _db.Repositories.Where(r => r.Project!.OwnerUserId == userId)
            .OrderBy(r => r.LocalPath).ToListAsync(ct);
        var visibleRepositories = repositories.Where(r => _workspacePolicy.IsAllowed(r.LocalPath)).ToList();
        var visibleRepositoryIds = visibleRepositories.Select(r => r.Id).ToArray();
        if (repositoryId is { } selectedId && !visibleRepositoryIds.Contains(selectedId)) return NotFound();

        var entries = await _db.MemoryEntries
            .Where(m => m.OwnerUserId == userId &&
                        (m.RepositoryId == null || visibleRepositoryIds.Contains(m.RepositoryId.Value)) &&
                        (repositoryId == null || m.RepositoryId == repositoryId))
            .OrderByDescending(m => m.CreatedAtUtc)
            .Take(100)
            .ToListAsync(ct);

        return View(new MemoryIndexViewModel
        {
            Entries = entries,
            Repositories = visibleRepositories,
            SelectedRepositoryId = repositoryId
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(
        string scope, string title, string content, string? tags, Guid? repositoryId, double baseImportance, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160 || string.IsNullOrWhiteSpace(content) || content.Length > 4_000 ||
            tags?.Length > 500 || !new[] { "LongTerm", "UserPreference", "Working" }.Contains(scope, StringComparer.OrdinalIgnoreCase) ||
            !double.IsFinite(baseImportance))
        {
            TempData["MemoryError"] = "Choose a valid scope; title, content, tags, and importance must be within their limits.";
            return RedirectToAction(nameof(Index), new { repositoryId });
        }

        Guid? projectId = null;
        if (repositoryId is not null)
        {
            var userId = User.RequireUserId();
            var repo = await _db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId && r.Project!.OwnerUserId == userId, ct);
            if (repo is null || !_workspacePolicy.IsAllowed(repo.LocalPath)) return NotFound();
            projectId = repo.ProjectId;
        }

        await _writeService.AddManualAsync(scope, title, content, tags, repositoryId, projectId, baseImportance, User.RequireUserId(), ct);
        return RedirectToAction(nameof(Index), new { repositoryId });
    }
}

public class MemoryIndexViewModel
{
    public IReadOnlyList<MemoryEntry> Entries { get; set; } = Array.Empty<MemoryEntry>();
    public IReadOnlyList<Repository> Repositories { get; set; } = Array.Empty<Repository>();
    public Guid? SelectedRepositoryId { get; set; }
}
