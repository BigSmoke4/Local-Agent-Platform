using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;
using Testcontainers.PostgreSql;
using Xunit;

namespace Platform.Tests.Integration;

/// <summary>
/// Real integration tests against an actual PostgreSQL instance via
/// Testcontainers — not EF Core's InMemory provider (see docs/TESTING.md
/// for why that gap mattered: InMemory doesn't validate real Npgsql
/// behavior, index constraints, or provider-specific SQL generation).
///
/// Honest environment note: this requires Docker running on the machine
/// that executes `dotnet test`. It was NOT run in the sandbox that
/// generated this code (no Docker access there — see the top-level
/// README's explanation of that constraint). `InitializeAsync` will throw
/// a clear Docker-connectivity error if Docker isn't available, rather
/// than silently skipping and reporting green — a skipped safety net that
/// looks like a passing one is worse than a visible failure.
/// </summary>
public class PostgresIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private PlatformDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("platform_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        _db = new PlatformDbContext(options);
        await _db.Database.MigrateAsync(); // real migration run against a real Postgres instance
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task ModelDescriptor_RuntimeIdUniqueConstraint_IsEnforcedByRealPostgres()
    {
        _db.Models.Add(new ModelDescriptor { Name = "A", RuntimeId = "same-id" });
        await _db.SaveChangesAsync();

        _db.Models.Add(new ModelDescriptor { Name = "B", RuntimeId = "same-id" });

        // This is exactly the kind of thing EF Core's InMemory provider does
        // NOT catch (it doesn't enforce unique indexes) — a real assertion
        // that only a real database backend can validate.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task AgentSession_CascadeDeletesToolExecutions_OnRealDatabase()
    {
        var session = new AgentSession { UserRequest = "test" };
        session.ToolExecutions.Add(new ToolExecution { ToolName = "CalculatorTool", Succeeded = true });
        _db.AgentSessions.Add(session);
        await _db.SaveChangesAsync();

        _db.AgentSessions.Remove(session);
        await _db.SaveChangesAsync();

        var orphaned = await _db.ToolExecutions.Where(t => t.AgentSessionId == session.Id).ToListAsync();
        Assert.Empty(orphaned); // real cascade-delete behavior, not assumed
    }

    [Fact]
    public async Task AutonomySetting_UserIdUniqueConstraint_IsEnforcedByRealPostgres()
    {
        _db.AutonomySettings.Add(new AutonomySetting { UserId = "user-1", Level = AutonomyLevel.Low });
        await _db.SaveChangesAsync();

        _db.AutonomySettings.Add(new AutonomySetting { UserId = "user-1", Level = AutonomyLevel.High });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }
}
