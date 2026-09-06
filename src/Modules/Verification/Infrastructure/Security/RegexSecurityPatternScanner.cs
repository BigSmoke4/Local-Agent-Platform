using System.Text.RegularExpressions;

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
        (new Regex(@"(?i)(api[_-]?key|secret|password)\s*=\s*""[^""]{8,}""", RegexOptions.Compiled),
            "Hardcoded credential-like literal", "High"),
        (new Regex(@"(?i)Server=.*;.*Password=[^;""]+;", RegexOptions.Compiled),
            "Connection string with inline password", "High"),
        (new Regex(@"\bnew\s+MD5CryptoServiceProvider\b|\bMD5\.Create\(\)|\bnew\s+SHA1CryptoServiceProvider\b", RegexOptions.Compiled),
            "Use of a weak hash algorithm (MD5/SHA1) — avoid for security-sensitive hashing", "Medium"),
        (new Regex(@"""\s*\+\s*\w+\s*\+\s*""[^""]*(SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "String-concatenated SQL — possible SQL injection risk, prefer parameterized queries", "High"),
        (new Regex(@"\bDangerousGetHttpClientHandler\b|\bServerCertificateCustomValidationCallback\s*=\s*.*=>\s*true", RegexOptions.Compiled),
            "TLS certificate validation appears to be disabled", "High"),
    };

    public async Task<IReadOnlyList<SecurityFinding>> ScanAsync(
        string repositoryRootPath, IReadOnlyList<string> relativeFilePaths, CancellationToken ct = default)
    {
        var findings = new List<SecurityFinding>();

        foreach (var relativePath in relativeFilePaths)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(repositoryRootPath, relativePath);
            if (!File.Exists(fullPath)) continue;

            string[] lines;
            try { lines = await File.ReadAllLinesAsync(fullPath, ct); }
            catch (IOException) { continue; } // unreadable file — skip, don't fabricate a finding or a failure

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (pattern, label, severity) in Rules)
                {
                    if (pattern.IsMatch(lines[i]))
                    {
                        findings.Add(new SecurityFinding(
                            relativePath, i + 1, label, severity,
                            lines[i].Trim().Length > 160 ? lines[i].Trim()[..160] + "..." : lines[i].Trim()));
                    }
                }
            }
        }

        return findings;
    }
}
