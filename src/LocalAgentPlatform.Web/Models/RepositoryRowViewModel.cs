namespace LocalAgentPlatform.Web.Models;

public class RepositoryRowViewModel
{
    public Guid Id { get; set; }
    public string ProjectName { get; set; } = default!;
    public string LocalPath { get; set; } = default!;
    public DateTimeOffset? LastIndexedAtUtc { get; set; }
    public string? LatestJobStatus { get; set; }
    public int FileCount { get; set; }
    public int SymbolCount { get; set; }
}
