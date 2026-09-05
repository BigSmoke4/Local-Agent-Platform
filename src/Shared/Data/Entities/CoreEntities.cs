namespace LocalAgentPlatform.Shared.Data.Entities;

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserName { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = default!;

    /// <summary>SHA-256 hash of the actual key — the raw key is shown to the user
    /// exactly once at creation time and never stored or logged in plaintext.</summary>
    public string KeyHash { get; set; } = default!;
    public string KeyPrefix { get; set; } = default!; // first 8 chars, shown in UI for identification
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = default!;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<Repository> Repositories { get; set; } = new();
}

public class Repository
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string LocalPath { get; set; } = default!;
    public string? DefaultBranch { get; set; }
    public DateTimeOffset? LastIndexedAtUtc { get; set; }
}

public class RegisteredModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ModelId { get; set; } = default!; // provider-native id, e.g. Ollama tag
    public string ProviderId { get; set; } = default!; // "ollama", "llama.cpp", ...
    public string Name { get; set; } = default!;
    public string? Quantization { get; set; }
    public int? ContextWindow { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset RegisteredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class AgentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid RepositoryId { get; set; }
    public string UserRequest { get; set; } = default!;
    public string State { get; set; } = "Created"; // Created, Understanding, Planning, Executing, AwaitingApproval, Verifying, Repairing, Completed, Failed, Cancelled
    public string? ModelIdUsed { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? FailureReason { get; set; }

    public string? PlanJson { get; set; }
    public string? FinalSummary { get; set; }

    // Resource budgets (Section 47) — enforced by AgentOrchestratorService, never just decorative.
    public int MaxIterations { get; set; } = 20;
    public int MaxRetries { get; set; } = 3;
    public int MaxDurationMinutes { get; set; } = 15;
    public int IterationCount { get; set; }

    // Phase 6: repair-loop budget, distinct from per-task retries above.
    public int MaxRepairAttempts { get; set; } = 2;
    public int RepairAttemptCount { get; set; }

    public List<TokenUsageRecord> TokenUsages { get; set; } = new();
    public List<AgentTaskNode> Tasks { get; set; } = new();
}

public class AgentTaskNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentSessionId { get; set; }
    public AgentSession? AgentSession { get; set; }

    public int OrderIndex { get; set; }
    public Guid? ParentId { get; set; } // linear chain for now — see docs/STATUS.md

    public string Type { get; set; } = default!; // ToolCall, Reasoning
    public string Description { get; set; } = default!;
    public string? ToolName { get; set; }
    public string? ArgumentsJson { get; set; }

    public string Status { get; set; } = "Pending"; // Pending, Executing, AwaitingApproval, Completed, Failed, Skipped
    public string? Output { get; set; }
    public string? Error { get; set; }
    public int RetryCount { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

// ---- Phase 6: Verification Engine entities (spec Section 15/16/21) ----

public class VerificationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentSessionId { get; set; }
    public int RepairAttemptNumber { get; set; } // 0 = first pass, 1+ = after a repair attempt

    public bool? BuildPassed { get; set; }
    public string? BuildOutputSummary { get; set; }
    public int? CompilerErrorCount { get; set; }
    public int? CompilerWarningCount { get; set; }

    public bool? TestsRan { get; set; }
    public bool? TestsPassed { get; set; }
    public int? TestsTotal { get; set; }
    public int? TestsFailed { get; set; }
    public int? TestsSkipped { get; set; }
    public string? TestOutputSummary { get; set; }

    public int SecurityFindingCount { get; set; }
    public string? SecurityFindingsJson { get; set; }

    /// <summary>Approved or Rejected — the self-critic reviewer's advisory verdict.
    /// This NEVER overrides an actual build/test failure; it's an additional gate on
    /// top of real verification, not a replacement for it (spec Section 65).</summary>
    public string? ReviewerVerdict { get; set; }
    public string? ReviewerReason { get; set; }

    public string OverallResult { get; set; } = default!; // Passed, Failed
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

// ---- Phase 7: Memory system entities (spec Section 14) ----

public class MemoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ProjectId { get; set; }
    public Guid? RepositoryId { get; set; }

    /// <summary>ShortTerm, Working, LongTerm, UserPreference, Execution.</summary>
    public string Scope { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string Content { get; set; } = default!;
    public string? Tags { get; set; } // comma-separated

    /// <summary>User- or system-assigned base weight (0.0–1.0) mixed into retrieval
    /// ranking alongside real keyword-overlap and recency — lets a user pin an
    /// architecture decision as more important than a stale note without deleting it.</summary>
    public double BaseImportance { get; set; } = 0.5;

    public Guid? SourceAgentSessionId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAccessedAtUtc { get; set; }
    public int AccessCount { get; set; }
}

public class TokenUsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentSessionId { get; set; }
    public AgentSession? AgentSession { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int ContextWindowMax { get; set; }
    public double TokensPerSecond { get; set; }
    public TimeSpan? TimeToFirstToken { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class AuditLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Action { get; set; } = default!;
    public string? Detail { get; set; }
    public string? ModelId { get; set; }
    public string? ToolName { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

// ---- Phase 3: Repository Intelligence entities (spec Section 12, 21, 60) ----

public class FileSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public Repository? Repository { get; set; }

    public string RelativePath { get; set; } = default!;
    public string ContentHash { get; set; } = default!; // SHA-256 of file bytes
    public string? Language { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset LastIndexedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDeleted { get; set; } // soft-marked when the file no longer exists on disk

    public List<CodeSymbol> Symbols { get; set; } = new();
}

public class CodeSymbol
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public Guid FileSnapshotId { get; set; }
    public FileSnapshot? FileSnapshot { get; set; }

    public string Name { get; set; } = default!;
    public string Kind { get; set; } = default!; // Class, Interface, Method, Property, Enum, etc.
    public string? ContainingNamespace { get; set; }
    public string? ContainingTypeName { get; set; }
    public int LineNumber { get; set; }
    public string? Signature { get; set; }
}

public class CodeRelationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }

    public Guid FromSymbolId { get; set; }
    public Guid ToSymbolId { get; set; }
    public string RelationshipType { get; set; } = default!; // References, Inherits, Implements, Calls
}

public class RepositoryIndexingJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }

    public string Status { get; set; } = "Queued"; // Queued, Scanning, Completed, Failed
    public int FilesScanned { get; set; }
    public int FilesChanged { get; set; }
    public int FilesDeleted { get; set; }
    public int SymbolsExtracted { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTimeOffset QueuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

// ---- Phase 4: Tool Execution Engine entities (spec Section 10, 11, 21) ----

public class ToolDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string RiskLevel { get; set; } = default!; // Safe, Moderate, Dangerous
    public bool RequiresApproval { get; set; }
    public int DefaultTimeoutSeconds { get; set; }
    public DateTimeOffset RegisteredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class ToolExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ToolName { get; set; } = default!;
    public Guid? RepositoryId { get; set; }
    public string WorkspaceRootPath { get; set; } = default!;
    public string ArgumentsJson { get; set; } = default!;

    /// <summary>Allowed, Denied, PendingApproval — the CommandPolicyEngine's verdict before execution.</summary>
    public string Decision { get; set; } = default!;
    public string? DecisionReason { get; set; }

    public bool? Success { get; set; } // null if never actually ran (e.g. Denied)
    public string? Output { get; set; }
    public string? Error { get; set; }
    public int? ExitCode { get; set; }

    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

/// <summary>Persistent per-executable Allow/Deny scoping (spec Section 11: "Approve /
/// Reject / Always Allow / Always Deny"). Checked by ToolExecutionService before
/// falling back to CommandPolicyEngine's static, in-code policy.</summary>
public class CommandPermissionRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public string ExecutableName { get; set; } = default!; // e.g. "npm", extracted the same way CommandPolicyEngine does
    public string Decision { get; set; } = default!; // AlwaysAllow, AlwaysDeny
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
