using System.Security.Cryptography;
using System.Text;
using LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;
using LocalAgentPlatform.Shared.Kernel.Tools;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

public sealed class FileMutationToolTests
{
    [Fact]
    public async Task FileWrite_requires_NEW_for_creation_and_refuses_stale_overwrite()
    {
        var root = Directory.CreateTempSubdirectory("lap-file-write-");
        try
        {
            var tool = new FileWriteTool();
            var context = new ToolExecutionContext(root.FullName, Guid.NewGuid());
            var path = Path.Combine(root.FullName, "notes.txt");

            var missingHash = await tool.ExecuteAsync(new Dictionary<string, string>
            {
                ["path"] = "notes.txt", ["content"] = "first", ["expectedHash"] = ""
            }, context);
            Assert.False(missingHash.Success);
            Assert.False(File.Exists(path));

            var created = await tool.ExecuteAsync(new Dictionary<string, string>
            {
                ["path"] = "notes.txt", ["content"] = "first", ["expectedHash"] = "NEW"
            }, context);
            Assert.True(created.Success, created.Error);
            Assert.Equal("first", await File.ReadAllTextAsync(path));

            var staleHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("first")));
            await File.WriteAllTextAsync(path, "external edit");
            var refused = await tool.ExecuteAsync(new Dictionary<string, string>
            {
                ["path"] = "notes.txt", ["content"] = "agent overwrite", ["expectedHash"] = staleHash
            }, context);
            Assert.False(refused.Success);
            Assert.Contains("changed since", refused.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("external edit", await File.ReadAllTextAsync(path));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task FileEdit_requires_the_hash_of_the_current_file()
    {
        var root = Directory.CreateTempSubdirectory("lap-file-edit-");
        try
        {
            var path = Path.Combine(root.FullName, "sample.txt");
            await File.WriteAllTextAsync(path, "before value after");
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var tool = new FileEditTool();
            var context = new ToolExecutionContext(root.FullName, Guid.NewGuid());

            var result = await tool.ExecuteAsync(new Dictionary<string, string>
            {
                ["path"] = "sample.txt",
                ["oldText"] = "value",
                ["newText"] = "updated",
                ["expectedHash"] = hash
            }, context);

            Assert.True(result.Success, result.Error);
            Assert.Equal("before updated after", await File.ReadAllTextAsync(path));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task FileWrite_rejects_dangling_directory_symlinks_before_creating_outside_paths()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("lap-dangling-link-root-");
        var outside = Directory.CreateTempSubdirectory("lap-dangling-link-outside-");
        try
        {
            var futureOutsideDirectory = Path.Combine(outside.FullName, "not-created-yet");
            Directory.CreateSymbolicLink(Path.Combine(root.FullName, "escape"), futureOutsideDirectory);
            var result = await new FileWriteTool().ExecuteAsync(new Dictionary<string, string>
            {
                ["path"] = "escape/new/file.txt", ["content"] = "must not escape", ["expectedHash"] = "NEW"
            }, new ToolExecutionContext(root.FullName, Guid.NewGuid()));

            Assert.False(result.Success);
            Assert.False(Directory.Exists(futureOutsideDirectory));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Some hosts disable unprivileged symbolic-link creation.
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task File_tools_reject_symlink_targets_that_escape_the_workspace()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("lap-symlink-root-");
        var outside = Directory.CreateTempSubdirectory("lap-symlink-outside-");
        try
        {
            var secret = Path.Combine(outside.FullName, "secret.txt");
            await File.WriteAllTextAsync(secret, "must not be read");
            File.CreateSymbolicLink(Path.Combine(root.FullName, "linked.txt"), secret);
            var context = new ToolExecutionContext(root.FullName, Guid.NewGuid());

            var read = await new FileReadTool().ExecuteAsync(
                new Dictionary<string, string> { ["path"] = "linked.txt" }, context);
            var write = await new FileWriteTool().ExecuteAsync(new Dictionary<string, string>
            {
                ["path"] = "linked.txt", ["content"] = "overwrite", ["expectedHash"] = "NEW"
            }, context);

            Assert.False(read.Success);
            Assert.False(write.Success);
            Assert.Equal("must not be read", await File.ReadAllTextAsync(secret));
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }
}
