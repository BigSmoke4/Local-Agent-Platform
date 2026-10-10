using System.Text.RegularExpressions;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Security;

namespace LocalAgentPlatform.Modules.Verification.Infrastructure.Security;

public sealed record SecurityFinding(string FilePath, int LineNumber, string Pattern, string Severity, string Excerpt);

public interface ISecurityPatternScanner
{
    /// <summary>Scans real files under the repository root for a fixed set of known-risky
    /// patterns. Returns only what it actually finds — an empty list is a real "0 findings",
    /// never a placeholder for "not implemented".</summary>
    Task<IReadOnlyList<SecurityFinding>> ScanAsync(string repositoryRootPath, IReadOnlyList<string> relativeFilePaths, CancellationToken ct = default);
}

/// <summary>
/// Deliberately narrow, deterministic pattern matching — not a full SAST tool. Flags
/// hardcoded secrets, weak crypto, and a couple of classic injection smells. Every
/// finding cites the real file/line/excerpt so it can be manually verified; there is no
/// severity scoring beyond a flat High/Medium split and no suppression/allowlist system
/// yet (see docs/STATUS.md).
/// </summary>
public sealed class RegexSecurityPatternScanner : ISecurityPatternScanner
{
    private static readonly (Regex Pattern, string Label, string Severity)[] Rules =
    {
        (new Regex(@"(?i)(api[_-]?key|secret|password)\s*=\s*""[^""]{8,}""", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            "Hardcoded credential-like literal", "High"),
        (new Regex(@"(?i)Server=.*;.*Password=[^;""]+;", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            "Connection string with inline password", "High"),
        (new Regex(@"\bnew\s+MD5CryptoServiceProvider\b|\bMD5\.Create\(\)|\bnew\s+SHA1CryptoServiceProvider\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            "Use of a weak hash algorithm (MD5/SHA1) — avoid for security-sensitive hashing", "Medium"),
        (new Regex(@"""\s*\+\s*\w+\s*\+\s*""[^""]*(SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)),
            "String-concatenated SQL — possible SQL injection risk, prefer parameterized queries", "High"),
        (new Regex(@"\bDangerousGetHttpClientHandler\b|\bServerCertificateCustomValidationCallback\s*=\s*.*=>\s*true", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            "TLS certificate validation appears to be disabled", "High"),
    };

    public async Task<IReadOnlyList<SecurityFinding>> ScanAsync(
        string repositoryRootPath, IReadOnlyList<string> relativeFilePaths, CancellationToken ct = default)
    {
        const long maxFileBytes = 2 * 1024 * 1024;
        const long maxTotalBytes = 64 * 1024 * 1024;
        const int maxFindings = 10_000;
        var findings = new List<SecurityFinding>();
        long totalBytes = 0;

        foreach (var relativePath in relativeFilePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(relativePath)) continue;
            if (!WorkspacePathGuard.IsWithinWorkspace(repositoryRootPath, relativePath))
                throw new InvalidOperationException($"Security scan refused a path outside the workspace: {relativePath}");
            var fullPath = Path.GetFullPath(Path.Combine(repositoryRootPath, relativePath));
            if (!WorkspacePathGuard.IsRegularFile(fullPath))
                throw new InvalidOperationException($"Security scan refused a non-regular file: {relativePath}; verification is incomplete.");
            long fileLength;
            try { fileLength = new FileInfo(fullPath).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The scanner received this path from a complete inventory. If it
                // disappeared or became unreadable before inspection, fail closed
                // rather than silently claiming the remaining subset was scanned.
                throw new InvalidOperationException($"Security scan could not inspect {relativePath}; verification is incomplete.", ex);
            }
            if (fileLength > maxFileBytes)
                throw new InvalidOperationException($"Security scan stopped: {relativePath} exceeds the {maxFileBytes}-byte per-file limit; verification is incomplete.");
            totalBytes += fileLength;
            if (totalBytes > maxTotalBytes)
                throw new InvalidOperationException($"Security scan stopped: source file total exceeds the {maxTotalBytes}-byte scan budget; verification is incomplete.");

            try
            {
                await using var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > maxFileBytes)
                    throw new InvalidOperationException($"Security scan stopped: {relativePath} grew beyond the {maxFileBytes}-byte per-file limit; verification is incomplete.");

                // Buffer at most the configured per-file byte limit before decoding so a
                // concurrently growing file cannot create an unbounded line/string while
                // StreamReader.ReadLineAsync is accumulating input.
                using var contents = new MemoryStream((int)Math.Min(stream.Length, maxFileBytes));
                var buffer = new byte[64 * 1024];
                long bytesRead = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    bytesRead += read;
                    if (bytesRead > maxFileBytes)
                        throw new InvalidOperationException($"Security scan stopped: {relativePath} grew beyond the {maxFileBytes}-byte per-file limit; verification is incomplete.");
                    await contents.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (bytesRead > fileLength)
                {
                    totalBytes += bytesRead - fileLength;
                    if (totalBytes > maxTotalBytes)
                        throw new InvalidOperationException($"Security scan stopped: source file total exceeds the {maxTotalBytes}-byte scan budget; verification is incomplete.");
                }

                contents.Position = 0;
                using var reader = new StreamReader(contents, detectEncodingFromByteOrderMarks: true);
                var lineNumber = 0;
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    lineNumber++;
                    foreach (var (pattern, label, severity) in Rules)
                    {
                        if (!pattern.IsMatch(line)) continue;
                        var redactedExcerpt = SecretRedactor.Redact(line.Trim());
                        var boundedExcerpt = redactedExcerpt.Length > 160 ? redactedExcerpt[..160] + "..." : redactedExcerpt;
                        if (findings.Count >= maxFindings)
                            throw new InvalidOperationException($"Security scan stopped after {maxFindings} findings; verification is incomplete.");
                        findings.Add(new SecurityFinding(
                            relativePath, lineNumber, label, severity, boundedExcerpt));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Security scan could not read {relativePath}; verification is incomplete.", ex);
            }
        }

        return findings;
    }
}
