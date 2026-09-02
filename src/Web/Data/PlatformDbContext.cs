using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Platform.Web.Models;

namespace Platform.Web.Data;

public class PlatformDbContext : IdentityDbContext<ApplicationUser>
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }

    public DbSet<ModelDescriptor> Models => Set<ModelDescriptor>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<ToolExecution> ToolExecutions => Set<ToolExecution>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AgentTaskNode> AgentTaskNodes => Set<AgentTaskNode>();
    public DbSet<FileSnapshot> FileSnapshots => Set<FileSnapshot>();
    public DbSet<CodeSymbol> CodeSymbols => Set<CodeSymbol>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<AutonomySetting> AutonomySettings => Set<AutonomySetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ModelDescriptor>(e =>
        {
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.RuntimeId).IsUnique();
        });

        builder.Entity<AgentSession>(e =>
        {
            e.HasOne(x => x.Model)
                .WithMany()
                .HasForeignKey(x => x.ModelId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasMany(x => x.ToolExecutions)
                .WithOne(x => x.AgentSession)
                .HasForeignKey(x => x.AgentSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.State);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        builder.Entity<ToolExecution>(e =>
        {
            e.HasIndex(x => x.ToolName);
        });

        builder.Entity<AgentTaskNode>(e =>
        {
            e.HasOne(x => x.AgentSession)
                .WithMany()
                .HasForeignKey(x => x.AgentSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.AgentSessionId);
            e.HasIndex(x => x.Status);
        });

        builder.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.OccurredAtUtc);
        });

        builder.Entity<FileSnapshot>(e =>
        {
            e.HasIndex(x => x.RelativePath);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        builder.Entity<CodeSymbol>(e =>
        {
            e.HasIndex(x => x.SymbolName);
            e.HasIndex(x => x.FilePath);
        });

        builder.Entity<Memory>(e =>
        {
            e.HasIndex(x => x.Type);
            e.HasIndex(x => x.AgentSessionId);
        });

        builder.Entity<AutonomySetting>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
        });
    }
}
