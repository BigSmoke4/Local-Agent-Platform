using System.Text.Json;

namespace Platform.Web.Services.Verification;

public record ReviewOutcome(bool Approved, string Reasoning);

/// <summary>
/// Independent review stage per §16. Makes a real second call to the local
/// model, asking it to judge the work against the original request and
/// verification results, and requiring a structured JSON verdict. This is a
/// genuine LLM call (real intelligence, not scripted) — its judgment quality
/// depends on the model, same as any other reasoning task in this system.
/// </summary>
public class ReviewerService
{
    private readonly IModelProvider _modelProvider;
    private readonly ILogger<ReviewerService> _logger;

    public ReviewerService(IModelProvider modelProvider, ILogger<ReviewerService> logger)
    {
        _modelProvider = modelProvider;
        _logger = logger;
    }

    public async Task<ReviewOutcome> ReviewAsync(
        string modelRuntimeId,
        string userRequest,
        string agentResult,
        string verificationSummary,
        CancellationToken ct = default)
    {
        var prompt = $"""
            You are an independent code reviewer. Respond with ONLY a JSON object,
            no other text, in this exact shape: {{"approved": true|false, "reasoning": "..."}}

            Original request: {userRequest}

            Agent's result: {agentResult}

            Verification result: {verificationSummary}

            Reject if verification failed, if the result does not address the request,
            or if you see an obvious correctness/security problem. Otherwise approve.
            """;

        try
        {
            var generation = await _modelProvider.GenerateAsync(
                new GenerationRequest(modelRuntimeId, prompt, MaxTokens: 300), ct);

            var jsonText = ExtractJson(generation.Text);
            var parsed = JsonSerializer.Deserialize<ReviewJson>(jsonText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed is null)
                return new ReviewOutcome(false, "Reviewer response could not be parsed; rejecting conservatively.");

            return new ReviewOutcome(parsed.Approved, parsed.Reasoning ?? "No reasoning provided.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reviewer call failed");
            // Fail closed: an unreachable/broken reviewer must not silently approve.
            return new ReviewOutcome(false, $"Reviewer unavailable: {ex.Message}");
        }
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private class ReviewJson
    {
        public bool Approved { get; set; }
        public string? Reasoning { get; set; }
    }
}
