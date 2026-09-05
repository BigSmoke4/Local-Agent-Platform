using System.Security.Claims;
using LocalAgentPlatform.Modules.Tools.Application.Services;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

public class ToolsController : Controller
{
    private readonly ToolExecutionService _toolExecutionService;
    private readonly CommandPermissionService _permissions;
    private readonly PlatformDbContext _db;

    public ToolsController(ToolExecutionService toolExecutionService, CommandPermissionService permissions, PlatformDbContext db)
    {
        _toolExecutionService = toolExecutionService;
        _permissions = permissions;
        _db = db;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = new ToolsIndexViewModel
        {
            Tools = _toolExecutionService.AllTools
                .Select(t => new ToolRowViewModel(t.Name, t.Description, t.RiskLevel.ToString(), t.Timeout))
                .OrderBy(t => t.Name)
                .ToList(),
            Repositories = await _db.Repositories.OrderBy(r => r.LocalPath).ToListAsync(ct),
            RecentExecutions = await _db.ToolExecutions
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
        string? oldText, string? newText, string? command, string? subcommand, string? target,
        bool approved, string? persist, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(path)) parameters["path"] = path;
        if (!string.IsNullOrEmpty(content)) parameters["content"] = content;
        if (!string.IsNullOrEmpty(oldText)) parameters["oldText"] = oldText;
        if (!string.IsNullOrEmpty(newText)) parameters["newText"] = newText;
        if (!string.IsNullOrEmpty(command)) parameters["command"] = command;
        if (!string.IsNullOrEmpty(subcommand)) parameters["subcommand"] = subcommand;
        if (!string.IsNullOrEmpty(target)) parameters["target"] = target;

        // "Always Allow"/"Always Deny" persists a real per-user, per-executable rule
        // (spec Section 11) before invoking — so this attempt and every future one for
        // that executable are covered by ToolExecutionService's persisted-rule check.
        if (!string.IsNullOrEmpty(persist) && !string.IsNullOrEmpty(command))
        {
            var executable = CommandPolicyEngine.ExtractExecutable(command);
            var decision = persist == "AlwaysAllow" ? PersistedCommandDecision.AlwaysAllow : PersistedCommandDecision.AlwaysDeny;
            await _permissions.SetAsync(CurrentUserId, executable, decision, ct);
            if (decision == PersistedCommandDecision.AlwaysAllow) approved = true;
        }

        try
        {
            var outcome = await _toolExecutionService.InvokeAsync(toolName, repositoryId, parameters, approved, ct, CurrentUserId);

            TempData["ToolOutcomeDecision"] = outcome.Decision;
            TempData["ToolOutcomeReason"] = outcome.DecisionReason;
            TempData["ToolOutcomeOutput"] = outcome.Result?.Output;
            TempData["ToolOutcomeError"] = outcome.Result?.Error;
            TempData["ToolOutcomeToolName"] = toolName;
            TempData["ToolOutcomeRepositoryId"] = repositoryId.ToString();
            TempData["ToolOutcomeParametersJson"] = System.Text.Json.JsonSerializer.Serialize(parameters);
        }
        catch (InvalidOperationException ex)
        {
            TempData["ToolOutcomeDecision"] = "Error";
            TempData["ToolOutcomeReason"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

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
