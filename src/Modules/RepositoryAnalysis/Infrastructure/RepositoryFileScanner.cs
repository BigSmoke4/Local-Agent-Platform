using System.Security.Cryptography;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Domain;
using LocalAgentPlatform.Shared.Kernel.Files;

namespace LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;

public sealed record ScannedFile(string RelativePath, string ContentHash, long SizeBytes, string? Language);

public interface IRepositoryFileScanner
{
    /// <summary>Enumerates every non-ignored file under rootPath with a real SHA-256 content hash.</summary>
    IAsyncEnumerable<ScannedFile> ScanAsync(string rootPath, CancellationToken ct = default);

    /// <summary>Enumerates current safe file paths without hashing. Strict mode throws when
    /// a non-ignored directory cannot be inspected or a reparse point would make coverage
    /// ambiguous; security verification uses this to avoid claiming a partial scan passed.</summary>
    IEnumerable<string> EnumeratePaths(string rootPath);
}

/// <summary>
/// Real filesystem walker. Every hash returned is computed from actual file bytes via
/// SHA-256 — used by the indexing service to detect which files actually changed
/// since the last index run (Section 60: "process only changed files").
/// </summary>
public sealed class RepositoryFileScanner : IRepositoryFileScanner
{
    private const int MaxEnumeratedEntries = 100_000;

    public async IAsyncEnumerable<ScannedFile> ScanAsync(
        string rootPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = ValidateRoot(rootPath);
        foreach (var filePath in EnumerateFiles(root, rejectReparsePoints: false, failOnEnumerationError: true))
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
            if (IndexingIgnoreRules.IsIgnoredPath(relativePath) ||
                !WorkspacePathGuard.IsWithinWorkspace(root, filePath) ||
                !WorkspacePathGuard.IsRegularFile(filePath)) continue;
            var length = TryGetFileLength(filePath);
            if (length is null) continue;

            string hash;
            try
            {
                await using var stream = File.OpenRead(filePath);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            }
            catch (FileNotFoundException) { continue; } // Removed after enumeration; it is no longer part of the workspace.
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException ex) { throw new IOException($"Could not hash repository file '{relativePath}'; indexing coverage is incomplete.", ex); }
            catch (UnauthorizedAccessException ex) { throw new IOException($"Could not access repository file '{relativePath}'; indexing coverage is incomplete.", ex); }

            yield return new ScannedFile(relativePath, hash, length.Value, IndexingIgnoreRules.DetectLanguage(filePath));
        }
    }

    public IEnumerable<string> EnumeratePaths(string rootPath)
    {
        var root = ValidateRoot(rootPath);
        foreach (var filePath in EnumerateFiles(root, rejectReparsePoints: true, failOnEnumerationError: true))
        {
            var relativePath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
            if (!IndexingIgnoreRules.IsIgnoredPath(relativePath)) yield return relativePath;
        }
    }

    private static string ValidateRoot(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Repository path does not exist: {rootPath}");
        var root = Path.GetFullPath(rootPath);
        if (!WorkspacePathGuard.IsSafeWorkspaceRoot(root))
            throw new IOException("Repository root must be a real directory, not a symbolic link or reparse point.");
        return root;
    }

    private static IEnumerable<string> EnumerateFiles(string root, bool rejectReparsePoints, bool failOnEnumerationError)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var enumeratedEntries = 0;

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in EnumerateEntries(dir, failOnEnumerationError))
            {
                if (++enumeratedEntries > MaxEnumeratedEntries)
                    throw new InvalidOperationException($"Repository traversal stopped after {MaxEnumeratedEntries} entries; coverage is incomplete.");
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (IndexingIgnoreRules.IsIgnoredPath(relative)) continue;

                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (FileNotFoundException)
                {
                    if (rejectReparsePoints && HasLinkTarget(entry))
                        throw new InvalidOperationException($"Security scan stopped because repository entry '{relative}' is a dangling symbolic link.");
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    if (rejectReparsePoints && HasLinkTarget(entry))
                        throw new InvalidOperationException($"Security scan stopped because repository entry '{relative}' is a dangling symbolic link.");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (failOnEnumerationError) throw new IOException($"Could not inspect repository entry {relative}.", ex);
                    continue;
                }
                catch (IOException ex)
                {
                    if (failOnEnumerationError) throw new IOException($"Could not inspect repository entry {relative}.", ex);
                    continue;
                }

                var name = Path.GetFileName(entry);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                var isDevice = (attributes & FileAttributes.Device) != 0;
                if (isDirectory)
                {
                    if (IndexingIgnoreRules.IsIgnoredDirectory(name)) continue;
                    if (isReparsePoint || !WorkspacePathGuard.IsWithinWorkspace(root, entry))
                    {
                        if (rejectReparsePoints)
                            throw new InvalidOperationException($"Security scan stopped because repository path '{relative}' is a reparse point or escapes the workspace.");
                        continue;
                    }
                    stack.Push(entry);
                }
                else
                {
                    var escapesWorkspace = !WorkspacePathGuard.IsWithinWorkspace(root, entry);
                    var isRegularFile = rejectReparsePoints && WorkspacePathGuard.IsRegularFile(entry);
                    if (isReparsePoint || isDevice || escapesWorkspace || (rejectReparsePoints && !isRegularFile))
                    {
                        if (rejectReparsePoints)
                            throw new InvalidOperationException($"Security scan stopped because repository file '{relative}' is a reparse point, special file, or escapes the workspace.");
                        continue;
                    }
                    yield return entry;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateEntries(string directory, bool failOnError)
    {
        IEnumerator<string> enumerator;
        try { enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator(); }
        catch (UnauthorizedAccessException ex)
        {
            if (failOnError) throw new IOException($"Could not enumerate repository directory {directory}.", ex);
            return Array.Empty<string>();
        }
        catch (IOException ex)
        {
            if (failOnError) throw new IOException($"Could not enumerate repository directory {directory}.", ex);
            return Array.Empty<string>();
        }
        return EnumerateEntriesCore(enumerator, directory, failOnError);
    }

    private static IEnumerable<string> EnumerateEntriesCore(IEnumerator<string> enumerator, string directory, bool failOnError)
    {
        using (enumerator)
        {
            while (true)
            {
                bool hasNext;
                Exception? enumerationError = null;
                try { hasNext = enumerator.MoveNext(); }
                catch (UnauthorizedAccessException ex) { hasNext = false; enumerationError = ex; }
                catch (IOException ex) { hasNext = false; enumerationError = ex; }
                if (enumerationError is not null)
                {
                    if (failOnError) throw new IOException($"Could not enumerate repository directory {directory}.", enumerationError);
                    yield break;
                }
                if (!hasNext) yield break;
                yield return enumerator.Current;
            }
        }
    }

    private static bool HasLinkTarget(string path)
    {
        foreach (FileSystemInfo info in new FileSystemInfo[] { new FileInfo(path), new DirectoryInfo(path) })
        {
            try
            {
                if (!string.IsNullOrEmpty(info.LinkTarget)) return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private static long? TryGetFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException ex) { throw new IOException($"Could not inspect repository file '{path}'; indexing coverage is incomplete.", ex); }
        catch (IOException ex) { throw new IOException($"Could not inspect repository file '{path}'; indexing coverage is incomplete.", ex); }
    }

}
