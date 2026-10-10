using System.Security.Cryptography;
using System.Text;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Tools;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>Creates a file or replaces its contents using an optimistic-concurrency
/// token. Existing files must be read first and supplied with their SHA-256; a new path
/// must explicitly use expectedHash=NEW. This prevents an agent from silently clobbering
/// edits made since its last read.</summary>
public sealed class FileWriteTool : ITool
{
    public string Name => "FileWriteTool";
    public string Description => "Creates or replaces a workspace file. Requires expectedHash containing the current SHA-256 from FileReadTool, or exactly NEW when creating a new file.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Medium;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("path", out var relativePath) || string.IsNullOrWhiteSpace(relativePath))
            return ToolExecutionResult.Fail("Missing required parameter 'path'.");
        if (!parameters.TryGetValue("content", out var content) || content is null)
            return ToolExecutionResult.Fail("Missing required parameter 'content'.");
        if (Encoding.UTF8.GetByteCount(content) > WorkspaceFileMutation.MaxFileBytes)
            return ToolExecutionResult.Fail($"Content exceeds the {WorkspaceFileMutation.MaxFileBytes}-byte mutation limit.");
        if (!parameters.TryGetValue("expectedHash", out var expectedHash) || string.IsNullOrWhiteSpace(expectedHash))
            return ToolExecutionResult.Fail("Missing required parameter 'expectedHash'. Read the file first or use NEW only for a new file.");
        if (!CommandPolicyEngine.IsWithinWorkspace(context.RepositoryRootPath, relativePath))
            return ToolExecutionResult.Fail($"Path '{relativePath}' escapes the repository workspace — refused.");

        var fullPath = Path.GetFullPath(Path.Combine(context.RepositoryRootPath, relativePath));
        var current = await WorkspaceFileMutation.ReadCurrentAsync(fullPath, expectedHash, ct);
        if (!current.Success) return ToolExecutionResult.Fail(current.Error!);

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var updated = await WorkspaceFileMutation.WriteAtomicallyAsync(
                fullPath, content, expectedHash, context.RepositoryRootPath, ct);
            return updated.Success
                ? ToolExecutionResult.Ok($"Wrote {content.Length} characters to {relativePath}. SHA-256: {updated.NewHash}")
                : ToolExecutionResult.Fail(updated.Error!);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"Failed to write file: {ex.Message}");
        }
    }
}

/// <summary>
/// Performs an exact find/replace edit with optimistic concurrency. The expected hash is
/// mandatory and checked again immediately before the atomic replacement.
/// </summary>
public sealed class FileEditTool : ITool
{
    public string Name => "FileEditTool";
    public string Description => "Replaces one exact substring in a workspace file. Requires expectedHash containing the current SHA-256 from FileReadTool; refuses stale or ambiguous edits.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Medium;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("path", out var relativePath) || string.IsNullOrWhiteSpace(relativePath))
            return ToolExecutionResult.Fail("Missing required parameter 'path'.");
        if (!parameters.TryGetValue("oldText", out var oldText) || oldText is null)
            return ToolExecutionResult.Fail("Missing required parameter 'oldText'.");
        if (!parameters.TryGetValue("newText", out var newText) || newText is null)
            return ToolExecutionResult.Fail("Missing required parameter 'newText'.");
        if (oldText.Length > WorkspaceFileMutation.MaxFileBytes || newText.Length > WorkspaceFileMutation.MaxFileBytes)
            return ToolExecutionResult.Fail($"Edit text exceeds the {WorkspaceFileMutation.MaxFileBytes}-character limit.");
        if (!parameters.TryGetValue("expectedHash", out var expectedHash) || string.IsNullOrWhiteSpace(expectedHash))
            return ToolExecutionResult.Fail("Missing required parameter 'expectedHash'. Read the file first; stale edits are refused.");
        if (!CommandPolicyEngine.IsWithinWorkspace(context.RepositoryRootPath, relativePath))
            return ToolExecutionResult.Fail($"Path '{relativePath}' escapes the repository workspace — refused.");

        var fullPath = Path.GetFullPath(Path.Combine(context.RepositoryRootPath, relativePath));
        if (!File.Exists(fullPath)) return ToolExecutionResult.Fail($"File not found: {relativePath}");

        var current = await WorkspaceFileMutation.ReadCurrentAsync(fullPath, expectedHash, ct);
        if (!current.Success) return ToolExecutionResult.Fail(current.Error!);

        string text;
        try
        {
            if (new FileInfo(fullPath).Length > WorkspaceFileMutation.MaxFileBytes)
                return ToolExecutionResult.Fail($"File exceeds the {WorkspaceFileMutation.MaxFileBytes}-byte mutation limit.");
            text = await File.ReadAllTextAsync(fullPath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolExecutionResult.Fail($"Failed to read file: {ex.Message}"); }

        var occurrences = CountOccurrences(text, oldText);
        if (occurrences == 0) return ToolExecutionResult.Fail("oldText was not found in the file — no edit applied.");
        if (occurrences > 1)
            return ToolExecutionResult.Fail($"oldText matched {occurrences} times — must match exactly once. Widen the context and retry.");

        var updatedText = text.Replace(oldText, newText, StringComparison.Ordinal);
        if (Encoding.UTF8.GetByteCount(updatedText) > WorkspaceFileMutation.MaxFileBytes)
            return ToolExecutionResult.Fail($"Updated file exceeds the {WorkspaceFileMutation.MaxFileBytes}-byte mutation limit.");
        var write = await WorkspaceFileMutation.WriteAtomicallyAsync(
            fullPath, updatedText, expectedHash, context.RepositoryRootPath, ct);
        return write.Success
            ? ToolExecutionResult.Ok($"Applied edit to {relativePath} ({oldText.Length} chars replaced with {newText.Length} chars). SHA-256: {write.NewHash}")
            : ToolExecutionResult.Fail(write.Error!);
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

internal static class WorkspaceFileMutation
{
    public const int MaxFileBytes = 2 * 1024 * 1024;

    public sealed record CurrentFileResult(bool Success, string? ContentHash, string? Error);
    public sealed record WriteResult(bool Success, string? NewHash, string? Error);

    public static async Task<CurrentFileResult> ReadCurrentAsync(string fullPath, string expectedHash, CancellationToken ct)
    {
        if (Directory.Exists(fullPath)) return new CurrentFileResult(false, null, "The target path is a directory.");

        if (!File.Exists(fullPath))
            return string.Equals(expectedHash.Trim(), "NEW", StringComparison.OrdinalIgnoreCase)
                ? new CurrentFileResult(true, null, null)
                : new CurrentFileResult(false, null, "File does not exist. Use expectedHash=NEW only when creating a new file.");

        if (!WorkspacePathGuard.IsRegularFile(fullPath))
            return new CurrentFileResult(false, null, "The target must be a regular file, not a symbolic link, device, or directory.");
        if (string.Equals(expectedHash.Trim(), "NEW", StringComparison.OrdinalIgnoreCase))
            return new CurrentFileResult(false, null, "The file already exists; refusing to overwrite it with expectedHash=NEW.");

        byte[] bytes;
        try
        {
            if (new FileInfo(fullPath).Length > MaxFileBytes)
                return new CurrentFileResult(false, null, $"File exceeds the {MaxFileBytes}-byte mutation limit.");
            bytes = await File.ReadAllBytesAsync(fullPath, ct);
            if (bytes.Length > MaxFileBytes)
                return new CurrentFileResult(false, null, $"File grew beyond the {MaxFileBytes}-byte mutation limit while being read.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new CurrentFileResult(false, null, $"Could not read the current file hash: {ex.Message}"); }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(hash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            return new CurrentFileResult(false, hash, $"File changed since it was read (expected {expectedHash.Trim()}, current {hash}); no edit was applied.");
        return new CurrentFileResult(true, hash, null);
    }

    public static async Task<WriteResult> WriteAtomicallyAsync(
        string fullPath, string content, string expectedHash, string workspaceRoot, CancellationToken ct)
    {
        // Re-check the content hash after generation and immediately before replacement.
        var current = await ReadCurrentAsync(fullPath, expectedHash, ct);
        if (!current.Success) return new WriteResult(false, null, current.Error);
        if (!CommandPolicyEngine.IsWithinWorkspace(workspaceRoot, Path.GetRelativePath(workspaceRoot, fullPath)))
            return new WriteResult(false, null, "Target path escaped the repository workspace before writing.");

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory)) return new WriteResult(false, null, "Target directory is invalid.");
        var contentBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        if (contentBytes.Length > MaxFileBytes)
            return new WriteResult(false, null, $"Content exceeds the {MaxFileBytes}-byte mutation limit.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".lap-{Guid.NewGuid():N}.tmp");
        try
        {
            // CreateNew prevents following a pre-existing temporary symlink at this
            // unpredictable name. The known byte array also bounds the output write.
            await using (var temp = new FileStream(
                             tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await temp.WriteAsync(contentBytes, ct);
                await temp.FlushAsync(ct);
            }
            // Use overwrite only for a target whose latest hash still matches the value
            // the caller observed. For a new file, File.Move(..., false) is create-only.
            var immediatelyCurrent = await ReadCurrentAsync(fullPath, expectedHash, ct);
            if (!immediatelyCurrent.Success) return new WriteResult(false, null, immediatelyCurrent.Error);
            File.Move(tempPath, fullPath, overwrite: immediatelyCurrent.ContentHash is not null);
            var newHash = Convert.ToHexString(SHA256.HashData(contentBytes));
            return new WriteResult(true, newHash, null);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
