using System.Text.Json;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Security;
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
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public ToolExecutionService(
        IEnumerable<ITool> tools, PlatformDbContext db, CommandPermissionService permissions,
        ILogger<ToolExecutionService> logger, IWorkspaceRootPolicy workspacePolicy)
    {
        _toolsByName = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _db = db;
        _permissions = permissions;
        _logger = logger;
        _workspacePolicy = workspacePolicy;
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

        var repository = await _db.Repositories
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found.");

        if (ownerUserId is { } callerId && repository.Project?.OwnerUserId != callerId)
            throw new InvalidOperationException("Repository not found or access denied.");
        if (!_workspacePolicy.IsAllowed(repository.LocalPath))
            throw new InvalidOperationException("Repository is outside the configured workspace roots.");

        var argumentsJson = JsonSerializer.Serialize(RedactAuditArguments(toolName, parameters));

        // Tool-level approval is an independent gate. A remembered executable decision
        // must never clear a tool's High/Critical requirement.
        var requiresToolApproval = tool.RiskLevel is ToolRiskLevel.High or ToolRiskLevel.Critical;
        var needsApproval = requiresToolApproval;
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
                    "Denied", policyResult.Reason, null, ct, ownerUserId);
            }

            decisionReason = policyResult.Reason;
            needsApproval = needsApproval || policyResult.Decision == CommandDecision.RequireApproval;

            if (ownerUserId is { } uid)
            {
                var executable = CommandPolicyEngine.ExtractExecutable(command);
                var persisted = string.IsNullOrEmpty(executable)
                    ? PersistedCommandDecision.None
                    : await _permissions.CheckAsync(uid, executable, ct);

                if (persisted == PersistedCommandDecision.AlwaysDeny)
                {
                    return await RecordAndReturnAsync(toolName, repositoryId, repository.LocalPath, argumentsJson,
                        "Denied", $"Denied by your persistent Always-Deny rule for '{executable}'.", null, ct, ownerUserId);
                }
                // An Always-Allow rule can suppress only an ordinary unknown-executable
                // approval. Dangerous-pattern and destructive-git approvals always need
                // a fresh, one-time human decision.
                if (persisted == PersistedCommandDecision.AlwaysAllow &&
                    policyResult.CanPersistApproval && !requiresToolApproval)
                {
                    needsApproval = false;
                    decisionReason = $"Allowed by your persistent Always-Allow rule for '{executable}'.";
                }
            }
        }

        if (needsApproval && !approved)
        {
            return await RecordAndReturnAsync(toolName, repositoryId, repository.LocalPath, argumentsJson,
                "PendingApproval", decisionReason ?? $"{tool.Name} has risk level {tool.RiskLevel} and requires approval.", null, ct, ownerUserId);
        }

        var decision = "Allowed";

        var policyRequiresApproval = tool.Name.Equals("TerminalTool", StringComparison.OrdinalIgnoreCase) &&
            parameters.TryGetValue("command", out var requestedCommand) &&
            CommandPolicyEngine.Evaluate(requestedCommand).Decision == CommandDecision.RequireApproval;
        var commandApprovalGranted = approved || (policyRequiresApproval && !needsApproval);
        var context = new ToolExecutionContext(repository.LocalPath, repositoryId, commandApprovalGranted);
        ToolExecutionResult result;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(tool.Timeout);
            result = await tool.ExecuteAsync(parameters, context, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordAndReturnAsync(toolName, repositoryId, repository.LocalPath, argumentsJson,
                "Cancelled", "Tool execution cancelled by caller.", ToolExecutionResult.Fail("Cancelled."),
                CancellationToken.None, ownerUserId);
            throw;
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

        return await RecordAndReturnAsync(toolName, repositoryId, repository.LocalPath, argumentsJson, decision, decisionReason, result, ct, ownerUserId);
    }

    private async Task<ToolInvocationOutcome> RecordAndReturnAsync(
        string toolName, Guid repositoryId, string workspaceRoot, string argumentsJson,
        string decision, string? decisionReason, ToolExecutionResult? result, CancellationToken ct, Guid? ownerUserId)
    {
        var entity = new ToolExecution
        {
            OwnerUserId = ownerUserId,
            ToolName = toolName,
            RepositoryId = repositoryId,
            WorkspaceRootPath = workspaceRoot,
            ArgumentsJson = argumentsJson,
            Decision = decision,
            DecisionReason = decisionReason,
            Success = result?.Success,
            Output = Truncate(RedactAuditOutput(toolName, result?.Output)),
            Error = Truncate(RedactSecrets(result?.Error)),
            ExitCode = result?.ExitCode,
            CompletedAtUtc = result is not null ? DateTimeOffset.UtcNow : null
        };
        _db.ToolExecutions.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new ToolInvocationOutcome(entity.Id, decision, decisionReason, result);
    }

    private static IReadOnlyDictionary<string, string> RedactAuditArguments(
        string toolName, IReadOnlyDictionary<string, string> parameters)
    {
        var redacted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            var safeValue = value ?? string.Empty;
            // The task/session store carries the data needed for execution; the separate
            // audit row should record which file was touched, not duplicate source text.
            if (toolName.Equals("FileWriteTool", StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals("FileEditTool", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("content", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("oldText", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("newText", StringComparison.OrdinalIgnoreCase))
                {
                    redacted[key] = $"[omitted; {safeValue.Length} characters]";
                    continue;
                }
            }
            redacted[key] = RedactSecrets(safeValue) ?? string.Empty;
        }
        return redacted;
    }

    private static string? RedactAuditOutput(string toolName, string? output)
    {
        if (output is null) return null;
        if (toolName.Equals("FileReadTool", StringComparison.OrdinalIgnoreCase))
            return $"[file contents omitted from audit; {output.Length} characters returned to the agent]";
        return RedactSecrets(output);
    }

    private static string? RedactSecrets(string? text) =>
        text is null ? null : SecretRedactor.Redact(text);

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
