namespace Platform.Web.Models;

public class DashboardViewModel
{
    public ModelDescriptor? DefaultModel { get; set; }
    public bool ModelRuntimeHealthy { get; set; }
    public string? ModelRuntimeMessage { get; set; }
    public IReadOnlyList<string> AvailableRuntimeModels { get; set; } = Array.Empty<string>();

    public int TotalAgentSessions { get; set; }
    public int ActiveAgentSessions { get; set; }
    public AgentSession? LatestSession { get; set; }

    public int TotalToolExecutions { get; set; }
    public int FailedToolExecutions { get; set; }

    public IReadOnlyList<AuditLog> RecentAuditLogs { get; set; } = Array.Empty<AuditLog>();
}
