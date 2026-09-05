namespace LocalAgentPlatform.Shared.Kernel.Tools;

/// <summary>
/// Abstraction every tool (file I/O, terminal, git, build, test, ...) implements.
/// The Agent Engine and the Web tool console depend on this interface only — never
/// on a concrete tool type — so new tools can be added without touching callers
/// (spec Section 10).
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }

    /// <summary>Low/Medium/High/Critical — drives whether execution requires human approval.</summary>
    ToolRiskLevel RiskLevel { get; }

    /// <summary>Maximum time this tool is allowed to run before being cancelled.</summary>
    TimeSpan Timeout { get; }

    Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        ToolExecutionContext context,
        CancellationToken ct = default);
}

public enum ToolRiskLevel { Low, Medium, High, Critical }

/// <summary>Everything a tool needs about the environment it's running in — never
/// broader filesystem access than the repository root it's scoped to (Section 38).</summary>
public sealed record ToolExecutionContext(string RepositoryRootPath, Guid? RepositoryId);

public sealed record ToolExecutionResult(bool Success, string Output, string? Error, int? ExitCode = null)
{
    public static ToolExecutionResult Ok(string output) => new(true, output, null);
    public static ToolExecutionResult Fail(string error, string partialOutput = "") => new(false, partialOutput, error);
}
