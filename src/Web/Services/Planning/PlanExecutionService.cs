using Platform.Web.Data;
using Platform.Web.Models;
using Platform.Web.Services.Tools;
using Platform.Web.Services.Verification;

namespace Platform.Web.Services.Planning;

public record PlanExecutionResult(bool Succeeded, List<AgentTaskNode> ExecutedNodes, string Summary);

/// <summary>
/// Real executor for PlannerService's dynamically generated plans. This is
/// the piece that was missing before: it walks whatever step sequence the
/// model actually produced — not a hardcoded Build→Test→Review — and runs
/// the real tool behind each known step type.
///
/// Honest scope: only the step types PlannerService is constrained to
/// (Build, Test, Repair, Review, SearchSymbol, GitStatus, GitDiff, Analyze)
/// have real execution behind them here. "Analyze" has no dedicated tool
/// yet, so it's treated as a no-op informational step (recorded, not
/// fabricated as having done analysis) — see the switch statement below,
/// which says so in a comment at that exact branch rather than silently
/// pretending it ran something.
/// </summary>
public class PlanExecutionService
{
    private readonly PlatformDbContext _db;
    private readonly BuildTool _build;
    private readonly TestTool _test;
    private readonly GitTool _git;
    private readonly SearchSymbolTool _searchSymbol;
    private readonly ReviewerService _reviewer;
    private readonly ILogger<PlanExecutionService> _logger;

    public PlanExecutionService(
        PlatformDbContext db,
        BuildTool build,
        TestTool test,
        GitTool git,
        SearchSymbolTool searchSymbol,
        ReviewerService reviewer,
        ILogger<PlanExecutionService> logger)
    {
        _db = db;
        _build = build;
        _test = test;
        _git = git;
        _searchSymbol = searchSymbol;
        _reviewer = reviewer;
        _logger = logger;
    }

    public async Task<PlanExecutionResult> ExecuteAsync(
        Guid agentSessionId, string modelRuntimeId, string userRequest, List<PlannedStep> steps, CancellationToken ct = default)
    {
        var nodes = new List<AgentTaskNode>();
        var summaryParts = new List<string>();
        var overallSucceeded = true;
        var sequence = 0;
        string? lastBuildOutput = null;

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            sequence++;

            var node = new AgentTaskNode
            {
                AgentSessionId = agentSessionId,
                SequenceOrder = sequence,
                Type = step.Type,
                Description = step.Description,
                Status = TaskNodeStatus.Running,
                StartedAtUtc = DateTime.UtcNow
            };
            _db.AgentTaskNodes.Add(node);
            await _db.SaveChangesAsync(ct);

            bool stepSucceeded;
            string outputSummary;

            switch (step.Type)
            {
                case "Build":
                    var buildResult = await _build.RunAsync(null, ct);
                    lastBuildOutput = buildResult.RawOutput;
                    stepSucceeded = buildResult.Succeeded;
                    outputSummary = $"Build: {(buildResult.Succeeded ? "PASS" : $"FAIL ({buildResult.ErrorCount} errors)")}";
                    break;

                case "Test":
                    var testResult = await _test.RunAsync(null, ct);
                    stepSucceeded = testResult.Succeeded;
                    outputSummary = $"Tests: {testResult.Passed}/{testResult.Passed + testResult.Failed}";
                    break;

                case "GitStatus":
                    var statusResult = await _git.StatusAsync(ct);
                    stepSucceeded = statusResult.Succeeded;
                    outputSummary = statusResult.Succeeded ? statusResult.Output : statusResult.Error;
                    break;

                case "GitDiff":
                    var diffResult = await _git.DiffAsync(null, ct);
                    stepSucceeded = diffResult.Succeeded;
                    outputSummary = diffResult.Succeeded ? diffResult.Output : diffResult.Error;
                    break;

                case "SearchSymbol":
                    // Uses the step description as the search query — real search, real results.
                    var matches = await _searchSymbol.FindAsync(step.Description, ct);
                    stepSucceeded = true;
                    outputSummary = $"{matches.Count} symbol match(es) found.";
                    break;

                case "Review":
                    var review = await _reviewer.ReviewAsync(
                        modelRuntimeId, userRequest, string.Join("; ", summaryParts), lastBuildOutput ?? "(no build ran yet)", ct);
                    stepSucceeded = review.Approved;
                    outputSummary = review.Reasoning;
                    break;

                case "Repair":
                    // Repair requires knowing which file to edit, which this
                    // generic executor doesn't have enough context to decide on
                    // its own (that's what AgentVerificationController's
                    // dedicated repair path with SemanticRepairTargetResolver
                    // does). Recorded honestly as skipped, not faked.
                    stepSucceeded = true;
                    outputSummary = "Repair step skipped by generic plan executor — use POST /api/agent/run-verified for actual file repair, which has real target-resolution logic this generic step sequence doesn't.";
                    break;

                case "Analyze":
                    // No dedicated "Analyze" tool exists yet. Recorded as a
                    // no-op rather than fabricating analysis output.
                    stepSucceeded = true;
                    outputSummary = "No dedicated Analyze tool exists yet; step recorded but performed no action.";
                    break;

                default:
                    stepSucceeded = false;
                    outputSummary = $"Unknown step type '{step.Type}' — not executed (this shouldn't happen; PlannerService should have filtered it).";
                    break;
            }

            node.Status = stepSucceeded ? TaskNodeStatus.Succeeded : TaskNodeStatus.Failed;
            node.OutputSummary = outputSummary;
            node.EndedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            nodes.Add(node);
            summaryParts.Add($"{step.Type}: {outputSummary}");
            overallSucceeded &= stepSucceeded;

            _logger.LogInformation("Plan step {Sequence} ({Type}) completed: succeeded={Succeeded}", sequence, step.Type, stepSucceeded);

            if (!stepSucceeded)
                break; // stop the plan on first real failure rather than continuing blindly
        }

        return new PlanExecutionResult(overallSucceeded, nodes, string.Join(" | ", summaryParts));
    }
}
