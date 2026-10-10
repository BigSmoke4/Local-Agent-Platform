using LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;
using LocalAgentPlatform.Shared.Kernel.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

public sealed class RealProcessToolTests
{
    [Fact]
    public async Task TerminalTool_Runs_Real_Allowlisted_Process()
    {
        var dir = Directory.CreateTempSubdirectory("lap-terminal-");
        try
        {
            var tool = new TerminalTool(NullLogger<TerminalTool>.Instance);
            var command = OperatingSystem.IsWindows() ? "dotnet --version" : "dotnet --version";
            var result = await tool.ExecuteAsync(
                new Dictionary<string,string> { ["command"] = command },
                new ToolExecutionContext(dir.FullName, Guid.NewGuid(), ApprovalGranted: true));
            Assert.True(result.Success, result.Error);
            Assert.False(string.IsNullOrWhiteSpace(result.Output));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task TerminalTool_requires_one_time_approval_even_for_allowlisted_executables()
    {
        var dir = Directory.CreateTempSubdirectory("lap-terminal-approval-");
        try
        {
            var tool = new TerminalTool(NullLogger<TerminalTool>.Instance);
            var result = await tool.ExecuteAsync(
                new Dictionary<string, string> { ["command"] = "dotnet --version" },
                new ToolExecutionContext(dir.FullName, Guid.NewGuid()));

            Assert.False(result.Success);
            Assert.Contains("one-time human approval", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task BuildAndTestTools_require_approval_before_running_workspace_code()
    {
        var dir = Directory.CreateTempSubdirectory("lap-build-test-approval-");
        try
        {
            var parameters = new Dictionary<string, string> { ["target"] = "." };
            var context = new ToolExecutionContext(dir.FullName, Guid.NewGuid());
            var build = await new BuildTool().ExecuteAsync(parameters, context);
            var test = await new TestTool().ExecuteAsync(parameters, context);

            Assert.False(build.Success);
            Assert.Contains("one-time human approval", build.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(test.Success);
            Assert.Contains("one-time human approval", test.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task TerminalTool_rejects_shell_chaining_even_after_approval()
    {
        var dir = Directory.CreateTempSubdirectory("lap-terminal-shell-");
        try
        {
            var tool = new TerminalTool(NullLogger<TerminalTool>.Instance);
            var result = await tool.ExecuteAsync(
                new Dictionary<string, string> { ["command"] = "echo safe; echo unsafe" },
                new ToolExecutionContext(dir.FullName, Guid.NewGuid(), ApprovalGranted: true));
            Assert.False(result.Success);
            Assert.Contains("not supported", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task BuildTool_rejects_targets_outside_the_workspace()
    {
        var dir = Directory.CreateTempSubdirectory("lap-build-target-");
        try
        {
            var tool = new BuildTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, string> { ["target"] = "../outside.sln" },
                new ToolExecutionContext(dir.FullName, Guid.NewGuid(), ApprovalGranted: true));
            Assert.False(result.Success);
            Assert.Contains("inside the workspace", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ToolRiskLevel.High, tool.RiskLevel);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task GitTool_Runs_Real_Git_Process()
    {
        var dir = Directory.CreateTempSubdirectory("lap-git-");
        try
        {
            using var init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "git", Arguments = "init", WorkingDirectory = dir.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            Assert.NotNull(init); await init!.WaitForExitAsync(); Assert.Equal(0, init.ExitCode);
            var tool = new GitTool();
            var result = await tool.ExecuteAsync(new Dictionary<string,string> { ["subcommand"] = "status --short" }, new ToolExecutionContext(dir.FullName, Guid.NewGuid()));
            Assert.True(result.Success, result.Error);

            var arbitraryOutput = await tool.ExecuteAsync(
                new Dictionary<string, string> { ["subcommand"] = "diff --output=/tmp/lap-git-outside" },
                new ToolExecutionContext(dir.FullName, Guid.NewGuid()));
            Assert.False(arbitraryOutput.Success);
            Assert.Contains("not permitted", arbitraryOutput.Error, StringComparison.OrdinalIgnoreCase);

            var escapedPath = await tool.ExecuteAsync(
                new Dictionary<string, string> { ["subcommand"] = "diff -- ../../etc/passwd" },
                new ToolExecutionContext(dir.FullName, Guid.NewGuid()));
            Assert.False(escapedPath.Success);
            Assert.Contains("inside", escapedPath.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(true); }
    }
}
