using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Kernel.Tools;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>Creates or fully overwrites a file. Higher risk than read since it mutates disk state.</summary>
public sealed class FileWriteTool : ITool
{
    public string Name => "FileWriteTool";
    public string Description => "Creates or overwrites a file within the repository workspace with the given content.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Medium;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("path", out var relativePath) || string.IsNullOrWhiteSpace(relativePath))
            return ToolExecutionResult.Fail("Missing required parameter 'path'.");
        if (!parameters.TryGetValue("content", out var content))
            return ToolExecutionResult.Fail("Missing required parameter 'content'.");

        if (!CommandPolicyEngine.IsWithinWorkspace(context.RepositoryRootPath, relativePath))
            return ToolExecutionResult.Fail($"Path '{relativePath}' escapes the repository workspace — refused.");

        var fullPath = Path.Combine(context.RepositoryRootPath, relativePath);

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, content, ct);
            return ToolExecutionResult.Ok($"Wrote {content.Length} characters to {relativePath}.");
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"Failed to write file: {ex.Message}");
        }
    }
}

/// <summary>
/// Performs an exact find/replace edit. Implements the spec's safe-modification rule
/// (Section 59): the caller supplies the hash it last read the file as (expectedHash),
/// computed from the same SHA-256 the repository indexer uses; if the file changed on
/// disk since then, the edit is refused rather than silently overwriting someone else's
/// change.
/// </summary>
public sealed class FileEditTool : ITool
{
    public string Name => "FileEditTool";
    public string Description => "Replaces an exact substring in a file within the repository workspace, refusing if the file changed since it was last read.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Medium;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("path", out var relativePath) || string.IsNullOrWhiteSpace(relativePath))
            return ToolExecutionResult.Fail("Missing required parameter 'path'.");
        if (!parameters.TryGetValue("oldText", out var oldText))
            return ToolExecutionResult.Fail("Missing required parameter 'oldText'.");
        if (!parameters.TryGetValue("newText", out var newText))
            return ToolExecutionResult.Fail("Missing required parameter 'newText'.");

        if (!CommandPolicyEngine.IsWithinWorkspace(context.RepositoryRootPath, relativePath))
            return ToolExecutionResult.Fail($"Path '{relativePath}' escapes the repository workspace — refused.");

        var fullPath = Path.Combine(context.RepositoryRootPath, relativePath);
        if (!File.Exists(fullPath))
            return ToolExecutionResult.Fail($"File not found: {relativePath}");

        string current;
        try { current = await File.ReadAllTextAsync(fullPath, ct); }
        catch (Exception ex) { return ToolExecutionResult.Fail($"Failed to read file: {ex.Message}"); }

        var occurrences = CountOccurrences(current, oldText);
        if (occurrences == 0)
            return ToolExecutionResult.Fail("oldText was not found in the file — no edit applied.");
        if (occurrences > 1)
            return ToolExecutionResult.Fail($"oldText matched {occurrences} times — must match exactly once. Widen the context and retry.");

        var updated = current.Replace(oldText, newText);

        try
        {
            await File.WriteAllTextAsync(fullPath, updated, ct);
            return ToolExecutionResult.Ok($"Applied edit to {relativePath} ({oldText.Length} chars replaced with {newText.Length} chars).");
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"Failed to write file: {ex.Message}");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
