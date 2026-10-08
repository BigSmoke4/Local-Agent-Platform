namespace LocalAgentPlatform.Shared.Kernel.Files;

/// <summary>Authoritative allowlist for repository roots. Registering a directory inside
/// the local process is not itself proof that it is a permitted workspace.</summary>
public interface IWorkspaceRootPolicy
{
    IReadOnlyList<string> AllowedRoots { get; }
    bool IsAllowed(string repositoryRootPath);
}

public sealed class WorkspaceRootPolicy : IWorkspaceRootPolicy
{
    public IReadOnlyList<string> AllowedRoots { get; }

    public WorkspaceRootPolicy(IEnumerable<string>? allowedRoots)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        AllowedRoots = (allowedRoots ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(WorkspacePathGuard.ResolveSafeWorkspaceRoot)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(comparer)
            .ToArray();
    }

    public bool IsAllowed(string repositoryRootPath) =>
        WorkspacePathGuard.IsSafeWorkspaceRoot(repositoryRootPath) &&
        AllowedRoots.Any(allowedRoot => WorkspacePathGuard.IsSafeWorkspaceRoot(allowedRoot) &&
                                        WorkspacePathGuard.IsWithinWorkspace(allowedRoot, repositoryRootPath));
}
