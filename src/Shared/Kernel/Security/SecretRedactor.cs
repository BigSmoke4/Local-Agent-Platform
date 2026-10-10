using System.Text.RegularExpressions;

namespace LocalAgentPlatform.Shared.Kernel.Security;

/// <summary>Best-effort redaction for common credential forms before command output or
/// static-analysis excerpts are persisted. This is defense in depth, not a guarantee
/// that arbitrary application-specific secrets are detected.</summary>
public static class SecretRedactor
{
    private static readonly (Regex Pattern, string Replacement)[] Patterns =
    {
        (new Regex(@"(?i)(authorization:\s*bearer\s+)[a-z0-9._~+/=-]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), "$1[REDACTED]"),
        (new Regex(@"(?i)(api[_-]?key|password|passwd|pwd|secret|access[_-]?token|client[_-]?secret|authorization)\s*([:=]\s*|\s+)(""[^""]*""|'[^']*'|[^\s,;]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), "$1$2[REDACTED]"),
        (new Regex(@"(?i)(bearer\s+)[a-z0-9._~+/=-]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), "$1[REDACTED]"),
        (new Regex(@"lap_[a-f0-9]{48}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)), "lap_[REDACTED]"),
        (new Regex(@"(?i)(postgres(?:ql)?://[^:/\s]+:)[^@\s]+(@)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), "$1[REDACTED]$2"),
        (new Regex(@"-----BEGIN [^-]+PRIVATE KEY-----[\s\S]*?-----END [^-]+PRIVATE KEY-----",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), "[PRIVATE KEY REDACTED]")
    };

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        foreach (var (pattern, replacement) in Patterns)
            text = pattern.Replace(text, replacement);
        return text;
    }
}
