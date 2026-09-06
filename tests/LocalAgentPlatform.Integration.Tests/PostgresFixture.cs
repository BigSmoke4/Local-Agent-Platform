using LocalAgentPlatform.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Integration.Tests;

/// <summary>
/// Provides a real PlatformDbContext backed by an actual PostgreSQL instance. The
/// connection string comes from the ConnectionStrings__PlatformDb environment
/// variable (matching ASP.NET Core's standard env-var config binding), falling back
/// to a sensible local default. These tests are genuine integration tests — if
/// Postgres isn't reachable, they fail with a real connection error rather than a
/// fake pass. CI provides a real `postgres:16` service container (see
/// .github/workflows/ci.yml); for local runs, `docker compose up -d postgres` first.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public PlatformDbContext CreateContext()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PlatformDb")
            ?? "Host=localhost;Port=5432;Database=local_agent_platform_test;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PlatformDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        // EnsureCreated (not Migrate) here deliberately: these tests care about
        // exercising real application/infrastructure code against a real database,
        // not about verifying the migration history itself.
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
