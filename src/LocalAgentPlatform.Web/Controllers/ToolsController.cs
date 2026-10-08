using System.Security.Claims;
using LocalAgentPlatform.Modules.Tools.Application.Services;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

public class ToolsController : Controller
{
    private readonly ToolExecutionService _toolExecutionService;
    private readonly CommandPermissionService _permissions;
    private readonly PlatformDbContext _db;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public ToolsController(
        ToolExecutionService toolExecutionService,
        CommandPermissionService permissions,
        PlatformDbContext db,
        IWorkspaceRootPolicy workspacePolicy)
    {
        _toolExecutionService = toolExecutionService;
        _permissions = permissions;
        _db = db;
        _workspacePolicy = workspacePolicy;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var repositories = await _db.Repositories
            .Where(r => r.Project!.OwnerUserId == CurrentUserId)
            .OrderBy(r => r.LocalPath)
            .ToListAsync(ct);
        var visibleRepositories = repositories.Where(r => _workspacePolicy.IsAllowed(r.LocalPath)).ToList();
        var visibleRepositoryIds = visibleRepositories.Select(r => r.Id).ToArray();
        var vm = new ToolsIndexViewModel
        {
            Tools = _toolExecutionService.AllTools
                .Select(t => new ToolRowViewModel(t.Name, t.Description, t.RiskLevel.ToString(), t.Timeout))
                .OrderBy(t => t.Name)
                .ToList(),
            Repositories = visibleRepositories,
            RecentExecutions = await _db.ToolExecutions
                .Where(e => e.OwnerUserId == CurrentUserId &&
                            (e.RepositoryId == null || visibleRepositoryIds.Contains(e.RepositoryId.Value)))
                .OrderByDescending(e => e.RequestedAtUtc)
                .Take(25)
                .ToListAsync(ct),
            PermissionRules = await _permissions.ListAsync(CurrentUserId, ct)
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invoke(
        string toolName, Guid repositoryId, string? path, string? content,
        string? oldText, string? newText, string? expectedHash, string? command, string? subcommand, string? target,
        bool approved, string? persist, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(path)) parameters["path"] = path;
        if (content is not null) parameters["content"] = content;
        if (oldText is not null) parameters["oldText"] = oldText;
        if (newText is not null) parameters["newText"] = newText;
        if (!string.IsNullOrEmpty(expectedHash)) parameters["expectedHash"] = expectedHash;
        if (!string.IsNullOrEmpty(command)) parameters["command"] = command;
        if (!string.IsNullOrEmpty(subcommand)) parameters["subcommand"] = subcommand;
        if (!string.IsNullOrEmpty(target)) parameters["target"] = target;

        try
        {
            if (path is { Length: > 1_500 } || subcommand is { Length: > 1_500 } || target is { Length: > 1_500 } ||
                expectedHash is { Length: > 256 } || content is { Length: > 2_097_152 } ||
                oldText is { Length: > 2_097_152 } || newText is { Length: > 2_097_152 })
                throw new ArgumentException("Tool input exceeds the supported size limit.");

            if (persist is not null)
            {
                if (persist is not ("AlwaysAllow" or "AlwaysDeny") ||
                    string.IsNullOrWhiteSpace(command) ||
                    !string.Equals(toolName, "TerminalTool", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Persistent permissions apply only to a parsed TerminalTool executable.");
                if (command.Length > 1_500)
                    throw new ArgumentException("Terminal command exceeds the 1,500-character approval limit.");

                var executable = CommandPolicyEngine.ExtractExecutable(command);
                if (string.IsNullOrWhiteSpace(executable))
                    throw new ArgumentException("A persistent permission requires a valid executable name.");

                var decision = persist == "AlwaysAllow"
                    ? PersistedCommandDecision.AlwaysAllow
                    : PersistedCommandDecision.AlwaysDeny;
                if (decision == PersistedCommandDecision.AlwaysAllow)
                {
                    var commandPolicy = CommandPolicyEngine.Evaluate(command);
                    var terminalTool = _toolExecutionService.AllTools.FirstOrDefault(t =>
                        t.Name.Equals("TerminalTool", StringComparison.OrdinalIgnoreCase));
                    // Persistent approval is only meaningful for a command that asks for
                    // approval solely because its executable is unknown. It cannot be used
                    // to clear the TerminalTool's independent High-risk or dangerous-command gates.
                    if (commandPolicy.Decision != CommandDecision.RequireApproval || !commandPolicy.CanPersistApproval ||
                        terminalTool is null || terminalTool.RiskLevel is ToolRiskLevel.High or ToolRiskLevel.Critical)
                        throw new ArgumentException("Always Allow is unavailable for dangerous commands, known executables, or High/Critical-risk tools.");
                    approved = true; // the explicit button click approves this one invocation too
                }
                await _permissions.SetAsync(CurrentUserId, executable, decision, ct);
            }

            if (command is { Length: > 1_500 })
                throw new ArgumentException("Terminal command exceeds the 1,500-character approval limit.");
            var outcome = await _toolExecutionService.InvokeAsync(toolName, repositoryId, parameters, approved, ct, CurrentUserId);

            TempData["ToolOutcomeDecision"] = outcome.Decision;
            TempData["ToolOutcomeReason"] = Preview(outcome.DecisionReason, 2_000);
            TempData["ToolOutcomeOutput"] = Preview(outcome.Result?.Output, 8_000);
            TempData["ToolOutcomeError"] = Preview(outcome.Result?.Error, 4_000);
            TempData["ToolOutcomeToolName"] = toolName;
            TempData["ToolOutcomeRepositoryId"] = repositoryId.ToString();
            TempData["ToolOutcomeParametersJson"] = outcome.Decision == "PendingApproval"
                ? System.Text.Json.JsonSerializer.Serialize(ConfirmationParameters(toolName, parameters))
                : null;
        }
        catch (InvalidOperationException ex)
        {
            TempData["ToolOutcomeDecision"] = "Error";
            TempData["ToolOutcomeReason"] = Preview(ex.Message, 2_000);
        }
        catch (ArgumentException ex)
        {
            TempData["ToolOutcomeDecision"] = "Error";
            TempData["ToolOutcomeReason"] = Preview(ex.Message, 2_000);
        }

        return RedirectToAction(nameof(Index));
    }

    private static IReadOnlyDictionary<string, string> ConfirmationParameters(
        string toolName, IReadOnlyDictionary<string, string> parameters)
    {
        var keys = toolName.ToLowerInvariant() switch
        {
            "terminaltool" => new[] { "command" },
            "buildtool" or "testtool" => new[] { "target" },
            _ => Array.Empty<string>()
        };
        return parameters
            .Where(pair => keys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string? Preview(string? value, int maxCharacters) =>
        value is { Length: > 0 } && value.Length > maxCharacters
            ? value[..maxCharacters] + "... [display truncated]"
            : value;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokePermission(string executableName, CancellationToken ct)
    {
        await _permissions.SetAsync(CurrentUserId, executableName, PersistedCommandDecision.None, ct);
        return RedirectToAction(nameof(Index));
    }
}

public class ToolsIndexViewModel
{
    public IReadOnlyList<ToolRowViewModel> Tools { get; set; } = Array.Empty<ToolRowViewModel>();
    public IReadOnlyList<Repository> Repositories { get; set; } = Array.Empty<Repository>();
    public IReadOnlyList<ToolExecution> RecentExecutions { get; set; } = Array.Empty<ToolExecution>();
    public IReadOnlyList<CommandPermissionRule> PermissionRules { get; set; } = Array.Empty<CommandPermissionRule>();
}

public record ToolRowViewModel(string Name, string Description, string RiskLevel, TimeSpan Timeout);
