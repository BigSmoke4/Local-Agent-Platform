using System.Security.Cryptography;
using System.Text;

namespace Platform.Web.Services.Tools;

public class FileWriteToolException : Exception
{
    public FileWriteToolException(string message) : base(message) { }
}

public record FileWriteResult(string RelativePath, string PreviousHash, string NewHash, bool Created);

/// <summary>
/// Writes files within the sandboxed workspace only. Implements safe-write
/// semantics from §59: records a hash before writing, and if the caller
/// supplies an ExpectedHash that doesn't match what's actually on disk,
/// refuses the write instead of silently overwriting an externally-changed
/// file.
/// </summary>
public class FileWriteTool
{
    private readonly string _workspaceRoot;
    private readonly ILogger<FileWriteTool> _logger;

    public string Name => "FileWriteTool";

    public FileWriteTool(IConfiguration config, ILogger<FileWriteTool> logger)
    {
        _workspaceRoot = Path.GetFullPath(
            config["Workspace:Root"] ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
        Directory.CreateDirectory(_workspaceRoot);
        _logger = logger;
    }

    public static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public async Task<FileWriteResult> WriteAsync(
        string relativePath,
        string content,
        string? expectedHash,
        CancellationToken ct = default)
    {
        var fullPath = ResolveWithinWorkspace(relativePath);
        var existed = File.Exists(fullPath);
        string previousHash = string.Empty;

        if (existed)
        {
            var currentContent = await File.ReadAllTextAsync(fullPath, ct);
            previousHash = ComputeHash(currentContent);

            if (expectedHash is not null && !string.Equals(previousHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileWriteToolException(
                    $"Conflict: file '{relativePath}' changed on disk since it was last read " +
                    $"(expected hash {expectedHash}, found {previousHash}). Refusing to overwrite.");
            }
        }
        else if (expectedHash is not null)
        {
            throw new FileWriteToolException(
                $"Conflict: expected hash was supplied but '{relativePath}' does not exist.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);

        var newHash = ComputeHash(content);
        _logger.LogInformation("FileWriteTool wrote {Path} ({PrevHash} -> {NewHash})", relativePath, previousHash, newHash);

        return new FileWriteResult(relativePath, previousHash, newHash, Created: !existed);
    }

    private string ResolveWithinWorkspace(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));

        if (!combined.StartsWith(_workspaceRoot, StringComparison.Ordinal))
            throw new FileWriteToolException("Path traversal outside workspace is not permitted.");

        return combined;
    }
}
