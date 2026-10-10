using LocalAgentPlatform.Modules.Verification.Infrastructure.Security;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

public sealed class RegexSecurityPatternScannerTests
{
    [Fact]
    public async Task Reports_real_findings_but_redacts_the_secret_excerpt()
    {
        var root = Directory.CreateTempSubdirectory("lap-security-scan-");
        try
        {
            const string secret = "verySecretValue123";
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "credentials.cs"),
                $"class Settings {{ string api_key = \"{secret}\"; }}");

            var findings = await new RegexSecurityPatternScanner().ScanAsync(root.FullName, new[] { "credentials.cs" });

            var finding = Assert.Single(findings);
            Assert.Equal("High", finding.Severity);
            Assert.DoesNotContain(secret, finding.Excerpt, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", finding.Excerpt, StringComparison.Ordinal);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Rejects_a_missing_path_in_the_scan_inventory()
    {
        var root = Directory.CreateTempSubdirectory("lap-security-missing-");
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RegexSecurityPatternScanner().ScanAsync(root.FullName, new[] { "removed.cs" }));
            Assert.Contains("verification is incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Stops_instead_of_reporting_a_partial_scan_when_a_file_exceeds_the_limit()
    {
        var root = Directory.CreateTempSubdirectory("lap-security-limit-");
        try
        {
            var path = Path.Combine(root.FullName, "large.cs");
            await File.WriteAllBytesAsync(path, new byte[2 * 1024 * 1024 + 1]);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RegexSecurityPatternScanner().ScanAsync(root.FullName, new[] { "large.cs" }));
            Assert.Contains("verification is incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { root.Delete(recursive: true); }
    }
}
