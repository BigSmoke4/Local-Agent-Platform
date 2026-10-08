using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LocalAgentPlatform.Shared.Data;

/// <summary>
/// Creates the context for EF design-time operations without booting the web host,
/// connecting to PostgreSQL, running migrations, or seeding application data.
/// </summary>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    private const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=local_agent_platform;Username=postgres;Password=postgres";

    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PlatformDb")
            ?? FallbackConnectionString;
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
            .Options;
        return new PlatformDbContext(options);
    }
}
