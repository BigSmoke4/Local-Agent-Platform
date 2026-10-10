using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

public sealed class EnsureCreatedAdoptionFixtureFactAttribute : FactAttribute
{
    public EnsureCreatedAdoptionFixtureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LAP_RUN_ENSURECREATED_ADOPTION_FIXTURE") != "1")
            Skip = "This destructive-schema fixture is opt-in and must target a fresh disposable PostgreSQL database.";
    }
}

/// <summary>
/// Creates the exact kind of legacy database that the baseline-adoption helper is
/// designed for: EF EnsureCreated schema, with no migration-history table. Run only
/// against a fresh, disposable database by setting LAP_RUN_ENSURECREATED_ADOPTION_FIXTURE=1.
/// </summary>
public sealed class EnsureCreatedAdoptionFixtureTests
{
    [EnsureCreatedAdoptionFixtureFact]
    public async Task EnsureCreatedCreatesSchemaWithoutEfMigrationHistory()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PlatformDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings__PlatformDb must point to an empty disposable database.");

        // The adoption helper requires exclusive access; avoid retaining an idle pooled
        // backend after the fixture context closes on the test host.
        var fixtureConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(fixtureConnectionString)
            .Options;
        await using var db = new PlatformDbContext(options);

        Assert.True(await db.Database.EnsureCreatedAsync(), "The adoption fixture database must start empty.");
        db.Projects.Add(new Project
        {
            OwnerUserId = Guid.NewGuid(),
            Name = "ensurecreated-adoption-sentinel"
        });
        await db.SaveChangesAsync();

        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NULL;";
        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false), "EnsureCreated must not create EF migration history.");

        command.CommandText = "SELECT to_regclass('public.\"Projects\"') IS NOT NULL;";
        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false), "The fixture must contain the platform's real EnsureCreated schema.");
    }
}
