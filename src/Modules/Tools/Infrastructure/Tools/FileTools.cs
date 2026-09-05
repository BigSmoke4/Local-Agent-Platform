using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Kernel.Tools;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>Reads a text file's real contents. Refuses any path outside the workspace root.</summary>
public sealed class FileReadTool : ITool
{
    public string Name => "FileReadTool";
    public string Description => "Reads the contents of a text file within the repository workspace.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Low;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("path", out var relativePath) || string.IsNullOrWhiteSpace(relativePath))
            return ToolExecutionResult.Fail("Missing required parameter 'path'.");

        if (!CommandPolicyEngine.IsWithinWorkspace(context.RepositoryRootPath, relativePath))
            return ToolExecutionResult.Fail($"Path '{relativePath}' escapes the repository workspace — refused.");

        var fullPath = Path.Combine(context.RepositoryRootPath, relativePath);
        if (!File.Exists(fullPath))
            return ToolExecutionResult.Fail($"File not found: {relativePath}");

        try
        {
            var content = await File.ReadAllTextAsync(fullPath, ct);
            return ToolExecutionResult.Ok(content);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"Failed to read file: {ex.Message}");
        }
    }
}

/// <summary>Lists files/directories under a workspace-relative path. Real Directory.EnumerateFileSystemEntries.</summary>
public sealed class DirectoryListTool : ITool
{
    public string Name => "DirectoryListTool";
    public string Description => "Lists files and subdirectories under a path within the repository workspace.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Low;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        var relativePath = parameters.TryGetValue("path", out var p) ? p : ".";

        if (!CommandPolicyEngine.IsWithinWorkspace(context.RepositoryRootPath, relativePath))
            return Task.FromResult(ToolExecutionResult.Fail($"Path '{relativePath}' escapes the repository workspace — refused."));

        var fullPath = Path.Combine(context.RepositoryRootPath, relativePath);
        if (!Directory.Exists(fullPath))
            return Task.FromResult(ToolExecutionResult.Fail($"Directory not found: {relativePath}"));

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(fullPath)
                .Select(e => Path.GetRelativePath(context.RepositoryRootPath, e).Replace('\\', '/'))
                .OrderBy(e => e)
                .ToList();
            return Task.FromResult(ToolExecutionResult.Ok(string.Join('\n', entries)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolExecutionResult.Fail($"Failed to list directory: {ex.Message}"));
        }
    }
}
