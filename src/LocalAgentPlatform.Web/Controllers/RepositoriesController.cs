using LocalAgentPlatform.Modules.RepositoryAnalysis.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Web.Models;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

public class RepositoriesController : Controller
{
    private readonly PlatformDbContext _db;
    private readonly IBackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public RepositoriesController(PlatformDbContext db, IBackgroundTaskQueue queue, IServiceScopeFactory scopeFactory, IWorkspaceRootPolicy workspacePolicy)
    {
        _db = db;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _workspacePolicy = workspacePolicy;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var repos = await _db.Repositories
            .Include(r => r.Project)
            .Where(r => r.Project!.OwnerUserId == userId)
            .OrderByDescending(r => r.LastIndexedAtUtc)
            .ToListAsync(ct);
        repos = repos.Where(r => _workspacePolicy.IsAllowed(r.LocalPath)).ToList();

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
            .Where(s => s.FileSnapshot != null && !s.FileSnapshot.IsDeleted)
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

        ViewBag.Projects = await _db.Projects.Where(p => p.OwnerUserId == userId).OrderBy(p => p.Name).ToListAsync(ct);
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
            OwnerUserId = User.RequireUserId(),
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
        if (string.IsNullOrWhiteSpace(localPath) || !Path.IsPathFullyQualified(localPath) || !Directory.Exists(localPath))
        {
            TempData["RepoError"] = "Choose an existing absolute directory on this host.";
            return RedirectToAction(nameof(Index));
        }
        var normalizedPath = Path.GetFullPath(localPath);
        if (!_workspacePolicy.IsAllowed(normalizedPath))
        {
            TempData["RepoError"] = "This directory is outside the configured repository workspace roots.";
            return RedirectToAction(nameof(Index));
        }

        var userId = User.RequireUserId();
        if (!await _db.Projects.AnyAsync(p => p.Id == projectId && p.OwnerUserId == userId, ct)) return NotFound();
        var repo = new Repository { ProjectId = projectId, LocalPath = normalizedPath };
        _db.Repositories.Add(repo);
        await _db.SaveChangesAsync(ct);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TriggerIndex(Guid repositoryId, CancellationToken requestCt)
    {
        var userId = User.RequireUserId();
        var repoPath = await _db.Repositories.Where(r => r.Id == repositoryId && r.Project!.OwnerUserId == userId)
            .Select(r => r.LocalPath).FirstOrDefaultAsync(requestCt);
        if (repoPath is null || !_workspacePolicy.IsAllowed(repoPath)) return NotFound();
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

    public async Task<IActionResult> Symbols(Guid repositoryId, int page = 1, int pageSize = 100, string? q = null, CancellationToken ct = default)
    {
        var userId = User.RequireUserId();
        var repo = await _db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId && r.Project!.OwnerUserId == userId, ct);
        if (repo is null || !_workspacePolicy.IsAllowed(repo.LocalPath)) return NotFound();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 20, 250);
        var query = _db.CodeSymbols.Where(s => s.RepositoryId == repositoryId &&
            s.FileSnapshot != null && !s.FileSnapshot.IsDeleted);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => EF.Functions.ILike(s.Name, $"%{q}%") || (s.Signature != null && EF.Functions.ILike(s.Signature, $"%{q}%")));
        var total = await query.CountAsync(ct);
        var symbols = await query.OrderBy(s => s.ContainingNamespace).ThenBy(s => s.ContainingTypeName).ThenBy(s => s.Name)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        ViewBag.RepositoryPath = repo.LocalPath;
        ViewBag.Page = page; ViewBag.PageSize = pageSize; ViewBag.Total = total; ViewBag.Query = q;
        return View(symbols);
    }
}
