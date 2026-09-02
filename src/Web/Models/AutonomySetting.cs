namespace Platform.Web.Models;

public enum AutonomyLevel
{
    Low,    // Agent asks before any file modification
    Medium, // Agent modifies files but asks before dangerous/unapproved commands
    High    // Agent may perform allowlisted or pre-approved operations automatically
}

public class AutonomySetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public AutonomyLevel Level { get; set; } = AutonomyLevel.Low;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
