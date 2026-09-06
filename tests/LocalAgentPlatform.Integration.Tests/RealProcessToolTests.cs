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
            var result = await tool.ExecuteAsync(new Dictionary<string,string> { ["command"] = command }, new ToolExecutionContext(dir.FullName, Guid.NewGuid()));
            Assert.True(result.Success, result.Error);
            Assert.False(string.IsNullOrWhiteSpace(result.Output));
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
        }
        finally { dir.Delete(true); }
    }
}
