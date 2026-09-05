namespace LocalAgentPlatform.Web.Models.Api;

public sealed record RegisteredModelDto(Guid Id, string ProviderId, string ModelId, string Name, string? Quantization, int? ContextWindow, bool IsDefault);

public sealed record AgentSessionDto(
    Guid Id, string UserRequest, string State, string? ModelIdUsed,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc,
    int IterationCount, int MaxIterations, int RepairAttemptCount, int MaxRepairAttempts,
    string? FailureReason, string? FinalSummary);

public sealed record AgentTaskDto(
    Guid Id, int OrderIndex, string Type, string Description, string? ToolName,
    string Status, string? Output, string? Error, int RetryCount);

public sealed record StartAgentSessionRequest(Guid RepositoryId, string ModelId, string UserRequest);

public sealed record ApproveTaskRequest(Guid TaskId);

public sealed record HardwareTelemetryDto(
    DateTimeOffset TimestampUtc, double? CpuUtilizationPercent,
    long? RamUsedBytes, long? RamTotalBytes, double? GpuUtilizationPercent,
    long? GpuVramUsedBytes, long? GpuVramTotalBytes);

public sealed record TokenUsageSummaryDto(int TotalInputTokens, int TotalOutputTokens, int RecordCount);
