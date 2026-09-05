using LocalAgentPlatform.Modules.RepositoryAnalysis.Application.Services;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

[Collection("Postgres")]
public class RepositoryIndexingServiceTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _tempRepoPath;

    public RepositoryIndexingServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _tempRepoPath = Path.Combine(Path.GetTempPath(), "lap-integration-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRepoPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRepoPath)) Directory.Delete(_tempRepoPath, recursive: true);
    }

    private RepositoryIndexingService CreateService(LocalAgentPlatform.Shared.Data.PlatformDbContext db) =>
        new(db, new RepositoryFileScanner(), new[] { new RoslynCSharpSymbolExtractor() }, NullLogger<RepositoryIndexingService>.Instance);

    private async Task<Guid> RegisterRepositoryAsync(LocalAgentPlatform.Shared.Data.PlatformDbContext db)
    {
        var project = new Project { Name = $"IntegrationTestProject-{Guid.NewGuid()}", OwnerUserId = Guid.NewGuid() };
        db.Projects.Add(project);
        var repo = new Repository { ProjectId = project.Id, LocalPath = _tempRepoPath };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }

    [Fact]
    public async Task Indexing_a_real_cs_file_extracts_real_symbols()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempRepoPath, "Sample.cs"), """
            namespace Demo;
            public class Widget
            {
                public int Count { get; set; }
                public void DoWork() { }
            }
            """);

        await using var db = _fixture.CreateContext();
        var repositoryId = await RegisterRepositoryAsync(db);
        var service = CreateService(db);

        var job = await service.RunIndexingAsync(repositoryId);

        Assert.Equal("Completed", job.Status);
        Assert.Equal(1, job.FilesChanged);

        var symbols = await db.CodeSymbols.Where(s => s.RepositoryId == repositoryId).ToListAsync();
        Assert.Contains(symbols, s => s.Name == "Widget" && s.Kind == "Class");
        Assert.Contains(symbols, s => s.Name == "Count" && s.Kind == "Property");
        Assert.Contains(symbols, s => s.Name == "DoWork" && s.Kind == "Method");
    }

    [Fact]
    public async Task Reindexing_unchanged_file_does_not_reprocess_it()
    {
        var filePath = Path.Combine(_tempRepoPath, "Unchanged.cs");
        await File.WriteAllTextAsync(filePath, "namespace Demo; public class Stable { }");

        await using var db = _fixture.CreateContext();
        var repositoryId = await RegisterRepositoryAsync(db);
        var service = CreateService(db);

        var firstRun = await service.RunIndexingAsync(repositoryId);
        Assert.Equal(1, firstRun.FilesChanged);

        var secondRun = await service.RunIndexingAsync(repositoryId);
        Assert.Equal(0, secondRun.FilesChanged); // real incremental behavior — same hash, no reprocessing
    }

    [Fact]
    public async Task Deleting_a_file_soft_marks_its_snapshot_on_next_index()
    {
        var filePath = Path.Combine(_tempRepoPath, "Temp.cs");
        await File.WriteAllTextAsync(filePath, "namespace Demo; public class Temp { }");

        await using var db = _fixture.CreateContext();
        var repositoryId = await RegisterRepositoryAsync(db);
        var service = CreateService(db);

        await service.RunIndexingAsync(repositoryId);
        File.Delete(filePath);
        await service.RunIndexingAsync(repositoryId);

        var snapshot = await db.FileSnapshots.FirstOrDefaultAsync(f => f.RepositoryId == repositoryId && f.RelativePath == "Temp.cs");
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsDeleted);
    }
}
