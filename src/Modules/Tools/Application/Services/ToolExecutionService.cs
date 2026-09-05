using System.Text.Json;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.Tools.Application.Services;

public sealed record ToolInvocationOutcome(
    Guid ExecutionId,
    string Decision, // Allowed, Denied, PendingApproval
    string? DecisionReason,
    ToolExecutionResult? Result);

/// <summary>
/// The single seam every caller (web console, future Agent engine) goes through to run
/// a tool. Responsibilities: resolve the workspace root for a repository, run the
/// command through <see cref="CommandPolicyEngine"/> for TerminalTool specifically,
/// respect each tool's own RiskLevel/RequiresApproval, and write a full audit trail to
/// Postgres for every attempt — allowed, denied, or pending — per spec Section 37.
/// </summary>
public sealed class ToolExecutionService
{
    private readonly IReadOnlyDictionary<string, ITool> _toolsByName;
    private readonly PlatformDbContext _db;
    private readonly CommandPermissionService _permissions;
    private readonly ILogger<ToolExecutionService> _logger;

    public ToolExecutionService(
        IEnumerable<ITool> tools, PlatformDbContext db, CommandPermissionService permissions, ILogger<ToolExecutionService> logger)
    {
        _toolsByName = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _db = db;
        _permissions = permissions;
        _logger = logger;
    }

    public IReadOnlyList<ITool> AllTools => _toolsByName.Values.ToList();

    /// <summary>
    /// Attempts to run a tool. If the tool (or, for TerminalTool, the specific command)
    /// requires approval and <paramref name="approved"/> is false, no execution happens
    /// and the result comes back as PendingApproval — callers must re-invoke with
    /// approved=true to actually run it. <paramref name="ownerUserId"/> scopes the
    /// persistent Always-Allow/Always-Deny lookup (Section 11) — pass null when there's
    /// no specific human to attribute the call to (e.g. an unattended agent run); in
    /// that case only the static CommandPolicyEngine rules apply.
    /// </summary>
    public async Task<ToolInvocationOutcome> InvokeAsync(
        string toolName,
        Guid repositoryId,
        IReadOnlyDictionary<string, string> parameters,
        bool approved,
        CancellationToken ct = default,
        Guid? ownerUserId = null)
    {
        if (!_toolsByName.TryGetValue(toolName, out var tool))
            throw new InvalidOperationException($"Unknown tool '{toolName}'.");

        var repository = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found.");

        var argumentsJson = JsonSerializer.Serialize(parameters);

        // Tool-level approval gate (e.g. TerminalTool.RiskLevel == High).
        var needsApproval = tool.RiskLevel == ToolRiskLevel.High || tool.RiskLevel == ToolRiskLevel.Critical;
        string? decisionReason = null;

        // For TerminalTool specifically, also run the real command policy engine on the
        // actual command text so specific commands (not just the tool itself) get judged.
        if (tool.Name.Equals("TerminalTool", StringComparison.OrdinalIgnoreCase) &&
            parameters.TryGetValue("command", out var command))
        {
            var policyResult = CommandPolicyEngine.Evaluate(command);

            // The static denylist/dangerous-pattern Deny is never overridable by a
            // persisted Always-Allow rule — that protection exists specifically to stop
            // catastrophic commands regardless of past user choices.
            if (policyResult.Decision == CommandDecision.Deny)
            {
                return await RecordAndReturnAsync(toolName, repositoryId, repository.LocalPath, argumentsJson,
                    "Denied", policyResult.Reason, null, ct);
            }

            decisionReason = policyResult.Reason;
            needsApproval = needsApproval || policyResult.Decision == CommandDecision.RequireApproval;

            if (ownerUserId is { } uid)
            {
                var executable = CommandPolicyEngine.ExtractExecutable(command);
                var persisted = await _permissions.CheckAsync(uid, executable, ct);

                if (persisted == PersistedCommandDecision.AlwaysDeny)
                {
                    return await RecordAndReturnAsync(toolName, repositoryId, repository.LocalPath, argumentsJson,
                        "Denied", $"Denied by your persistent Always-Deny rule for '{executable}'.", null, ct);
                }
                if (persisted == PersistedCommandDecision.AlwaysAllow)
                {
                    needsApproval = false;
                    decisionReason = $"Allowed by your persistent Always-Allow rule for '{executable}'.";
                }
            }
        }

        if (needsApproval && !approved)
        {
            return await RecordAndReturnAsync(toolName, repositoryId, repository.LocalPath, argumentsJson,
                "PendingApproval", decisionReason ?? $"{tool.Name} has risk level {tool.RiskLevel} and requires approval.", null, ct);
        }

        var decision = "Allowed";

        var context = new ToolExecutionContext(repository.LocalPath, repositoryId);
        ToolExecutionResult result;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(tool.Timeout);
            result = await tool.ExecuteAsync(parameters, context, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            result = ToolExecutionResult.Fail($"{tool.Name} timed out after {tool.Timeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} threw an unhandled exception.", tool.Name);
            result = ToolExecutionResult.Fail($"Tool threw an unhandled exception: {ex.Message}");
        }

        return await RecordAndReturnAsync(toolName, repositoryId, repository.LocalPath, argumentsJson, decision, decisionReason, result, ct);
    }

    private async Task<ToolInvocationOutcome> RecordAndReturnAsync(
        string toolName, Guid repositoryId, string workspaceRoot, string argumentsJson,
        string decision, string? decisionReason, ToolExecutionResult? result, CancellationToken ct)
    {
        var entity = new ToolExecution
        {
            ToolName = toolName,
            RepositoryId = repositoryId,
            WorkspaceRootPath = workspaceRoot,
            ArgumentsJson = argumentsJson,
            Decision = decision,
            DecisionReason = decisionReason,
            Success = result?.Success,
            Output = Truncate(result?.Output),
            Error = Truncate(result?.Error),
            ExitCode = result?.ExitCode,
            CompletedAtUtc = result is not null ? DateTimeOffset.UtcNow : null
        };
        _db.ToolExecutions.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new ToolInvocationOutcome(entity.Id, decision, decisionReason, result);
    }

    private static string? Truncate(string? s) => s is { Length: > 20_000 } ? s[..20_000] + "\n... [truncated]" : s;
}

/// <summary>Seeds ToolDefinition rows from the actually-registered ITool instances at
/// startup, so the ToolDefinitions table (spec Section 21) reflects real, live tools —
/// never a hand-maintained list that can drift from what's actually registered.</summary>
public sealed class ToolDefinitionSeeder
{
    private readonly IEnumerable<ITool> _tools;
    private readonly PlatformDbContext _db;

    public ToolDefinitionSeeder(IEnumerable<ITool> tools, PlatformDbContext db)
    {
        _tools = tools;
        _db = db;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var tool in _tools)
        {
            var existing = await _db.ToolDefinitions.FirstOrDefaultAsync(t => t.Name == tool.Name, ct);
            if (existing is null)
            {
                _db.ToolDefinitions.Add(new ToolDefinition
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    RiskLevel = tool.RiskLevel.ToString(),
                    RequiresApproval = tool.RiskLevel is ToolRiskLevel.High or ToolRiskLevel.Critical,
                    DefaultTimeoutSeconds = (int)tool.Timeout.TotalSeconds
                });
            }
            else
            {
                existing.Description = tool.Description;
                existing.RiskLevel = tool.RiskLevel.ToString();
                existing.RequiresApproval = tool.RiskLevel is ToolRiskLevel.High or ToolRiskLevel.Critical;
                existing.DefaultTimeoutSeconds = (int)tool.Timeout.TotalSeconds;
            }
        }
        await _db.SaveChangesAsync(ct);
    }
}
