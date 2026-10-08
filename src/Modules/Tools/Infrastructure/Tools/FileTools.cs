using System.Security.Cryptography;
using System.Text;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Tools;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>Reads a bounded text file and returns its content hash so later mutations
/// can be optimistic-concurrency checked.</summary>
public sealed class FileReadTool : ITool
{
    private const long MaxReadBytes = 2 * 1024 * 1024;

    public string Name => "FileReadTool";
    public string Description => "Reads a text file within the repository workspace and returns its SHA-256 hash for safe edits.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Low;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("path", out var relativePath) || string.IsNullOrWhiteSpace(relativePath))
            return ToolExecutionResult.Fail("Missing required parameter 'path'.");
        if (!CommandPolicyEngine.IsWithinWorkspace(context.RepositoryRootPath, relativePath))
            return ToolExecutionResult.Fail($"Path '{relativePath}' escapes the repository workspace — refused.");

        var fullPath = Path.GetFullPath(Path.Combine(context.RepositoryRootPath, relativePath));
        if (!File.Exists(fullPath) || !WorkspacePathGuard.IsRegularFile(fullPath))
            return ToolExecutionResult.Fail($"File not found or not a regular file: {relativePath}");

        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaxReadBytes)
                return ToolExecutionResult.Fail($"File is {info.Length} bytes; FileReadTool is limited to {MaxReadBytes} bytes. Use search or a narrower context instead.");
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            if (bytes.Length > MaxReadBytes)
                return ToolExecutionResult.Fail($"File grew beyond the {MaxReadBytes}-byte FileReadTool limit while being read.");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var content = Encoding.UTF8.GetString(bytes);
            return ToolExecutionResult.Ok($"SHA-256: {hash}\n--- BEGIN FILE ---\n{content}\n--- END FILE ---");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"Failed to read file: {ex.Message}");
        }
    }
}

/// <summary>Lists workspace entries without following symlinked directories.</summary>
public sealed class DirectoryListTool : ITool
{
    private const int MaxEntries = 500;

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

        var fullPath = Path.GetFullPath(Path.Combine(context.RepositoryRootPath, relativePath));
        if (!WorkspacePathGuard.IsSafeWorkspaceRoot(fullPath))
            return Task.FromResult(ToolExecutionResult.Fail($"Directory not found or traverses a symbolic link/reparse point: {relativePath}"));

        try
        {
            ct.ThrowIfCancellationRequested();
            var entries = Directory.EnumerateFileSystemEntries(fullPath)
                .Take(MaxEntries + 1)
                .Select(e => Path.GetRelativePath(context.RepositoryRootPath, e).Replace('\\', '/'))
                .OrderBy(e => e, StringComparer.Ordinal)
                .ToList();
            var truncated = entries.Count > MaxEntries;
            if (truncated) entries = entries.Take(MaxEntries).ToList();
            var output = string.Join('\n', entries);
            if (truncated) output += $"\n[listing truncated at {MaxEntries} entries]";
            return Task.FromResult(ToolExecutionResult.Ok(output));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(ToolExecutionResult.Fail($"Failed to list directory: {ex.Message}"));
        }
    }
}
