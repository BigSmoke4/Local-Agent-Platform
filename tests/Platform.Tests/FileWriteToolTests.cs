using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Web.Services.Tools;
using Xunit;

namespace Platform.Tests;

public class FileWriteToolTests : IDisposable
{
    private readonly string _tempWorkspace;
    private readonly FileWriteTool _tool;

    public FileWriteToolTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), "platform-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempWorkspace);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspace:Root"] = _tempWorkspace })
            .Build();

        _tool = new FileWriteTool(config, NullLogger<FileWriteTool>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempWorkspace))
            Directory.Delete(_tempWorkspace, recursive: true);
    }

    [Fact]
    public async Task WriteAsync_NewFile_Succeeds()
    {
        var result = await _tool.WriteAsync("new.txt", "hello", expectedHash: null);
        Assert.True(result.Created);
        Assert.Equal(FileWriteTool.ComputeHash("hello"), result.NewHash);
    }

    [Fact]
    public async Task WriteAsync_WithCorrectExpectedHash_Succeeds()
    {
        await _tool.WriteAsync("existing.txt", "v1", expectedHash: null);
        var v1Hash = FileWriteTool.ComputeHash("v1");

        var result = await _tool.WriteAsync("existing.txt", "v2", expectedHash: v1Hash);
        Assert.False(result.Created);
        Assert.Equal(FileWriteTool.ComputeHash("v2"), result.NewHash);
    }

    [Fact]
    public async Task WriteAsync_WithStaleExpectedHash_ThrowsConflict()
    {
        await _tool.WriteAsync("existing.txt", "v1", expectedHash: null);

        await Assert.ThrowsAsync<FileWriteToolException>(() =>
            _tool.WriteAsync("existing.txt", "v2", expectedHash: "not-the-real-hash"));
    }

    [Fact]
    public async Task WriteAsync_PathTraversal_IsBlocked()
    {
        await Assert.ThrowsAsync<FileWriteToolException>(() =>
            _tool.WriteAsync("../../etc/passwd", "malicious", expectedHash: null));
    }
}
