using System.Text;
using LocalAgentPlatform.Shared.Kernel.Files;

namespace LocalAgentPlatform.Modules.Tools.Domain;

public enum CommandDecision { Allow, Deny, RequireApproval }

/// <summary>
/// Pure policy logic for terminal command execution — no process spawning, no I/O.
/// Terminal commands are parsed into an executable and argument vector and are never
/// passed through a shell. Shell control syntax is rejected instead of being guessed at.
/// </summary>
public sealed record CommandPolicyResult(
    CommandDecision Decision,
    string Reason,
    bool CanPersistApproval = false);

public sealed record ParsedCommand(string Executable, IReadOnlyList<string> Arguments);

public static class CommandPolicyEngine
{
    /// <summary>Executables that are never allowed, even after interactive approval.</summary>
    private static readonly string[] DenylistedExecutables =
    {
        "mkfs", "fdisk", "parted", "dd", "shutdown", "reboot", "poweroff", "halt",
        "passwd", "chpasswd", "userdel", "visudo"
    };

    /// <summary>Command text that must receive fresh, one-time approval. These decisions
    /// cannot be converted into a persistent Always-Allow rule.</summary>
    private static readonly string[] DangerousPatterns =
    {
        "rm -rf /", "rm -rf ~", "rm -rf *", ":(){ :|:& };:",
        "> /dev/sda", "curl | sh", "wget | sh", "curl|sh", "wget|sh",
        "chmod -r 777 /", "chmod 777 /", ".ssh/id_rsa", ".aws/credentials",
        "format c:", "del /s /q c:\\", "net user", "reg delete"
    };

    private static readonly string[] DefaultAllowedExecutables =
    {
        "git", "dotnet", "npm", "node", "ls", "dir", "cat", "type",
        "grep", "find", "pwd", "whoami", "dotnet-ef"
    };

    /// <summary>
    /// Parse a deliberately small, shell-free command-line format. Quotes group argument
    /// text; environment expansion, pipelines, command chaining, redirects, substitutions,
    /// and other shell syntax are not supported. The result is suitable for
    /// ProcessStartInfo.ArgumentList and cannot turn an argument into a second command.
    /// </summary>
    public static bool TryParseSimpleCommand(string? commandLine, out ParsedCommand? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            error = "Empty command.";
            return false;
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var tokenStarted = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c is '\r' or '\n')
            {
                error = "Newlines are not supported in terminal commands.";
                return false;
            }

            if (quote == '\'')
            {
                if (c == '\'') quote = '\0';
                else current.Append(c);
                tokenStarted = true;
                continue;
            }

            if (quote == '"')
            {
                if (c == '"')
                {
                    quote = '\0';
                }
                else if (c == '\\' && i + 1 < commandLine.Length && (commandLine[i + 1] is '"' or '\\'))
                {
                    current.Append(commandLine[++i]);
                }
                else
                {
                    current.Append(c);
                }
                tokenStarted = true;
                continue;
            }

            if (c == '\\')
            {
                // Keep ordinary Windows path separators; only treat backslash as an
                // escape for whitespace/quotes/backslash in the small supported grammar.
                if (i + 1 < commandLine.Length && (char.IsWhiteSpace(commandLine[i + 1]) || commandLine[i + 1] is '\'' or '"' or '\\'))
                    current.Append(commandLine[++i]);
                else
                    current.Append(c);
                tokenStarted = true;
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                tokenStarted = true;
                continue;
            }

            if (c is ';' or '|' or '&' or '<' or '>' or '`' or '$' or '(' or ')')
            {
                error = $"Shell control syntax ('{c}') is not supported. Use separate tool calls for separate commands.";
                return false;
            }

            if (char.IsWhiteSpace(c))
            {
                if (tokenStarted)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }
                continue;
            }

            current.Append(c);
            tokenStarted = true;
        }

        if (quote != '\0')
        {
            error = "Command contains an unterminated quote.";
            return false;
        }

        if (tokenStarted) tokens.Add(current.ToString());
        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
        {
            error = "Command must begin with an executable name.";
            return false;
        }

        parsed = new ParsedCommand(tokens[0], tokens.Skip(1).ToArray());
        return true;
    }

    public static CommandPolicyResult Evaluate(
        string commandLine,
        IReadOnlyCollection<string>? extraAllowedExecutables = null,
        IReadOnlyCollection<string>? extraDeniedExecutables = null)
    {
        if (!TryParseSimpleCommand(commandLine, out var parsed, out var parseError) || parsed is null)
            return new CommandPolicyResult(CommandDecision.Deny, parseError ?? "Invalid command.");

        var executable = parsed.Executable;
        var executableBase = Path.GetFileNameWithoutExtension(executable);
        if (!string.Equals(Path.GetFileName(executable), executable, StringComparison.Ordinal))
        {
            return new CommandPolicyResult(CommandDecision.Deny,
                "Explicit executable paths are not allowed; use an approved executable name.");
        }

        if (DenylistedExecutables.Contains(executable, StringComparer.OrdinalIgnoreCase) ||
            DenylistedExecutables.Contains(executableBase, StringComparer.OrdinalIgnoreCase) ||
            (extraDeniedExecutables?.Contains(executable, StringComparer.OrdinalIgnoreCase) ?? false) ||
            (extraDeniedExecutables?.Contains(executableBase, StringComparer.OrdinalIgnoreCase) ?? false))
        {
            return new CommandPolicyResult(CommandDecision.Deny, $"Executable '{executable}' is denylisted.");
        }

        var normalized = commandLine.Trim();
        var lower = normalized.ToLowerInvariant();
        foreach (var pattern in DangerousPatterns)
        {
            if (lower.Contains(pattern, StringComparison.Ordinal))
            {
                return new CommandPolicyResult(CommandDecision.RequireApproval,
                    $"Command contains a recognized dangerous pattern ('{pattern}') and requires fresh, one-time approval.");
            }
        }

        var allowed = DefaultAllowedExecutables.Contains(executableBase, StringComparer.OrdinalIgnoreCase) ||
                      (extraAllowedExecutables?.Contains(executable, StringComparer.OrdinalIgnoreCase) ?? false) ||
                      (extraAllowedExecutables?.Contains(executableBase, StringComparer.OrdinalIgnoreCase) ?? false);

        if (!allowed)
        {
            return new CommandPolicyResult(CommandDecision.RequireApproval,
                $"Executable '{executable}' is not on the default allowlist; approval required.",
                CanPersistApproval: true);
        }

        if (executable.Equals("git", StringComparison.OrdinalIgnoreCase) &&
            (lower.Contains("push --force", StringComparison.Ordinal) ||
             lower.Contains("reset --hard", StringComparison.Ordinal) ||
             lower.Contains(" clean -fdx", StringComparison.Ordinal)))
        {
            return new CommandPolicyResult(CommandDecision.RequireApproval,
                "Destructive git subcommand requires fresh, one-time approval.");
        }

        return new CommandPolicyResult(CommandDecision.Allow,
            $"'{executable}' is allowlisted and no dangerous pattern was found.");
    }

    /// <summary>Returns whether an absolute or relative path resolves within an existing,
    /// real workspace root. Linked/reparse-point root components are rejected; requested
    /// links are resolved and rejected if they escape the workspace.
    /// </summary>
    public static bool IsWithinWorkspace(string workspaceRootPath, string requestedPath) =>
        WorkspacePathGuard.IsSafeWorkspaceRoot(workspaceRootPath) &&
        WorkspacePathGuard.IsWithinWorkspace(workspaceRootPath, requestedPath);

    /// <summary>Extracts the executable name using the same parser as Evaluate so that
    /// persistent permissions cannot disagree with command-policy parsing.</summary>
    public static string ExtractExecutable(string commandLine) =>
        TryParseSimpleCommand(commandLine, out var parsed, out _) && parsed is not null
            ? Path.GetFileNameWithoutExtension(parsed.Executable)
            : string.Empty;
}
