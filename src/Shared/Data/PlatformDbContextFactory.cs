using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LocalAgentPlatform.Shared.Data;

/// <summary>
/// Creates the context for EF design-time operations without booting the web host,
/// connecting to PostgreSQL, running migrations, or seeding application data.
/// </summary>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PlatformDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Set ConnectionStrings__PlatformDb explicitly before running EF design-time commands; no database credentials are bundled as a fallback.");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
            .Options;
        return new PlatformDbContext(options);
    }
}
