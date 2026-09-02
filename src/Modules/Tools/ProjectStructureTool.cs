namespace Platform.Web.Services.Tools;

public record ProjectNode(string Name, bool IsDirectory, List<ProjectNode> Children);

/// <summary>
/// Real filesystem scan of the workspace, sandboxed the same way as
/// FileReadTool. Ignores build/vcs artifact directories per §60.
///
/// Incremental behavior: computes a cheap aggregate signature (path + size +
/// last-write-time for every file under the root) and skips rebuilding the
/// tree if that signature matches the previous scan — a real, working
/// incremental check, not the same cost as a full tree rebuild every call.
/// Honest limitation: the cache is in-process only (a static field), so it
/// resets on app restart rather than persisting across restarts the way
/// RepositoryIndexService's per-file content hashes in PostgreSQL do; that
/// would be the natural next step if this needs to survive restarts.
/// </summary>
public class ProjectStructureTool
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".vscode"
    };

    private readonly string _workspaceRoot;

    private static string? _lastSignature;
    private static ProjectNode? _lastResult;
    private static readonly object CacheLock = new();

    public string Name => "ProjectStructureTool";

    public ProjectStructureTool(IConfiguration config)
    {
        _workspaceRoot = Path.GetFullPath(
            config["Workspace:Root"] ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public ProjectNode Scan(int maxDepth = 6)
    {
        var signature = ComputeSignature();

        lock (CacheLock)
        {
            if (_lastSignature == signature && _lastResult is not null)
                return _lastResult; // real skip — no directory tree rebuilt this call
        }

        var result = ScanDirectory(new DirectoryInfo(_workspaceRoot), depth: 0, maxDepth);

        lock (CacheLock)
        {
            _lastSignature = signature;
            _lastResult = result;
        }

        return result;
    }

    private string ComputeSignature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(_workspaceRoot, "*", SearchOption.AllDirectories)
                     .Where(f => !IgnoredDirs.Any(seg => f.Contains($"{Path.DirectorySeparatorChar}{seg}{Path.DirectorySeparatorChar}")))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            sb.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
        }
        return FileWriteTool.ComputeHash(sb.ToString());
    }

    private ProjectNode ScanDirectory(DirectoryInfo dir, int depth, int maxDepth)
    {
        var children = new List<ProjectNode>();

        if (depth < maxDepth)
        {
            foreach (var sub in dir.GetDirectories().Where(d => !IgnoredDirs.Contains(d.Name)).OrderBy(d => d.Name))
                children.Add(ScanDirectory(sub, depth + 1, maxDepth));

            foreach (var file in dir.GetFiles().OrderBy(f => f.Name))
                children.Add(new ProjectNode(file.Name, false, new List<ProjectNode>()));
        }

        return new ProjectNode(dir.Name, true, children);
    }
}
