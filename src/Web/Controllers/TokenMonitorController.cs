using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;

namespace Platform.Web.Controllers;

public class TokenMonitorViewModel
{
    public List<AgentSession> RecentSessions { get; set; } = new();
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public double AverageOutputTokensPerSession { get; set; }
}

/// <summary>
/// Real token telemetry screen — every number here comes directly from
/// AgentSession rows populated by OllamaModelProvider's actual response
/// fields (see MODEL_RUNTIME.md). Nothing is estimated on this page.
/// </summary>
[Authorize]
[Route("token-monitor")]
public class TokenMonitorController : Controller
{
    private readonly PlatformDbContext _db;

    public TokenMonitorController(PlatformDbContext db)
    {
        _db = db;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var sessions = await _db.AgentSessions
            .OrderByDescending(s => s.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        var vm = new TokenMonitorViewModel
        {
            RecentSessions = sessions,
            TotalInputTokens = sessions.Sum(s => s.InputTokens),
            TotalOutputTokens = sessions.Sum(s => s.OutputTokens),
            AverageOutputTokensPerSession = sessions.Count > 0 ? sessions.Average(s => s.OutputTokens) : 0
        };

        return View("~/Views/Telemetry/TokenMonitor.cshtml", vm);
    }
}
