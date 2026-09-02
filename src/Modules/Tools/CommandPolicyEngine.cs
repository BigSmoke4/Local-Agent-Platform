using System.Text.RegularExpressions;

namespace Platform.Web.Services.Tools;

public enum CommandDecision
{
    Allow,
    RequireApproval,
    Deny
}

public record CommandPolicyResult(CommandDecision Decision, string Reason);

/// <summary>
/// Evaluates shell commands against allow/deny rules before any terminal
/// tool is permitted to execute them. Dangerous commands are denied outright;
/// everything else not explicitly allowed requires human approval.
/// </summary>
public class CommandPolicyEngine
{
    private static readonly string[] DeniedPatterns =
    {
        @"rm\s+-rf\s+/(\s|$)",
        @"format\s+[a-zA-Z]:",
        @"mkfs",
        @":(\)\{.*\};:",   // fork bomb
        @"dd\s+if=.*of=/dev/",
        @"shutdown",
        @"reboot",
        @">\s*/dev/sd[a-z]",
    };

    private static readonly string[] AutoAllowedPrefixes =
    {
        "git status",
        "git diff",
        "git log",
        "dotnet build",
        "dotnet test",
        "ls ",
        "dir ",
    };

    public CommandPolicyResult Evaluate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new CommandPolicyResult(CommandDecision.Deny, "Empty command.");

        foreach (var pattern in DeniedPatterns)
        {
            if (Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase))
                return new CommandPolicyResult(CommandDecision.Deny, $"Matched denylist pattern: {pattern}");
        }

        foreach (var prefix in AutoAllowedPrefixes)
        {
            if (command.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new CommandPolicyResult(CommandDecision.Allow, $"Matched allowlist prefix: {prefix}");
        }

        return new CommandPolicyResult(CommandDecision.RequireApproval, "Command is not on the allowlist; explicit approval required.");
    }
}
