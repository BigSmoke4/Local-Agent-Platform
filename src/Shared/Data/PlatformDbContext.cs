using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Shared.Data;

public class PlatformDbContext : DbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<RegisteredModel> RegisteredModels => Set<RegisteredModel>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<TokenUsageRecord> TokenUsageRecords => Set<TokenUsageRecord>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<FileSnapshot> FileSnapshots => Set<FileSnapshot>();
    public DbSet<CodeSymbol> CodeSymbols => Set<CodeSymbol>();
    public DbSet<CodeRelationship> CodeRelationships => Set<CodeRelationship>();
    public DbSet<RepositoryIndexingJob> RepositoryIndexingJobs => Set<RepositoryIndexingJob>();
    public DbSet<ToolDefinition> ToolDefinitions => Set<ToolDefinition>();
    public DbSet<ToolExecution> ToolExecutions => Set<ToolExecution>();
    public DbSet<CommandPermissionRule> CommandPermissionRules => Set<CommandPermissionRule>();
    public DbSet<AgentTaskNode> AgentTaskNodes => Set<AgentTaskNode>();
    public DbSet<VerificationRun> VerificationRuns => Set<VerificationRun>();
    public DbSet<MemoryEntry> MemoryEntries => Set<MemoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasIndex(x => x.UserName).IsUnique();
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasIndex(x => x.KeyHash).IsUnique();
        });

        modelBuilder.Entity<Project>(e =>
        {
            e.HasIndex(x => new { x.OwnerUserId, x.Name }).IsUnique();
            e.HasMany(x => x.Repositories)
                .WithOne(x => x.Project)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Repository>(e =>
        {
            e.HasIndex(x => x.LocalPath);
        });

        modelBuilder.Entity<RegisteredModel>(e =>
        {
            e.HasIndex(x => new { x.ProviderId, x.ModelId }).IsUnique();
        });

        modelBuilder.Entity<AgentSession>(e =>
        {
            e.HasIndex(x => x.State);
            e.HasMany(x => x.TokenUsages)
                .WithOne(x => x.AgentSession)
                .HasForeignKey(x => x.AgentSessionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tasks)
                .WithOne(x => x.AgentSession)
                .HasForeignKey(x => x.AgentSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.HasIndex(x => x.OccurredAtUtc);
        });

        modelBuilder.Entity<FileSnapshot>(e =>
        {
            e.HasIndex(x => new { x.RepositoryId, x.RelativePath }).IsUnique();
            e.HasIndex(x => x.ContentHash);
            e.HasMany(x => x.Symbols)
                .WithOne(x => x.FileSnapshot)
                .HasForeignKey(x => x.FileSnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CodeSymbol>(e =>
        {
            e.HasIndex(x => new { x.RepositoryId, x.Name });
        });

        modelBuilder.Entity<CodeRelationship>(e =>
        {
            e.HasIndex(x => x.FromSymbolId);
            e.HasIndex(x => x.ToSymbolId);
        });

        modelBuilder.Entity<RepositoryIndexingJob>(e =>
        {
            e.HasIndex(x => new { x.RepositoryId, x.QueuedAtUtc });
        });

        modelBuilder.Entity<ToolDefinition>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ToolExecution>(e =>
        {
            e.HasIndex(x => x.ToolName);
            e.HasIndex(x => x.RequestedAtUtc);
        });

        modelBuilder.Entity<CommandPermissionRule>(e =>
        {
            e.HasIndex(x => new { x.OwnerUserId, x.ExecutableName }).IsUnique();
        });

        modelBuilder.Entity<AgentTaskNode>(e =>
        {
            e.HasIndex(x => new { x.AgentSessionId, x.OrderIndex });
        });

        modelBuilder.Entity<VerificationRun>(e =>
        {
            e.HasIndex(x => new { x.AgentSessionId, x.RepairAttemptNumber });
        });

        modelBuilder.Entity<MemoryEntry>(e =>
        {
            e.HasIndex(x => new { x.RepositoryId, x.Scope });
            e.HasIndex(x => x.CreatedAtUtc);
        });
    }
}
