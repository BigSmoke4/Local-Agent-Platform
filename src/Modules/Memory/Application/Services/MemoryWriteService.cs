using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Modules.Memory.Application.Services;

public sealed class MemoryWriteService
{
    private readonly PlatformDbContext _db;

    public MemoryWriteService(PlatformDbContext db) => _db = db;

    public async Task<MemoryEntry> AddManualAsync(
        string scope, string title, string content, string? tags,
        Guid? repositoryId, Guid? projectId, double baseImportance, CancellationToken ct = default)
    {
        var entry = new MemoryEntry
        {
            Scope = scope,
            Title = title,
            Content = content,
            Tags = tags,
            RepositoryId = repositoryId,
            ProjectId = projectId,
            BaseImportance = Math.Clamp(baseImportance, 0.0, 1.0)
        };
        _db.MemoryEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// Called by the Agent orchestrator at every terminal state (Completed/Failed/
    /// Cancelled). Writes a real "Execution memory: previous attempts and failures"
    /// entry (spec Section 14) built from the session's actual final summary and
    /// failure reason — never a synthesized or hypothetical account. This is what lets
    /// a later session on the same repository retrieve "we already tried X and it
    /// failed because Y" instead of repeating the same mistake blind.
    /// </summary>
    public async Task RecordSessionOutcomeAsync(AgentSession session, CancellationToken ct = default)
    {
        var title = $"{session.State}: {Truncate(session.UserRequest, 80)}";
        var content = session.State switch
        {
            "Completed" => $"Request succeeded. {session.FinalSummary}",
            "Cancelled" => $"Request was cancelled by the user. Request was: {session.UserRequest}",
            _ => $"Request failed. Reason: {session.FailureReason}\n{session.FinalSummary}"
        };

        // Failures get a higher base importance so future retrieval surfaces "this was
        // already tried and didn't work" ahead of routine successful-run notes.
        var importance = session.State == "Failed" ? 0.6 : 0.4;

        var exists = await _db.MemoryEntries.AnyAsync(m => m.SourceAgentSessionId == session.Id, ct);
        if (exists) return; // idempotent — don't double-record if called more than once for the same session

        _db.MemoryEntries.Add(new MemoryEntry
        {
            Scope = "Execution",
            Title = title,
            Content = Truncate(content, 4000)!,
            RepositoryId = session.RepositoryId,
            ProjectId = session.ProjectId,
            BaseImportance = importance,
            SourceAgentSessionId = session.Id
        });
        await _db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] + "... [truncated]" : s;
}
