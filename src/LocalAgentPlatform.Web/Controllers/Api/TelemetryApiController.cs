using LocalAgentPlatform.Shared.Data;
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

    public TelemetryApiController(IHardwareTelemetryProvider hardware, PlatformDbContext db)
    {
        _hardware = hardware;
        _db = db;
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
        var records = await _db.TokenUsageRecords.ToListAsync(ct);
        return Ok(new TokenUsageSummaryDto(
            records.Sum(r => r.InputTokens), records.Sum(r => r.OutputTokens), records.Count));
    }
}
