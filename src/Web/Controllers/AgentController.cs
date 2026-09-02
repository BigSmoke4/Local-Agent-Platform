using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;
using Platform.Web.Services;
using Platform.Web.Services.Tools;

namespace Platform.Web.Controllers;

/// <summary>
/// Minimal real agent loop: Created -> Understanding -> Executing -> Completed/Failed.
/// This intentionally does NOT claim to do repository analysis, planning graphs,
/// build/test verification, or self-repair — those are unimplemented extension
/// points (see ARCHITECTURE.md), not faked here.
/// </summary>
[Authorize]
[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IModelProvider _modelProvider;
    private readonly CalculatorTool _calculator;
    private readonly Services.Routing.ModelRouter _router;
    private readonly ILogger<AgentController> _logger;

    public AgentController(
        PlatformDbContext db,
        IModelProvider modelProvider,
        CalculatorTool calculator,
        Services.Routing.ModelRouter router,
        ILogger<AgentController> logger)
    {
        _db = db;
        _modelProvider = modelProvider;
        _calculator = calculator;
        _router = router;
        _logger = logger;
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions(CancellationToken ct)
        => Ok(await _db.AgentSessions
            .OrderByDescending(s => s.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct));

    [HttpGet("sessions/{id:guid}")]
    public async Task<IActionResult> GetSession(Guid id, CancellationToken ct)
    {
        var session = await _db.AgentSessions
            .Include(s => s.ToolExecutions)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        return session is null ? NotFound() : Ok(session);
    }

    public record RunRequest(string UserRequest);

    [HttpPost("run")]
    public async Task<IActionResult> Run(RunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserRequest))
            return BadRequest("UserRequest is required.");

        var routing = await _router.RouteAsync(request.UserRequest, ct);

        // Trivial (arithmetic) requests route to no model at all — handled below.
        if (routing.Complexity != Services.Routing.TaskComplexity.Trivial && routing.SelectedModel is null)
            return UnprocessableEntity("No models registered. POST /api/models first.");

        var session = new AgentSession
        {
            UserRequest = request.UserRequest,
            ModelId = routing.SelectedModel?.Id,
            State = AgentSessionState.Understanding
        };
        _db.AgentSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Routing decision for session {SessionId}: {Reason}", session.Id, routing.Reason);

        try
        {
            // Deterministic-first rule: arithmetic never goes to the model.
            if (TryExtractArithmetic(request.UserRequest, out var expression))
            {
                var toolExec = new ToolExecution
                {
                    AgentSessionId = session.Id,
                    ToolName = _calculator.Name,
                    InputJson = expression
                };

                try
                {
                    var result = _calculator.Evaluate(expression!);
                    toolExec.Succeeded = true;
                    toolExec.OutputJson = result.ToString();
                    toolExec.CompletedAtUtc = DateTime.UtcNow;
                    session.FinalResult = $"{expression} = {result}";
                }
                catch (Exception ex)
                {
                    toolExec.Succeeded = false;
                    toolExec.ErrorMessage = ex.Message;
                    toolExec.CompletedAtUtc = DateTime.UtcNow;
                    throw;
                }
                finally
                {
                    _db.ToolExecutions.Add(toolExec);
                }
            }
            else
            {
                session.State = AgentSessionState.Executing;
                await _db.SaveChangesAsync(ct);

                var sw = Stopwatch.StartNew();
                var generation = await _modelProvider.GenerateAsync(
                    new GenerationRequest(routing.SelectedModel!.RuntimeId, request.UserRequest), ct);
                sw.Stop();

                session.InputTokens = generation.InputTokens;
                session.OutputTokens = generation.OutputTokens;
                session.FinalResult = generation.Text;

                _logger.LogInformation(
                    "Agent session {SessionId} generated {OutputTokens} tokens in {Ms}ms ({Tps} tok/s)",
                    session.Id, generation.OutputTokens, sw.Elapsed.TotalMilliseconds, generation.TokensPerSecond);
            }

            session.State = AgentSessionState.Completed;
            session.CompletedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            session.State = AgentSessionState.Failed;
            session.ErrorMessage = ex.Message;
            session.CompletedAtUtc = DateTime.UtcNow;
            _logger.LogError(ex, "Agent session {SessionId} failed", session.Id);
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AgentSessionCompleted",
            Details = $"{session.Id}: {session.State}"
        });

        await _db.SaveChangesAsync(ct);
        return Ok(session);
    }

    private static bool TryExtractArithmetic(string input, out string? expression)
    {
        var trimmed = input.Trim();
        var isArithmetic = trimmed.Length > 0 &&
            trimmed.All(c => char.IsDigit(c) || "+-*/(). ".Contains(c));

        expression = isArithmetic ? trimmed : null;
        return isArithmetic;
    }
}
