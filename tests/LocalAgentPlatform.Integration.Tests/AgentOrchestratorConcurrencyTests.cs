using LocalAgentPlatform.Modules.Agent.Application.Services;
using LocalAgentPlatform.Modules.Memory.Application.Services;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Application.Services;
using LocalAgentPlatform.Modules.Tools.Application.Services;
using LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Models;
using LocalAgentPlatform.Shared.Kernel.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

[Collection("Postgres")]
public sealed class AgentOrchestratorConcurrencyTests
{
    private readonly PostgresFixture _fixture;

    public AgentOrchestratorConcurrencyTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("Created")]
    [InlineData("AwaitingApproval")]
    public async Task Pending_cancellation_is_owner_scoped_and_finishes_task_transitions(string state)
    {
        Guid ownerId;
        Guid sessionId;
        await using (var setup = _fixture.CreateContext())
        {
            (ownerId, sessionId) = await AddSessionAsync(setup, state, addTasks: true);
        }

        await using var db = _fixture.CreateContext();
        var service = CreateService(db);
        Assert.False(await service.CancelPendingAsync(sessionId, Guid.NewGuid()));
        await using (var afterWrongOwner = _fixture.CreateContext())
        {
            Assert.Equal(state, await afterWrongOwner.AgentSessions.AsNoTracking()
                .Where(s => s.Id == sessionId).Select(s => s.State).SingleAsync());
        }
        Assert.True(await service.CancelPendingAsync(sessionId, ownerId));
        Assert.False(await service.CancelPendingAsync(sessionId, ownerId));

        await using var verify = _fixture.CreateContext();
        var session = await verify.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal("Cancelled", session.State);
        Assert.NotNull(session.CompletedAtUtc);

        var tasks = await verify.AgentTaskNodes.AsNoTracking()
            .Where(t => t.AgentSessionId == sessionId)
            .ToDictionaryAsync(t => t.StepKey, t => t.Status);
        Assert.Equal("Cancelled", tasks["executing"]);
        Assert.Equal("Skipped", tasks["pending"]);
        Assert.Equal("Skipped", tasks["approval"]);
        Assert.Equal("Completed", tasks["completed"]);
        Assert.True(await verify.MemoryEntries.AnyAsync(m => m.SourceAgentSessionId == sessionId));
    }

    [Fact]
    public async Task Failed_task_marks_remaining_plan_tasks_skipped_and_normalizes_blank_path_arguments()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "lap-agent-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            Guid ownerId;
            Guid sessionId;
            await using (var setup = _fixture.CreateContext())
            {
                (ownerId, sessionId) = await AddSessionAsync(
                    setup, "Created", addTasks: false, maxRetries: 0, repositoryPath: workspace);
            }

            const string plan = """
                {"steps":[
                  {"id":"bad-path","description":"Read a file with blank path metadata","type":"ToolCall","toolName":"FileReadTool","arguments":{"path":" "},"dependsOn":[]},
                  {"id":"dependent","description":"A step depending on the failed read","type":"Reasoning","dependsOn":["bad-path"]}
                ]}
                """;
            await using var db = _fixture.CreateContext();
            var embeddings = new EmptyEmbeddingProvider();
            ITool[] tools = { new FileReadTool() };
            var toolService = new ToolExecutionService(
                tools, db, new CommandPermissionService(db), NullLogger<ToolExecutionService>.Instance,
                new WorkspaceRootPolicy(new[] { workspace }));
            var service = new AgentOrchestratorService(
                db,
                new AgentPlanningService(new FixedPlanningModelProvider(plan)),
                toolService,
                null!,
                null!,
                new MemoryRetrievalService(db, embeddings),
                new MemoryWriteService(db, embeddings),
                new EmptyRepositoryContextEngine(),
                new NullAgentEventBroadcaster(),
                new AgentRunRegistry(),
                NullLogger<AgentOrchestratorService>.Instance);

            await service.RunAsync(sessionId);

            await using var verify = _fixture.CreateContext();
            Assert.Equal("Failed", await verify.AgentSessions.AsNoTracking()
                .Where(s => s.Id == sessionId).Select(s => s.State).SingleAsync());
            var statuses = await verify.AgentTaskNodes.AsNoTracking()
                .Where(t => t.AgentSessionId == sessionId)
                .ToDictionaryAsync(t => t.StepKey, t => t.Status);
            Assert.Equal("Failed", statuses["bad-path"]);
            Assert.Equal("Skipped", statuses["dependent"]);
            Assert.True(await verify.ToolExecutions.AnyAsync(e => e.OwnerUserId == ownerId &&
                e.RepositoryId != null && e.Decision == "Allowed" && e.Success == false));
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task Null_persisted_path_value_is_removed_before_tool_invocation()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "lap-null-task-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var db = _fixture.CreateContext();
            var (_, sessionId) = await AddSessionAsync(db, "AwaitingApproval", addTasks: false, repositoryPath: workspace);
            var repositoryId = await db.AgentSessions.Where(s => s.Id == sessionId)
                .Select(s => s.RepositoryId).SingleAsync();
            var task = new AgentTaskNode
            {
                AgentSessionId = sessionId,
                StepKey = "null-path",
                Type = "ToolCall",
                Description = "Task with null persisted path metadata",
                ToolName = "FileReadTool",
                ArgumentsJson = "{\"path\":null}",
                Status = "AwaitingApproval"
            };
            db.AgentTaskNodes.Add(task);
            await db.SaveChangesAsync();

            var embeddings = new EmptyEmbeddingProvider();
            var tools = new ToolExecutionService(
                new ITool[] { new FileReadTool() }, db, new CommandPermissionService(db),
                NullLogger<ToolExecutionService>.Instance, new WorkspaceRootPolicy(new[] { workspace }));
            var service = new AgentOrchestratorService(
                db, null!, tools, null!, null!,
                new MemoryRetrievalService(db, embeddings),
                new MemoryWriteService(db, embeddings),
                new EmptyRepositoryContextEngine(),
                new NullAgentEventBroadcaster(),
                new AgentRunRegistry(),
                NullLogger<AgentOrchestratorService>.Instance);

            await service.ApproveAndResumeAsync(sessionId, task.Id);

            await using var verify = _fixture.CreateContext();
            var storedTask = await verify.AgentTaskNodes.AsNoTracking().SingleAsync(t => t.Id == task.Id);
            Assert.Equal("Failed", storedTask.Status);
            Assert.Contains("Missing required parameter 'path'", storedTask.Error);
            var execution = await verify.ToolExecutions.AsNoTracking()
                .SingleAsync(e => e.RepositoryId == repositoryId);
            Assert.Equal("{}", execution.ArgumentsJson);
            Assert.False(execution.Success);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("[]")]
    public async Task Invalid_persisted_task_argument_json_fails_the_task_without_invoking_a_tool(string argumentsJson)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "lap-invalid-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var db = _fixture.CreateContext();
            var (_, sessionId) = await AddSessionAsync(db, "AwaitingApproval", addTasks: false, repositoryPath: workspace);
            var repositoryId = await db.AgentSessions.Where(s => s.Id == sessionId)
                .Select(s => s.RepositoryId).SingleAsync();
            var task = new AgentTaskNode
            {
                AgentSessionId = sessionId,
                StepKey = "invalid-json",
                Type = "ToolCall",
                Description = "Task with corrupt persisted arguments",
                ToolName = "FileReadTool",
                ArgumentsJson = argumentsJson,
                Status = "AwaitingApproval"
            };
            db.AgentTaskNodes.Add(task);
            await db.SaveChangesAsync();

            var embeddings = new EmptyEmbeddingProvider();
            var tools = new ToolExecutionService(
                new ITool[] { new FileReadTool() }, db, new CommandPermissionService(db),
                NullLogger<ToolExecutionService>.Instance, new WorkspaceRootPolicy(new[] { workspace }));
            var service = new AgentOrchestratorService(
                db, null!, tools, null!, null!,
                new MemoryRetrievalService(db, embeddings),
                new MemoryWriteService(db, embeddings),
                new EmptyRepositoryContextEngine(),
                new NullAgentEventBroadcaster(),
                new AgentRunRegistry(),
                NullLogger<AgentOrchestratorService>.Instance);

            await service.ApproveAndResumeAsync(sessionId, task.Id);

            await using var verify = _fixture.CreateContext();
            var storedTask = await verify.AgentTaskNodes.AsNoTracking().SingleAsync(t => t.Id == task.Id);
            Assert.Equal("Failed", storedTask.Status);
            Assert.Contains("Invalid persisted tool arguments", storedTask.Error);
            Assert.Equal("Failed", await verify.AgentSessions.AsNoTracking()
                .Where(s => s.Id == sessionId).Select(s => s.State).SingleAsync());
            Assert.False(await verify.ToolExecutions.AnyAsync(e => e.RepositoryId == repositoryId));
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_before_claim_does_not_mutate_a_session_owned_by_no_run()
    {
        Guid sessionId;
        await using (var setup = _fixture.CreateContext())
        {
            (_, sessionId) = await AddSessionAsync(setup, "Created", addTasks: false);
        }

        await using var db = _fixture.CreateContext();
        var registry = new AgentRunRegistry();
        var service = CreateService(db, registry: registry);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await service.RunAsync(sessionId, cancelled.Token);

        await using var verify = _fixture.CreateContext();
        var session = await verify.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal("Created", session.State);
        Assert.False(registry.IsRunning(sessionId));
        Assert.Empty(await verify.MemoryEntries.Where(m => m.SourceAgentSessionId == sessionId).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_workers_cannot_both_claim_the_same_created_session()
    {
        Guid sessionId;
        await using (var setup = _fixture.CreateContext())
        {
            (_, sessionId) = await AddSessionAsync(setup, "Created", addTasks: false);
        }

        var updateInterceptor = new AgentSessionUpdateInterceptor();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(updateInterceptor)
            .Options;
        await using var firstDb = new PlatformDbContext(options);
        await using var secondDb = new PlatformDbContext(options);
        await using var lockDb = _fixture.CreateContext();
        await using var rowLock = await lockDb.Database.BeginTransactionAsync();
        await LockSessionRowAsync(lockDb, sessionId);

        var contextGate = new BlockingRepositoryContextEngine();
        var firstService = CreateService(firstDb, contextGate);
        var secondService = CreateService(secondDb, contextGate);
        Task firstRun = Task.CompletedTask;
        Task secondRun = Task.CompletedTask;
        try
        {
            firstRun = firstService.RunAsync(sessionId);
            secondRun = secondService.RunAsync(sessionId);

            // Holding a PostgreSQL row lock keeps both conditional UPDATEs pending while
            // their preceding SELECTs still observe Created. Releasing it forces the
            // database to resolve the true claim race, rather than merely testing the
            // orchestrator's per-process duplicate registry.
            await updateInterceptor.BothSessionUpdatesStarted.WaitAsync(TimeSpan.FromSeconds(30));
            await rowLock.CommitAsync();

            await contextGate.Entered.WaitAsync(TimeSpan.FromSeconds(30));
            var firstCompleted = await Task.WhenAny(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(30));
            await firstCompleted.WaitAsync(TimeSpan.FromSeconds(30));
            var stillRunning = ReferenceEquals(firstCompleted, firstRun) ? secondRun : firstRun;
            Assert.False(stillRunning.IsCompleted);
            Assert.Equal(1, contextGate.CallCount);

            await using var during = _fixture.CreateContext();
            Assert.Equal("Planning", await during.AgentSessions.AsNoTracking()
                .Where(s => s.Id == sessionId).Select(s => s.State).SingleAsync());
        }
        finally
        {
            contextGate.Release();
            try { await rowLock.RollbackAsync(); } catch { /* The transaction may already be committed. */ }
            try { await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { /* Preserve the original assertion/timeout failure while cleaning up. */ }
        }

        Assert.Equal(1, contextGate.CallCount);
        await using var final = _fixture.CreateContext();
        Assert.Equal("Failed", await final.AgentSessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.State).SingleAsync());
    }

    private async Task<(Guid OwnerId, Guid SessionId)> AddSessionAsync(
        PlatformDbContext db, string state, bool addTasks, int maxRetries = 3, string? repositoryPath = null)
    {
        var ownerId = Guid.NewGuid();
        var project = new Project { Name = $"AgentConcurrency-{Guid.NewGuid()}", OwnerUserId = ownerId };
        var repository = new Repository { Project = project, LocalPath = repositoryPath ?? Path.GetTempPath() };
        var session = new AgentSession
        {
            OwnerUserId = ownerId,
            ProjectId = project.Id,
            RepositoryId = repository.Id,
            UserRequest = "Exercise an agent session concurrency edge case.",
            State = state,
            ModelIdUsed = "test-model",
            MaxRetries = maxRetries
        };
        db.Projects.Add(project);
        db.Repositories.Add(repository);
        db.AgentSessions.Add(session);

        if (addTasks)
        {
            db.AgentTaskNodes.AddRange(
                CreateTask(session.Id, "executing", "Executing"),
                CreateTask(session.Id, "pending", "Pending"),
                CreateTask(session.Id, "approval", "AwaitingApproval"),
                CreateTask(session.Id, "completed", "Completed"));
        }

        await db.SaveChangesAsync();
        return (ownerId, session.Id);
    }

    private static AgentTaskNode CreateTask(Guid sessionId, string key, string status) => new()
    {
        AgentSessionId = sessionId,
        StepKey = key,
        Type = "ToolCall",
        Description = $"Test task {key}",
        Status = status
    };

    private static AgentOrchestratorService CreateService(
        PlatformDbContext db,
        IRepositoryContextEngine? contextEngine = null,
        AgentRunRegistry? registry = null)
    {
        var embeddings = new EmptyEmbeddingProvider();
        var tools = new ToolExecutionService(
            Array.Empty<ITool>(), db, new CommandPermissionService(db),
            NullLogger<ToolExecutionService>.Instance, new WorkspaceRootPolicy(Array.Empty<string>()));
        return new AgentOrchestratorService(
            db,
            null!, // In the claim test this fails only after the winner is released from the context gate.
            tools,
            null!,
            null!,
            new MemoryRetrievalService(db, embeddings),
            new MemoryWriteService(db, embeddings),
            contextEngine!,
            new NullAgentEventBroadcaster(),
            registry ?? new AgentRunRegistry(),
            NullLogger<AgentOrchestratorService>.Instance);
    }

    private static async Task LockSessionRowAsync(PlatformDbContext db, Guid sessionId)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT \"Id\" FROM \"AgentSessions\" WHERE \"Id\" = @sessionId FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "sessionId";
        parameter.Value = sessionId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
    }

    private sealed class EmptyRepositoryContextEngine : IRepositoryContextEngine
    {
        public Task<IReadOnlyList<RepositoryContextSnippet>> BuildContextAsync(
            Guid repositoryId, string query, int maxFiles = 6, int maxChars = 12000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepositoryContextSnippet>>(Array.Empty<RepositoryContextSnippet>());
    }

    private sealed class FixedPlanningModelProvider : IModelProvider
    {
        private readonly string _response;
        public FixedPlanningModelProvider(string response) => _response = response;
        public string ProviderId => "fixed-test-model";
        public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModelDescriptor>>(Array.Empty<ModelDescriptor>());
        public Task<ModelLoadResult> LoadModelAsync(string modelId, CancellationToken ct = default) =>
            Task.FromResult(new ModelLoadResult(true, null, TimeSpan.Zero));
        public Task UnloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ModelGenerationResult> GenerateAsync(ModelGenerationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ModelGenerationResult(_response, 1, 1, TimeSpan.Zero, null, request.ModelId, false));
        public async IAsyncEnumerable<ModelStreamChunk> GenerateStreamAsync(
            ModelGenerationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ModelStreamChunk(_response, true, 1);
        }
        public Task<int> CountTokensAsync(string modelId, string text, CancellationToken ct = default) =>
            Task.FromResult(text.Length);
        public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct = default) =>
            Task.FromResult(new ModelProviderHealth(true, "test provider"));
    }

    private sealed class EmptyEmbeddingProvider : IEmbeddingProvider
    {
        public string ProviderId => "test";
        public string DefaultEmbeddingModelId => "test-embedding";
        public Task<float[]> EmbedAsync(string text, string? modelId = null, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<float>());
    }

    private sealed class BlockingRepositoryContextEngine : IRepositoryContextEngine
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public Task Entered => _entered.Task;
        public int CallCount => Volatile.Read(ref _callCount);
        public void Release() => _release.TrySetResult(true);

        public async Task<IReadOnlyList<RepositoryContextSnippet>> BuildContextAsync(
            Guid repositoryId, string query, int maxFiles = 6, int maxChars = 12000, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(ct);
            return Array.Empty<RepositoryContextSnippet>();
        }
    }

    private sealed class AgentSessionUpdateInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> _bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task BothSessionUpdatesStarted => _bothStarted.Task;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("\"AgentSessions\"", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _count) == 2) _bothStarted.TrySetResult(true);
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
