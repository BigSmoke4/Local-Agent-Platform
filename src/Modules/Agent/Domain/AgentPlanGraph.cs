namespace LocalAgentPlatform.Modules.Agent.Domain;

/// <summary>Validates and deterministically orders the agent's bounded dependency DAG.
/// Invalid metadata (unknown, duplicate, self, or cyclic dependencies) returns null so
/// callers can fail the plan before executing any step.</summary>
public static class AgentPlanGraph
{
    public static List<AgentPlanStep>? TryTopologicalOrder(IReadOnlyList<AgentPlanStep> steps)
    {
        if (steps is null || steps.Count is 0 or > 20) return null;

        var byId = new Dictionary<string, AgentPlanStep>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Id) || step.Id.Length > 64 ||
                step.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) ||
                !byId.TryAdd(step.Id, step))
                return null;
        }

        var indegree = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            var dependencies = step.DependsOn ?? Array.Empty<string>();
            if (dependencies.Count > 20 ||
                dependencies.Any(string.IsNullOrWhiteSpace) ||
                dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != dependencies.Count)
                return null;

            foreach (var dependency in dependencies)
            {
                if (!byId.ContainsKey(dependency) ||
                    string.Equals(dependency, step.Id, StringComparison.OrdinalIgnoreCase))
                    return null;
                indegree[step.Id]++;
            }
        }

        // Queue in source order for deterministic scheduling among independent nodes.
        var ready = new Queue<AgentPlanStep>(steps.Where(step => indegree[step.Id] == 0));
        var ordered = new List<AgentPlanStep>(steps.Count);
        while (ready.Count > 0)
        {
            var step = ready.Dequeue();
            ordered.Add(step);
            foreach (var dependent in steps.Where(candidate =>
                         (candidate.DependsOn ?? Array.Empty<string>())
                         .Contains(step.Id, StringComparer.OrdinalIgnoreCase)))
            {
                indegree[dependent.Id]--;
                if (indegree[dependent.Id] == 0) ready.Enqueue(dependent);
            }
        }

        return ordered.Count == steps.Count ? ordered : null;
    }
}
