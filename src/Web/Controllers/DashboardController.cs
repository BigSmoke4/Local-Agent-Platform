using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;
using Platform.Web.Services;

namespace Platform.Web.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly PlatformDbContext _db;
    private readonly IModelProvider _modelProvider;

    public DashboardController(PlatformDbContext db, IModelProvider modelProvider)
    {
        _db = db;
        _modelProvider = modelProvider;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = new DashboardViewModel
        {
            DefaultModel = await _db.Models.FirstOrDefaultAsync(m => m.IsDefault, ct),
            TotalAgentSessions = await _db.AgentSessions.CountAsync(ct),
            ActiveAgentSessions = await _db.AgentSessions.CountAsync(
                s => s.State != AgentSessionState.Completed
                     && s.State != AgentSessionState.Failed
                     && s.State != AgentSessionState.Cancelled, ct),
            LatestSession = await _db.AgentSessions
                .Include(s => s.ToolExecutions)
                .OrderByDescending(s => s.CreatedAtUtc)
                .FirstOrDefaultAsync(ct),
            TotalToolExecutions = await _db.ToolExecutions.CountAsync(ct),
            FailedToolExecutions = await _db.ToolExecutions.CountAsync(t => !t.Succeeded, ct),
            RecentAuditLogs = await _db.AuditLogs
                .OrderByDescending(a => a.OccurredAtUtc)
                .Take(10)
                .ToListAsync(ct),
        };

        // Real health check against the local runtime — never fabricated.
        var health = await _modelProvider.CheckHealthAsync(ct);
        vm.ModelRuntimeHealthy = health.IsHealthy;
        vm.ModelRuntimeMessage = health.Message;

        vm.AvailableRuntimeModels = health.IsHealthy
            ? await _modelProvider.ListAvailableModelsAsync(ct)
            : Array.Empty<string>();

        return View(vm);
    }
}
