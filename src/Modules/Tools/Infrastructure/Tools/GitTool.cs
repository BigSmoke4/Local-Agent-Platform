using System.Diagnostics;
using System.Globalization;
using System.Text;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Security;
using LocalAgentPlatform.Shared.Kernel.Tools;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>
/// Read-only Git inspection adapter. It accepts a narrow set of argument vectors,
/// disables external diff/textconv and fsmonitor hooks, and never passes input through
/// a shell. Mutating Git operations remain available only through the separately gated
/// TerminalTool and are not exposed here.
/// </summary>
public sealed class GitTool : ITool
{
    private const int MaxCapturedCharacters = 100_000;
    private static readonly HashSet<string> AllowedSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "branch", "show", "blame"
    };

    public string Name => "GitTool";
    public string Description => "Runs a restricted, read-only git status/diff/log/branch/show/blame command in the repository.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Low;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("subcommand", out var commandLine) || string.IsNullOrWhiteSpace(commandLine))
            return ToolExecutionResult.Fail("Missing required parameter 'subcommand'.");
        if (!CommandPolicyEngine.TryParseSimpleCommand(commandLine, out var parsed, out var parseError) || parsed is null)
            return ToolExecutionResult.Fail($"GitTool refused the command: {parseError}");

        var subcommand = parsed.Executable;
        if (!AllowedSubcommands.Contains(subcommand))
            return ToolExecutionResult.Fail($"Subcommand '{subcommand}' is not allowed by GitTool.");
        if (!WorkspacePathGuard.IsSafeWorkspaceRoot(context.RepositoryRootPath))
            return ToolExecutionResult.Fail("Repository workspace must be an existing real directory without linked path components.");
        var gitMetadata = Path.Combine(context.RepositoryRootPath, ".git");
        if (!Directory.Exists(gitMetadata))
            return ToolExecutionResult.Fail("GitTool requires an in-repository .git directory; linked-worktree gitfile metadata is not supported.");
        try
        {
            var metadataAttributes = File.GetAttributes(gitMetadata);
            if ((metadataAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
                !WorkspacePathGuard.IsWithinWorkspace(context.RepositoryRootPath, gitMetadata))
                return ToolExecutionResult.Fail("Git metadata must be a non-linked .git directory inside the repository workspace.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ToolExecutionResult.Fail("Git metadata could not be safely inspected.");
        }
        if (!TryValidateArguments(subcommand, parsed.Arguments, context.RepositoryRootPath, out var error))
            return ToolExecutionResult.Fail(error);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = context.RepositoryRootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--no-pager");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.fsmonitor=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("status.submoduleSummary=false");
        psi.ArgumentList.Add(subcommand);
        if (subcommand.Equals("diff", StringComparison.OrdinalIgnoreCase) ||
            subcommand.Equals("show", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add("--no-ext-diff");
        if (subcommand.Equals("diff", StringComparison.OrdinalIgnoreCase) ||
            subcommand.Equals("show", StringComparison.OrdinalIgnoreCase) ||
            subcommand.Equals("blame", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add("--no-textconv");
        if (subcommand.Equals("status", StringComparison.OrdinalIgnoreCase) ||
            subcommand.Equals("diff", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add("--ignore-submodules=all");
        if (subcommand.Equals("log", StringComparison.OrdinalIgnoreCase) ||
            subcommand.Equals("show", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add("--no-show-signature");
        foreach (var argument in parsed.Arguments) psi.ArgumentList.Add(argument);
        RestrictEnvironment(psi);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            process.Start();
            stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxCapturedCharacters);
            stderrTask = ReadBoundedAsync(process.StandardError, MaxCapturedCharacters);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = SecretRedactor.Redact(await stdoutTask);
            var stderr = SecretRedactor.Redact(await stderrTask);
            return process.ExitCode == 0
                ? ToolExecutionResult.Ok(stdout)
                : ToolExecutionResult.Fail(stderr, stdout) with { ExitCode = process.ExitCode };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            await DrainOutputAsync(stdoutTask, stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var (stdout, stderr) = await DrainOutputAsync(stdoutTask, stderrTask);
            return ToolExecutionResult.Fail($"Git command timed out after {Timeout.TotalSeconds:0}s. {SecretRedactor.Redact(stderr)}", SecretRedactor.Redact(stdout));
        }
        catch (Exception ex)
        {
            TryKill(process);
            await DrainOutputAsync(stdoutTask, stderrTask);
            return ToolExecutionResult.Fail($"git invocation failed: {SecretRedactor.Redact(ex.Message)}");
        }
    }

    private static bool TryValidateArguments(
        string subcommand, IReadOnlyList<string> arguments, string repositoryRoot, out string error)
    {
        error = "GitTool received an option or argument that is not permitted for this read-only subcommand.";
        var args = arguments.ToList();
        var separator = args.IndexOf("--");
        var beforeSeparator = separator >= 0 ? args.Take(separator).ToList() : args;
        var pathArguments = separator >= 0 ? args.Skip(separator + 1).ToList() : new List<string>();

        if (pathArguments.Any(path => path.StartsWith("-", StringComparison.Ordinal) ||
                                      !WorkspacePathGuard.IsWithinWorkspace(repositoryRoot, path)))
        {
            error = "Git path arguments must resolve inside the repository workspace.";
            return false;
        }

        bool HasOnlyOptions(params string[] allowed) => beforeSeparator.All(arg => allowed.Contains(arg, StringComparer.OrdinalIgnoreCase));
        bool HasOnlyRefsAndOptions(IReadOnlyCollection<string> allowedOptions, int maxRefs)
        {
            var refs = 0;
            foreach (var arg in beforeSeparator)
            {
                if (allowedOptions.Contains(arg, StringComparer.OrdinalIgnoreCase)) continue;
                if (arg.StartsWith("-", StringComparison.Ordinal) || !IsSafeRevision(arg) || ++refs > maxRefs) return false;
            }
            return true;
        }

        if (subcommand.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            if (separator >= 0 || pathArguments.Count != 0 ||
                !HasOnlyOptions("--short", "--branch", "--porcelain", "--porcelain=v1", "--porcelain=v2")) return false;
        }
        else if (subcommand.Equals("diff", StringComparison.OrdinalIgnoreCase))
        {
            if (!HasOnlyOptions("--stat", "--numstat", "--name-only", "--name-status", "--cached", "--staged", "--check", "--no-color")) return false;
        }
        else if (subcommand.Equals("log", StringComparison.OrdinalIgnoreCase))
        {
            var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "--oneline", "--decorate", "--all", "--graph", "--format=oneline", "--format=short",
                "--format=medium", "--format=fuller", "--no-color"
            };
            for (var i = 0; i < beforeSeparator.Count; i++)
            {
                var arg = beforeSeparator[i];
                if (options.Contains(arg)) continue;
                if (arg == "-n" && i + 1 < beforeSeparator.Count && IsBoundedInteger(beforeSeparator[++i], 1, 1000)) continue;
                if (arg.StartsWith("--max-count=", StringComparison.OrdinalIgnoreCase) &&
                    IsBoundedInteger(arg[12..], 1, 1000)) continue;
                if (arg.StartsWith("-", StringComparison.Ordinal) || !IsSafeRevision(arg)) return false;
            }
        }
        else if (subcommand.Equals("branch", StringComparison.OrdinalIgnoreCase))
        {
            if (separator >= 0 || pathArguments.Count != 0 ||
                !HasOnlyOptions("--list", "--all", "--remotes", "--show-current", "--verbose", "-v")) return false;
        }
        else if (subcommand.Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "--stat", "--name-only", "--no-patch", "--format=oneline", "--format=short", "--format=medium"
            };
            if (!HasOnlyRefsAndOptions(options, maxRefs: 1)) return false;
        }
        else if (subcommand.Equals("blame", StringComparison.OrdinalIgnoreCase))
        {
            if (pathArguments.Count != 1 || beforeSeparator.Count > 3) return false;
            var argsToCheck = beforeSeparator.ToList();
            if (argsToCheck.Count == 2 && !IsSafeRevision(argsToCheck[0])) return false;
            var lineIndex = argsToCheck.FindIndex(arg => arg == "-L");
            if (lineIndex >= 0)
            {
                if (lineIndex + 1 >= argsToCheck.Count || !IsSafeLineRange(argsToCheck[lineIndex + 1])) return false;
                argsToCheck.RemoveAt(lineIndex + 1);
                argsToCheck.RemoveAt(lineIndex);
            }
            if (argsToCheck.Any(arg => !IsSafeRevision(arg))) return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsSafeRevision(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.StartsWith("-", StringComparison.Ordinal)) return false;
        if (value is "HEAD" or "head") return true;
        if (value.Length is >= 4 and <= 64 && value.All(Uri.IsHexDigit)) return true;
        if (!value.StartsWith("HEAD", StringComparison.OrdinalIgnoreCase)) return false;
        return value.Skip(4).All(c => char.IsAsciiDigit(c) || c is '~' or '^');
    }

    private static bool IsSafeLineRange(string value)
    {
        var parts = value.Split(',');
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start > 0 &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end) && end >= start;
    }

    private static bool IsBoundedInteger(string value, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= min && count <= max;

    private static void RestrictEnvironment(ProcessStartInfo psi)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "HOME", "USERPROFILE", "SYSTEMROOT", "WINDIR", "TEMP", "TMP"
        };
        foreach (var name in psi.Environment.Keys.ToArray())
            if (!allowed.Contains(name)) psi.Environment.Remove(name);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
        {
            var remaining = maxCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        if (truncated) builder.Append("\n[output truncated at the capture limit]");
        return builder.ToString();
    }

    private static async Task<(string Stdout, string Stderr)> DrainOutputAsync(Task<string>? stdout, Task<string>? stderr)
    {
        try
        {
            if (stdout is null || stderr is null) return (string.Empty, string.Empty);
            await Task.WhenAll(stdout, stderr);
            return (await stdout, await stderr);
        }
        catch { return (string.Empty, string.Empty); }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
