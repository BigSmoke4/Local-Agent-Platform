namespace Platform.Web.Models;

public class FileSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? AgentSessionId { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Full content stored so a rollback can restore it. For a real product
    /// at scale this would move to content-addressed blob storage instead of
    /// inline text, but for Phase 1 this keeps rollback genuinely functional
    /// rather than a stub that claims to work.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
