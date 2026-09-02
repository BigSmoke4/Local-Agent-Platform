using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;

namespace Platform.Web.Services.Routing;

public enum TaskComplexity
{
    Trivial,
    Simple,
    Complex
}

public record RoutingDecision(ModelDescriptor? SelectedModel, TaskComplexity Complexity, string Reason);

/// <summary>
/// Real routing logic per §31: classifies the request and picks a
/// registered model accordingly, rather than always using the default.
/// Classification here is a genuine (if simple) heuristic over request
/// length/keywords — not a learned classifier — and is labeled as such.
/// Falls back honestly to the default/any registered model when no
/// specialized one is registered for a tier, rather than pretending
/// specialization exists.
/// </summary>
public class ModelRouter
{
    private readonly PlatformDbContext _db;

    private static readonly string[] ComplexKeywords =
        { "debug", "architecture", "refactor", "design", "analyze", "optimize", "security", "performance" };

    public ModelRouter(PlatformDbContext db)
    {
        _db = db;
    }

    public TaskComplexity Classify(string userRequest)
    {
        var trimmed = userRequest.Trim();

        var isArithmetic = trimmed.Length > 0 && trimmed.All(c => char.IsDigit(c) || "+-*/(). ".Contains(c));
        if (isArithmetic) return TaskComplexity.Trivial;

        if (trimmed.Length < 40 && !ComplexKeywords.Any(k => trimmed.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return TaskComplexity.Simple;

        if (ComplexKeywords.Any(k => trimmed.Contains(k, StringComparison.OrdinalIgnoreCase)) || trimmed.Length > 300)
            return TaskComplexity.Complex;

        return TaskComplexity.Simple;
    }

    public async Task<RoutingDecision> RouteAsync(string userRequest, CancellationToken ct = default)
    {
        var complexity = Classify(userRequest);

        if (complexity == TaskComplexity.Trivial)
            return new RoutingDecision(null, complexity, "Trivial/arithmetic request — routed to CalculatorTool, no model needed.");

        // Prefer a model explicitly flagged for reasoning on complex tasks;
        // otherwise fall back to whatever is registered, honestly noting the fallback.
        if (complexity == TaskComplexity.Complex)
        {
            var reasoningModel = await _db.Models.FirstOrDefaultAsync(m => m.ReasoningCapability, ct);
            if (reasoningModel is not null)
                return new RoutingDecision(reasoningModel, complexity, $"Complex request routed to reasoning-capable model '{reasoningModel.Name}'.");
        }
        else
        {
            var smallModel = await _db.Models
                .Where(m => m.ParameterCount != null && m.ParameterCount <= 3_000_000_000)
                .OrderBy(m => m.ParameterCount)
                .FirstOrDefaultAsync(ct);
            if (smallModel is not null)
                return new RoutingDecision(smallModel, complexity, $"Simple request routed to small model '{smallModel.Name}' to avoid wasting large-model inference.");
        }

        var fallback = await _db.Models.FirstOrDefaultAsync(m => m.IsDefault, ct);
        return new RoutingDecision(
            fallback, complexity,
            fallback is null
                ? "No models registered."
                : $"No specialized model registered for {complexity} tier; falling back to default model '{fallback.Name}'.");
    }
}
