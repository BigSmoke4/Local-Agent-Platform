using System.Text.Json;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Models;

namespace LocalAgentPlatform.Modules.Verification.Application.Services;

public sealed record ReviewOutcome(string Verdict, string Reason); // Verdict: Approved, Rejected, Unavailable

/// <summary>
/// Independent review stage (spec Section 16). This makes a real call to the
/// configured model, giving it the task description and the actual VerificationRun
/// results, and asks for a structured Approve/Reject verdict with a reason.
/// <para/>
/// Important scope note: this is advisory only. A "Rejected" verdict feeds into the
/// agent's decision to attempt a repair, but a model's opinion can never turn a real
/// build/test failure into a false pass, and — just as importantly — a clean model
/// opinion can never turn a real build/test failure into a reported success. The
/// deterministic VerificationPipelineService result is always the ground truth.
/// </summary>
public sealed class ReviewerService
{
    private readonly IModelProvider _modelProvider;

    public ReviewerService(IModelProvider modelProvider) => _modelProvider = modelProvider;

    public async Task<ReviewOutcome> ReviewAsync(
        string modelId, string userRequest, VerificationRun verification, CancellationToken ct = default)
    {
        var systemPrompt =
            "You are an independent code reviewer for a local coding agent. You will be given " +
            "the user's original request and the results of a real build/test/security " +
            "verification pass. Decide whether the change should be considered acceptable. " +
            "Respond with ONLY a single JSON object, no markdown fences: " +
            """{"verdict":"Approved","reason":"..."}""" + " or " +
            """{"verdict":"Rejected","reason":"..."}""" +
            " Reject if the security scan found High-severity issues, if warnings suggest " +
            "an incomplete change, or if the summary otherwise looks wrong for the request. " +
            "Do not reject solely because tests were not run.";

        var userPrompt =
            $"Request: {userRequest}\n\n" +
            $"Build passed: {verification.BuildPassed}\n" +
            $"Compiler errors: {verification.CompilerErrorCount}, warnings: {verification.CompilerWarningCount}\n" +
            $"Tests ran: {verification.TestsRan}, tests passed: {verification.TestsPassed} ({verification.TestOutputSummary})\n" +
            $"Security findings: {verification.SecurityFindingCount}\n";

        try
        {
            var result = await _modelProvider.GenerateAsync(new ModelGenerationRequest(
                ModelId: modelId, Prompt: userPrompt, SystemPrompt: systemPrompt, Temperature: 0.1, MaxOutputTokens: 300), ct);

            var json = ExtractJsonObject(result.Text);
            if (json is null) return new ReviewOutcome("Unavailable", "Reviewer model did not return a parseable verdict.");

            var parsed = JsonSerializer.Deserialize<VerdictShape>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (parsed?.Verdict is null) return new ReviewOutcome("Unavailable", "Reviewer JSON was missing a verdict field.");

            var verdict = string.Equals(parsed.Verdict, "Rejected", StringComparison.OrdinalIgnoreCase) ? "Rejected" : "Approved";
            return new ReviewOutcome(verdict, parsed.Reason ?? "(no reason given)");
        }
        catch (Exception ex)
        {
            // The reviewer is advisory — if it fails, verification still stands on its
            // own real results. This is never treated as a rejection or an approval.
            return new ReviewOutcome("Unavailable", $"Reviewer call failed: {ex.Message}");
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') { depth--; if (depth == 0) return text[start..(i + 1)]; }
        }
        return null;
    }

    private sealed class VerdictShape
    {
        public string? Verdict { get; set; }
        public string? Reason { get; set; }
    }
}
