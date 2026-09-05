using LocalAgentPlatform.Modules.RepositoryAnalysis.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using LocalAgentPlatform.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

public class RepositoriesController : Controller
{
    private readonly PlatformDbContext _db;
    private readonly IBackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;

    public RepositoriesController(PlatformDbContext db, IBackgroundTaskQueue queue, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _queue = queue;
        _scopeFactory = scopeFactory;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var repos = await _db.Repositories
            .Include(r => r.Project)
            .OrderByDescending(r => r.LastIndexedAtUtc)
            .ToListAsync(ct);

        var latestJobs = await _db.RepositoryIndexingJobs
            .GroupBy(j => j.RepositoryId)
            .Select(g => g.OrderByDescending(j => j.QueuedAtUtc).First())
            .ToListAsync(ct);

        var fileCounts = await _db.FileSnapshots
            .Where(f => !f.IsDeleted)
            .GroupBy(f => f.RepositoryId)
            .Select(g => new { RepositoryId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var symbolCounts = await _db.CodeSymbols
            .GroupBy(s => s.RepositoryId)
            .Select(g => new { RepositoryId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var vm = repos.Select(r => new RepositoryRowViewModel
        {
            Id = r.Id,
            ProjectName = r.Project?.Name ?? "(unknown)",
            LocalPath = r.LocalPath,
            LastIndexedAtUtc = r.LastIndexedAtUtc,
            LatestJobStatus = latestJobs.FirstOrDefault(j => j.RepositoryId == r.Id)?.Status,
            FileCount = fileCounts.FirstOrDefault(f => f.RepositoryId == r.Id)?.Count ?? 0,
            SymbolCount = symbolCounts.FirstOrDefault(s => s.RepositoryId == r.Id)?.Count ?? 0
        }).ToList();

        ViewBag.Projects = await _db.Projects.OrderBy(p => p.Name).ToListAsync(ct);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateProject(string name, CancellationToken ct)
    {
        // Minimal project creation so a repository has somewhere to attach to.
        // Full Projects module (workspace stats, branches, etc. — Section 36) is not
        // implemented yet; this is the smallest real slice needed to unblock Phase 3.
        var project = new LocalAgentPlatform.Shared.Data.Entities.Project
        {
            OwnerUserId = Guid.Empty, // no auth/session wiring yet (Section 38 — extension point)
            Name = name
        };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(Guid projectId, string localPath, CancellationToken ct)
    {
        if (!Directory.Exists(localPath))
        {
            TempData["RepoError"] = $"Path does not exist on this host: {localPath}";
            return RedirectToAction(nameof(Index));
        }

        var repo = new Repository { ProjectId = projectId, LocalPath = localPath };
        _db.Repositories.Add(repo);
        await _db.SaveChangesAsync(ct);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult TriggerIndex(Guid repositoryId)
    {
        // Runs on the background queue (Section 40) — never blocks this HTTP request.
        // A fresh DI scope is created inside the work item because PlatformDbContext
        // is scoped and the queue drains outside any HTTP request scope.
        _queue.QueueBackgroundWorkItem(async ct =>
        {
            using var scope = _scopeFactory.CreateScope();
            var indexingService = scope.ServiceProvider.GetRequiredService<IRepositoryIndexingService>();
            await indexingService.RunIndexingAsync(repositoryId, ct);
        });

        TempData["RepoError"] = null;
        TempData["RepoInfo"] = "Indexing queued — refresh in a moment to see progress.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Symbols(Guid repositoryId, CancellationToken ct)
    {
        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo is null) return NotFound();

        var symbols = await _db.CodeSymbols
            .Where(s => s.RepositoryId == repositoryId)
            .OrderBy(s => s.ContainingNamespace).ThenBy(s => s.ContainingTypeName).ThenBy(s => s.Name)
            .Take(500) // pagination placeholder — full paging is a Phase 3 follow-up, not implemented yet
            .ToListAsync(ct);

        ViewBag.RepositoryPath = repo.LocalPath;
        return View(symbols);
    }
}
