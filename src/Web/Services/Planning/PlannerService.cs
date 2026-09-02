using System.Text.Json;

namespace Platform.Web.Services.Planning;

public record PlannedStep(string Type, string Description);

public record PlanResult(bool Succeeded, List<PlannedStep> Steps, string? Error);

/// <summary>
/// Real dynamic planner per §8/§9: asks the model to break the user's
/// request into a JSON list of steps, instead of a fixed sequence.
/// This makes an actual model call and parses actual structured output —
/// it does not fall back to a hardcoded plan on success, only reports
/// failure explicitly when parsing fails, rather than pretending the
/// model planned something it didn't.
///
/// Honest scope: allowed step Types are constrained to tools that actually
/// exist in this codebase (Build, Test, Repair, Review, SearchSymbol,
/// GitStatus, GitDiff, Analyze) — the planner cannot invent execution of a
/// tool that isn't real. Unknown step types returned by the model are
/// dropped with a logged warning rather than executed blindly.
/// </summary>
public class PlannerService
{
    private static readonly HashSet<string> KnownStepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Build", "Test", "Repair", "Review", "SearchSymbol", "GitStatus", "GitDiff", "Analyze"
    };

    private readonly IModelProvider _modelProvider;
    private readonly ILogger<PlannerService> _logger;

    public PlannerService(IModelProvider modelProvider, ILogger<PlannerService> logger)
    {
        _modelProvider = modelProvider;
        _logger = logger;
    }

    public async Task<PlanResult> CreatePlanAsync(string modelRuntimeId, string userRequest, CancellationToken ct = default)
    {
        var allowedTypes = string.Join(", ", KnownStepTypes);
        var prompt = $"""
            You are a software engineering task planner. Break the following
            request into an ordered JSON array of steps. Respond with ONLY the
            JSON array, no other text, no markdown fences.

            Each step must be: {{"type": "<one of: {allowedTypes}>", "description": "..."}}

            Rules:
            - Only use step types from the allowed list above — these map to real
              tools that exist; do not invent new types.
            - A plan verifying code should end with Build then Test then Review.
            - Keep the plan to 3-8 steps.

            Request: {userRequest}
            """;

        try
        {
            var generation = await _modelProvider.GenerateAsync(new GenerationRequest(modelRuntimeId, prompt, MaxTokens: 500), ct);
            var json = ExtractJsonArray(generation.Text);

            var raw = JsonSerializer.Deserialize<List<RawStep>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (raw is null || raw.Count == 0)
                return new PlanResult(false, new List<PlannedStep>(), "Model returned no parseable steps.");

            var steps = new List<PlannedStep>();
            foreach (var step in raw)
            {
                if (step.Type is null || !KnownStepTypes.Contains(step.Type))
                {
                    _logger.LogWarning("Planner dropped unknown step type '{Type}' — not a real tool in this codebase.", step.Type);
                    continue;
                }
                steps.Add(new PlannedStep(step.Type, step.Description ?? string.Empty));
            }

            if (steps.Count == 0)
                return new PlanResult(false, new List<PlannedStep>(), "All planned steps referenced unknown/unsupported tool types.");

            return new PlanResult(true, steps, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Planning failed");
            return new PlanResult(false, new List<PlannedStep>(), ex.Message);
        }
    }

    private static string ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private class RawStep
    {
        public string? Type { get; set; }
        public string? Description { get; set; }
    }
}
