using System.ComponentModel.DataAnnotations;

namespace Platform.Web.Models;

public enum AgentSessionState
{
    Created,
    Understanding,
    Planning,
    Executing,
    Observing,
    Verifying,
    Repairing,
    Completed,
    Failed,
    Cancelled
}

public class AgentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserRequest { get; set; } = string.Empty;

    public AgentSessionState State { get; set; } = AgentSessionState.Created;

    public Guid? ModelId { get; set; }
    public ModelDescriptor? Model { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedTokens { get; set; }

    public string? FinalResult { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public List<ToolExecution> ToolExecutions { get; set; } = new();
}

public class ToolExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentSessionId { get; set; }
    public AgentSession? AgentSession { get; set; }

    [Required, MaxLength(200)]
    public string ToolName { get; set; } = string.Empty;

    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }

    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}
