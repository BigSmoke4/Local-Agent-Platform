using System.Security.Cryptography;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Domain;

namespace LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;

public sealed record ScannedFile(string RelativePath, string ContentHash, long SizeBytes, string? Language);

public interface IRepositoryFileScanner
{
    /// <summary>Enumerates every non-ignored file under rootPath with a real SHA-256 content hash.</summary>
    IAsyncEnumerable<ScannedFile> ScanAsync(string rootPath, CancellationToken ct = default);
}

/// <summary>
/// Real filesystem walker. Every hash returned is computed from actual file bytes via
/// SHA-256 — used by the indexing service to detect which files actually changed
/// since the last index run (Section 60: "process only changed files").
/// </summary>
public sealed class RepositoryFileScanner : IRepositoryFileScanner
{
    public async IAsyncEnumerable<ScannedFile> ScanAsync(
        string rootPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Repository path does not exist: {rootPath}");

        foreach (var filePath in EnumerateFiles(rootPath))
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            if (IndexingIgnoreRules.IsIgnoredPath(relativePath)) continue;

            FileInfo info;
            try { info = new FileInfo(filePath); }
            catch (IOException) { continue; } // file vanished mid-scan; skip rather than crash the whole job

            string hash;
            try
            {
                await using var stream = File.OpenRead(filePath);
                var hashBytes = await SHA256.HashDataAsync(stream, ct);
                hash = Convert.ToHexString(hashBytes);
            }
            catch (IOException)
            {
                continue; // locked/unreadable file; skip rather than fail the whole scan
            }

            yield return new ScannedFile(
                relativePath,
                hash,
                info.Length,
                IndexingIgnoreRules.DetectLanguage(filePath));
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subDirs;
            IEnumerable<string> files;

            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (!IndexingIgnoreRules.IsIgnoredDirectory(name))
                    stack.Push(sub);
            }

            foreach (var file in files)
                yield return file;
        }
    }
}
