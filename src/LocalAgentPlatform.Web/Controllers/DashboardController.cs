using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Models;
using LocalAgentPlatform.Shared.Kernel.Telemetry;
using LocalAgentPlatform.Web.Models;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

public class DashboardController : Controller
{
    private readonly IModelProvider _modelProvider;
    private readonly IHardwareTelemetryProvider _hardware;
    private readonly PlatformDbContext _db;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public DashboardController(
        IModelProvider modelProvider,
        IHardwareTelemetryProvider hardware,
        PlatformDbContext db,
        IWorkspaceRootPolicy workspacePolicy)
    {
        _modelProvider = modelProvider;
        _hardware = hardware;
        _db = db;
        _workspacePolicy = workspacePolicy;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var repositories = await _db.Repositories
            .Where(r => r.Project!.OwnerUserId == userId)
            .Select(r => new { r.Id, r.LocalPath })
            .ToListAsync(ct);
        var visibleRepositoryIds = repositories
            .Where(r => _workspacePolicy.IsAllowed(r.LocalPath))
            .Select(r => r.Id)
            .ToArray();
        var health = await _modelProvider.CheckHealthAsync(ct);

        IReadOnlyList<ModelDescriptor> models = Array.Empty<ModelDescriptor>();
        if (health.IsHealthy)
        {
            try { models = await _modelProvider.ListModelsAsync(ct); }
            catch { /* surfaced via ModelProviderHealthy=false path already; avoid crashing dashboard */ }
        }

        var hw = await _hardware.GetSnapshotAsync(ct);

        var vm = new DashboardViewModel
        {
            ModelProviderHealthy = health.IsHealthy,
            ModelProviderDetail = health.Detail,
            AvailableModels = models.Select(m => new ModelRowViewModel
            {
                Id = m.Id,
                Quantization = m.Quantization,
                ParameterCount = m.ParameterCount,
                FileSizeBytes = m.FileSizeBytes
            }).ToList(),
            CpuUtilizationPercent = hw.CpuUtilizationPercent,
            RamUsedBytes = hw.RamUsedBytes,
            RamTotalBytes = hw.RamTotalBytes,
            GpuUtilizationPercent = hw.GpuUtilizationPercent,
            GpuVramUsedBytes = hw.GpuVramUsedBytes,
            GpuVramTotalBytes = hw.GpuVramTotalBytes,
            ActiveAgentSessionCount = await _db.AgentSessions.CountAsync(
                s => s.OwnerUserId == userId && visibleRepositoryIds.Contains(s.RepositoryId) &&
                     s.State != "Completed" && s.State != "Failed" && s.State != "Cancelled", ct),
            TotalProjectCount = await _db.Projects.CountAsync(p => p.OwnerUserId == userId, ct),
            TotalRepositoryCount = visibleRepositoryIds.Length
        };

        return View(vm);
    }
}
