using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Telemetry;
using LocalAgentPlatform.Web.Models.Api;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers.Api;

[ApiController]
[Route("api/telemetry")]
[EnableRateLimiting("api")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public sealed class TelemetryApiController : ControllerBase
{
    private readonly IHardwareTelemetryProvider _hardware;
    private readonly PlatformDbContext _db;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public TelemetryApiController(
        IHardwareTelemetryProvider hardware,
        PlatformDbContext db,
        IWorkspaceRootPolicy workspacePolicy)
    {
        _hardware = hardware;
        _db = db;
        _workspacePolicy = workspacePolicy;
    }

    [HttpGet("hardware")]
    public async Task<ActionResult<HardwareTelemetryDto>> Hardware(CancellationToken ct)
    {
        var s = await _hardware.GetSnapshotAsync(ct);
        return Ok(new HardwareTelemetryDto(
            s.TimestampUtc, s.CpuUtilizationPercent, s.RamUsedBytes, s.RamTotalBytes,
            s.GpuUtilizationPercent, s.GpuVramUsedBytes, s.GpuVramTotalBytes));
    }

    [HttpGet("tokens")]
    public async Task<ActionResult<TokenUsageSummaryDto>> Tokens(CancellationToken ct)
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
        var totals = await _db.TokenUsageRecords
            .Where(r => _db.AgentSessions.Any(s => s.Id == r.AgentSessionId && s.OwnerUserId == userId &&
                                                   visibleRepositoryIds.Contains(s.RepositoryId)))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Input = g.Sum(r => r.InputTokens),
                Output = g.Sum(r => r.OutputTokens),
                Count = g.Count()
            })
            .FirstOrDefaultAsync(ct);
        return Ok(new TokenUsageSummaryDto(totals?.Input ?? 0, totals?.Output ?? 0, totals?.Count ?? 0));
    }
}
