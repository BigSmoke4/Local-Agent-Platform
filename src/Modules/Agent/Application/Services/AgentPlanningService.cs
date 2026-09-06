using System.Text;
using System.Text.Json;
using LocalAgentPlatform.Modules.Agent.Domain;
using LocalAgentPlatform.Shared.Kernel.Models;
using LocalAgentPlatform.Shared.Kernel.Tools;

namespace LocalAgentPlatform.Modules.Agent.Application.Services;

public sealed record PlanningOutcome(AgentPlan? Plan, string RawModelText, ModelGenerationResult ModelResult, string? ParseError);

/// <summary>
/// Turns a user request into a real plan by calling the actual configured model
/// (via IModelProvider — no hard-coded plans, ever) and parsing its JSON response
/// against the AgentPlan contract. The tool list in the prompt is built live from
/// ToolExecutionService.AllTools, so the model is never told about a tool that doesn't
/// actually exist.
/// </summary>
public sealed class AgentPlanningService
{
    private readonly IModelProvider _modelProvider;

    public AgentPlanningService(IModelProvider modelProvider) => _modelProvider = modelProvider;

    public async Task<PlanningOutcome> CreatePlanAsync(
        string modelId, string userRequest, IReadOnlyList<ITool> availableTools, CancellationToken ct = default, string? additionalContext = null)
    {
        var systemPrompt = BuildSystemPrompt(availableTools, additionalContext);

        var request = new ModelGenerationRequest(
            ModelId: modelId,
            Prompt: userRequest,
            SystemPrompt: systemPrompt,
            Temperature: 0.1,
            MaxOutputTokens: 1024
        );

        var result = await _modelProvider.GenerateAsync(request, ct);
        var plan = TryParsePlan(result.Text, out var parseError);

        return new PlanningOutcome(plan, result.Text, result, parseError);
    }

    private static string BuildSystemPrompt(IReadOnlyList<ITool> tools, string? additionalContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the planning component of a local coding agent. Break the user's");
        sb.AppendLine("request into a short, ordered list of concrete steps. Respond with ONLY a");
        sb.AppendLine("single JSON object — no markdown fences, no commentary — matching exactly:");
        sb.AppendLine("""{"steps":[{"id":"inspect","description":"...","type":"ToolCall","toolName":"FileReadTool","arguments":{"path":"..."},"dependsOn":[]}]}""");
        sb.AppendLine("Each step MUST have a unique stable id. dependsOn contains zero or more earlier step ids.");
        sb.AppendLine("Independent steps may share the same dependencies; downstream steps can depend on multiple parents,");
        sb.AppendLine("which forms a DAG. Cycles are invalid. \"type\" is either \"ToolCall\" (use one of the tools below)");
        sb.AppendLine("or \"Reasoning\" (omit toolName/arguments). Keep the plan to at most 20 steps.");
        sb.AppendLine("Only use tool names from this exact list:");
        foreach (var tool in tools)
            sb.AppendLine($"- {tool.Name}: {tool.Description}");
        if (!string.IsNullOrWhiteSpace(additionalContext))
        {
            sb.AppendLine();
            sb.AppendLine(additionalContext);
        }
        return sb.ToString();
    }

    private static AgentPlan? TryParsePlan(string modelText, out string? parseError)
    {
        var candidate = ExtractJsonObject(modelText);
        if (candidate is null)
        {
            parseError = "No JSON object found in the model's response.";
            return null;
        }

        try
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var parsed = JsonSerializer.Deserialize<PlanJsonShape>(candidate, options);
            if (parsed?.Steps is null || parsed.Steps.Count == 0)
            {
                parseError = "Parsed JSON did not contain a non-empty 'steps' array.";
                return null;
            }

            var steps = parsed.Steps.Select((s, i) => new AgentPlanStep(
                Id: string.IsNullOrWhiteSpace(s.Id) ? $"step-{i + 1}" : s.Id.Trim(),
                Description: s.Description ?? "(no description)",
                Type: string.Equals(s.Type, "ToolCall", StringComparison.OrdinalIgnoreCase) ? "ToolCall" : "Reasoning",
                ToolName: s.ToolName,
                Arguments: s.Arguments,
                DependsOn: s.DependsOn ?? Array.Empty<string>()
            )).ToList();

            var ids = steps.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ids.Count != steps.Count)
            {
                parseError = "Plan contains duplicate step ids.";
                return null;
            }
            if (steps.Any(x => x.DependsOn?.Any(d => !ids.Contains(d)) == true))
            {
                parseError = "Plan contains a dependency on an unknown step id.";
                return null;
            }
            if (HasCycle(steps))
            {
                parseError = "Plan dependency graph contains a cycle.";
                return null;
            }

            parseError = null;
            return new AgentPlan(steps);
        }
        catch (JsonException ex)
        {
            parseError = $"Model response was not valid JSON: {ex.Message}";
            return null;
        }
    }

    /// <summary>Extracts the first balanced {...} object from arbitrary text, tolerating
    /// models that wrap JSON in markdown fences or add stray prose around it.</summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    private sealed class PlanJsonShape
    {
        public List<PlanStepJsonShape>? Steps { get; set; }
    }

    private static bool HasCycle(IReadOnlyList<AgentPlanStep> steps)
    {
        var graph = steps.ToDictionary(s => s.Id, s => s.DependsOn ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string id)
        {
            if (visited.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            foreach (var dep in graph[id])
                if (Visit(dep)) return true;
            visiting.Remove(id);
            visited.Add(id);
            return false;
        }

        return graph.Keys.Any(Visit);
    }

    private sealed class PlanStepJsonShape
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public string? Type { get; set; }
        public string? ToolName { get; set; }
        public Dictionary<string, string>? Arguments { get; set; }
        public List<string>? DependsOn { get; set; }
    }
}
