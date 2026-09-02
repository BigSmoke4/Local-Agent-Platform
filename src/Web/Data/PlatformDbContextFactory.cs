using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Platform.Web.Data;

/// <summary>
/// Enables `dotnet ef migrations add` / `dotnet ef database update` to work
/// without running the full host.
/// </summary>
public class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var connectionString = config.GetConnectionString("Default")
            ?? "Host=localhost;Port=5432;Database=local_agent_platform;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<PlatformDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new PlatformDbContext(optionsBuilder.Options);
    }
}
