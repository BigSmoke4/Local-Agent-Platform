using System.Net;
using LocalAgentPlatform.Shared.Kernel.Files;

namespace LocalAgentPlatform.Shared.Kernel.Security;

/// <summary>Fail-closed validation for settings that would otherwise make a deployed
/// instance unexpectedly public, ephemeral, or unable to constrain repository access.</summary>
public static class ProductionConfigurationPolicy
{
    public static IReadOnlyList<string> Validate(
        bool isDevelopment,
        string? allowedHosts,
        string? dataProtectionKeyRingPath,
        IEnumerable<string?>? allowedWorkspaceRoots)
    {
        if (isDevelopment) return Array.Empty<string>();

        var errors = new List<string>();
        var hosts = (allowedHosts ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(host => host.Contains('*')))
        {
            errors.Add("Set AllowedHosts to one or more exact production hostnames; wildcard or empty Host filtering is not permitted.");
        }
        else if (hosts.All(IsLoopbackHost))
        {
            errors.Add("Production AllowedHosts must include a non-loopback hostname.");
        }

        if (string.IsNullOrWhiteSpace(dataProtectionKeyRingPath) ||
            !Path.IsPathFullyQualified(dataProtectionKeyRingPath) ||
            !Directory.Exists(dataProtectionKeyRingPath))
        {
            errors.Add("Set DataProtection:KeyRingPath to an existing absolute persistent directory.");
        }

        var roots = (allowedWorkspaceRoots ?? Array.Empty<string?>())
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .ToArray();
        if (roots.Length == 0)
        {
            errors.Add("Configure at least one existing Repositories:AllowedRoots directory.");
        }
        else if (roots.Any(root => !IsSafeProductionWorkspaceRoot(root)))
        {
            errors.Add("Every production Repositories:AllowedRoots entry must resolve to an existing, safe, non-filesystem-root directory.");
        }

        return errors;
    }

    private static bool IsSafeProductionWorkspaceRoot(string root)
    {
        var resolved = WorkspacePathGuard.ResolveSafeWorkspaceRoot(root);
        if (resolved is null) return false;
        var filesystemRoot = Path.GetPathRoot(resolved);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.IsNullOrEmpty(filesystemRoot) ||
               !string.Equals(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   filesystemRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison);
    }

    private static bool IsLoopbackHost(string host)
    {
        var normalizedHost = host.TrimEnd('.');
        if (IPAddress.TryParse(normalizedHost, out var address)) return IPAddress.IsLoopback(address);

        // Accept an optional explicit port when classifying loopback literals. Otherwise
        // `localhost:8443` could be mistaken for a public hostname during startup checks.
        if (normalizedHost.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = normalizedHost.IndexOf(']');
            if (closingBracket > 0 && IPAddress.TryParse(normalizedHost[1..closingBracket], out address))
                return IPAddress.IsLoopback(address);
        }
        else
        {
            var colon = normalizedHost.LastIndexOf(':');
            if (colon > 0 && normalizedHost.IndexOf(':') == colon)
                normalizedHost = normalizedHost[..colon].TrimEnd('.');
        }

        return normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

}
