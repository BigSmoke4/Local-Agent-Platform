namespace LocalAgentPlatform.Modules.RepositoryAnalysis.Domain;

/// <summary>
/// Pure, unit-testable rules for which paths the indexer should skip.
/// No file-system access here — just string/path logic — so it's testable in isolation
/// from the actual scanner (Infrastructure layer).
/// </summary>
public static class IndexingIgnoreRules
{
    private static readonly string[] DefaultIgnoredDirectoryNames =
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "dist", "build", ".vscode"
    };

    public static bool IsIgnoredDirectory(string directoryName, IReadOnlyCollection<string>? extraIgnores = null)
    {
        if (DefaultIgnoredDirectoryNames.Contains(directoryName, StringComparer.OrdinalIgnoreCase))
            return true;
        return extraIgnores?.Contains(directoryName, StringComparer.OrdinalIgnoreCase) ?? false;
    }

    public static bool IsIgnoredPath(string relativePath, IReadOnlyCollection<string>? extraIgnores = null)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(seg => IsIgnoredDirectory(seg, extraIgnores));
    }

    public static string? DetectLanguage(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".razor" or ".cshtml" => "razor",
        ".ts" => "typescript",
        ".tsx" => "typescript-react",
        ".js" => "javascript",
        ".jsx" => "javascript-react",
        ".json" => "json",
        ".py" => "python",
        ".sql" => "sql",
        ".md" => "markdown",
        ".yml" or ".yaml" => "yaml",
        ".csproj" or ".sln" => "msbuild",
        _ => null
    };

    /// <summary>Files above this size are recorded (path/hash) but never fully read into memory for parsing.</summary>
    public const long MaxParsableFileSizeBytes = 2 * 1024 * 1024; // 2 MB
}
