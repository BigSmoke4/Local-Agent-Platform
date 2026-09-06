using LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ToolExecutionServiceType = LocalAgentPlatform.Modules.Tools.Application.Services.ToolExecutionService;

namespace LocalAgentPlatform.Integration.Tests;

[Collection("Postgres")]
public class ToolExecutionServiceTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _tempRepoPath;

    public ToolExecutionServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _tempRepoPath = Path.Combine(Path.GetTempPath(), "lap-tools-integration-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRepoPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRepoPath)) Directory.Delete(_tempRepoPath, recursive: true);
    }

    private async Task<Guid> RegisterRepositoryAsync(LocalAgentPlatform.Shared.Data.PlatformDbContext db)
    {
        var project = new Project { Name = $"ToolsIntegrationProject-{Guid.NewGuid()}", OwnerUserId = Guid.NewGuid() };
        db.Projects.Add(project);
        var repo = new Repository { ProjectId = project.Id, LocalPath = _tempRepoPath };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }

    [Fact]
    public async Task FileReadTool_reads_a_real_file_and_writes_a_real_audit_row()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempRepoPath, "notes.txt"), "hello from disk");

        await using var db = _fixture.CreateContext();
        var repositoryId = await RegisterRepositoryAsync(db);

        ITool[] tools = { new FileReadTool() };
        var service = new ToolExecutionServiceType(tools, db, new LocalAgentPlatform.Modules.Tools.Application.Services.CommandPermissionService(db), NullLogger<ToolExecutionServiceType>.Instance);

        var outcome = await service.InvokeAsync(
            "FileReadTool", repositoryId, new Dictionary<string, string> { ["path"] = "notes.txt" }, approved: true);

        Assert.Equal("Allowed", outcome.Decision);
        Assert.True(outcome.Result?.Success);
        Assert.Equal("hello from disk", outcome.Result!.Output);

        var auditRow = await db.ToolExecutions.FirstOrDefaultAsync(e => e.Id == outcome.ExecutionId);
        Assert.NotNull(auditRow);
        Assert.Equal("FileReadTool", auditRow!.ToolName);
        Assert.True(auditRow.Success);
    }

    [Fact]
    public async Task FileReadTool_refuses_path_traversal_outside_the_workspace()
    {
        await using var db = _fixture.CreateContext();
        var repositoryId = await RegisterRepositoryAsync(db);

        ITool[] tools = { new FileReadTool() };
        var service = new ToolExecutionServiceType(tools, db, new LocalAgentPlatform.Modules.Tools.Application.Services.CommandPermissionService(db), NullLogger<ToolExecutionServiceType>.Instance);

        var outcome = await service.InvokeAsync(
            "FileReadTool", repositoryId, new Dictionary<string, string> { ["path"] = "../../etc/passwd" }, approved: true);

        Assert.False(outcome.Result?.Success ?? true);
        Assert.Contains("workspace", outcome.Result?.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_tool_name_throws_rather_than_silently_no_opping()
    {
        await using var db = _fixture.CreateContext();
        var repositoryId = await RegisterRepositoryAsync(db);

        ITool[] tools = { new FileReadTool() };
        var service = new ToolExecutionServiceType(tools, db, new LocalAgentPlatform.Modules.Tools.Application.Services.CommandPermissionService(db), NullLogger<ToolExecutionServiceType>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InvokeAsync("NoSuchTool", repositoryId, new Dictionary<string, string>(), approved: true));
    }
}
