namespace LocalAgentPlatform.Modules.Tools.Domain;

public enum CommandDecision { Allow, Deny, RequireApproval }

public sealed record CommandPolicyResult(CommandDecision Decision, string Reason);

/// <summary>
/// Pure policy logic for terminal command execution — no process spawning, no I/O.
/// The infrastructure-layer TerminalTool consults this before ever invoking a shell
/// (spec Section 11: "never blindly execute arbitrary commands").
/// </summary>
public static class CommandPolicyEngine
{
    /// <summary>Executable names that are always denied outright, regardless of arguments —
    /// these have essentially no legitimate use inside an agent's terminal tool.</summary>
    private static readonly string[] DenylistedExecutables =
    {
        "mkfs", "fdisk", "parted", "dd", "shutdown", "reboot", "poweroff", "halt",
        "passwd", "chpasswd", "userdel", "visudo"
    };

    /// <summary>Substrings anywhere in the full command line that indicate a destructive
    /// or credential-extraction operation and require explicit human approval even if
    /// the base executable is otherwise allowed.</summary>
    private static readonly string[] DangerousPatterns =
    {
        "rm -rf /", "rm -rf ~", "rm -rf *", ":(){ :|:& };:", // fork bomb
        "> /dev/sda", "curl | sh", "wget | sh", "curl|sh", "wget|sh",
        "chmod -r 777 /", "chmod 777 /", ".ssh/id_rsa", ".aws/credentials",
        "format c:", "del /s /q c:\\", "net user", "reg delete"
    };

    /// <summary>Executables considered safe for unattended (non-approval) execution when
    /// nothing dangerous is detected in the full command line.</summary>
    private static readonly string[] DefaultAllowedExecutables =
    {
        "git", "dotnet", "npm", "node", "ls", "dir", "cat", "type", "echo",
        "grep", "find", "pwd", "whoami", "dotnet-ef"
    };

    public static CommandPolicyResult Evaluate(
        string commandLine,
        IReadOnlyCollection<string>? extraAllowedExecutables = null,
        IReadOnlyCollection<string>? extraDeniedExecutables = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return new CommandPolicyResult(CommandDecision.Deny, "Empty command.");

        var normalized = commandLine.Trim();
        var lower = normalized.ToLowerInvariant();

        foreach (var pattern in DangerousPatterns)
        {
            if (lower.Contains(pattern))
                return new CommandPolicyResult(CommandDecision.RequireApproval,
                    $"Command contains a recognized dangerous pattern ('{pattern}') and requires explicit approval.");
        }

        var executable = ExtractExecutable(normalized);

        if (DenylistedExecutables.Contains(executable, StringComparer.OrdinalIgnoreCase) ||
            (extraDeniedExecutables?.Contains(executable, StringComparer.OrdinalIgnoreCase) ?? false))
        {
            return new CommandPolicyResult(CommandDecision.Deny, $"Executable '{executable}' is denylisted.");
        }

        var allowed = DefaultAllowedExecutables.Contains(executable, StringComparer.OrdinalIgnoreCase) ||
                      (extraAllowedExecutables?.Contains(executable, StringComparer.OrdinalIgnoreCase) ?? false);

        if (!allowed)
            return new CommandPolicyResult(CommandDecision.RequireApproval,
                $"Executable '{executable}' is not on the default allowlist; approval required.");

        // Even allowlisted executables get flagged for approval on clearly destructive subcommands.
        if (executable.Equals("git", StringComparison.OrdinalIgnoreCase) &&
            (lower.Contains("push --force") || lower.Contains("reset --hard") || lower.Contains(" clean -fdx")))
        {
            return new CommandPolicyResult(CommandDecision.RequireApproval,
                "Destructive git subcommand requires explicit approval.");
        }

        return new CommandPolicyResult(CommandDecision.Allow, $"'{executable}' is allowlisted and no dangerous pattern was found.");
    }

    /// <summary>Rejects any path argument that would escape the given workspace root
    /// (spec Section 38: "path traversal protection").</summary>
    public static bool IsWithinWorkspace(string workspaceRootPath, string requestedPath)
    {
        var fullWorkspace = Path.GetFullPath(workspaceRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRequested = Path.GetFullPath(Path.IsPathRooted(requestedPath) ? requestedPath : Path.Combine(workspaceRootPath, requestedPath));
        return fullRequested.StartsWith(fullWorkspace, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts the base executable name from a full command line (e.g.
    /// "/usr/bin/git status" -> "git"). Public so persistent per-executable
    /// Allow/Deny scoping (spec Section 11) can key off the same extraction logic
    /// the policy engine itself uses, rather than duplicating it.</summary>
    public static string ExtractExecutable(string commandLine)
    {
        var trimmed = commandLine.TrimStart();
        var spaceIndex = trimmed.IndexOfAny(new[] { ' ', '\t' });
        var token = spaceIndex >= 0 ? trimmed[..spaceIndex] : trimmed;
        // Strip a leading path (e.g. /usr/bin/git -> git) so allowlist matching is name-based.
        return Path.GetFileName(token);
    }
}
