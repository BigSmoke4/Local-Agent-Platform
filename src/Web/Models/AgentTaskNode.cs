namespace Platform.Web.Models;

public enum TaskNodeStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

public class AgentTaskNode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentSessionId { get; set; }
    public AgentSession? AgentSession { get; set; }

    public Guid? ParentId { get; set; }

    public int SequenceOrder { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public TaskNodeStatus Status { get; set; } = TaskNodeStatus.Pending;

    public int RetryCount { get; set; }

    public string? Error { get; set; }

    public string? OutputSummary { get; set; }

    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}
