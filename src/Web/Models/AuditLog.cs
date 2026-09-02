namespace Platform.Web.Models;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
