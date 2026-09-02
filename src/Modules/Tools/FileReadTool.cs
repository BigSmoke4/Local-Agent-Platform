namespace Platform.Web.Services.Tools;

public class FileReadToolException : Exception
{
    public FileReadToolException(string message) : base(message) { }
}

/// <summary>
/// Reads files but only within a configured workspace root — never arbitrary
/// filesystem access, per the platform's security requirements.
/// </summary>
public class FileReadTool
{
    private readonly string _workspaceRoot;
    private readonly ILogger<FileReadTool> _logger;

    public string Name => "FileReadTool";

    public FileReadTool(IConfiguration config, ILogger<FileReadTool> logger)
    {
        _workspaceRoot = Path.GetFullPath(
            config["Workspace:Root"] ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
        Directory.CreateDirectory(_workspaceRoot);
        _logger = logger;
    }

    public async Task<string> ReadAsync(string relativePath, CancellationToken ct = default)
    {
        var fullPath = ResolveWithinWorkspace(relativePath);

        if (!File.Exists(fullPath))
            throw new FileReadToolException($"File not found: {relativePath}");

        _logger.LogInformation("FileReadTool reading {Path}", relativePath);
        return await File.ReadAllTextAsync(fullPath, ct);
    }

    public string ResolveWithinWorkspace(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));

        if (!combined.StartsWith(_workspaceRoot, StringComparison.Ordinal))
            throw new FileReadToolException("Path traversal outside workspace is not permitted.");

        return combined;
    }
}
