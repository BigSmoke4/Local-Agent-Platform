using System.Text.Json;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Models;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Modules.Memory.Application.Services;

public sealed class MemoryWriteService
{
    private readonly PlatformDbContext _db;
    private readonly IEmbeddingProvider _embeddings;
    public MemoryWriteService(PlatformDbContext db, IEmbeddingProvider embeddings) { _db = db; _embeddings = embeddings; }

    public async Task<MemoryEntry> AddManualAsync(string scope, string title, string content, string? tags,
        Guid? repositoryId, Guid? projectId, double baseImportance, CancellationToken ct = default, Guid? ownerUserId = null)
    {
        var entry = new MemoryEntry { OwnerUserId = ownerUserId ?? Guid.Empty, Scope = scope, Title = title, Content = content, Tags = tags,
            RepositoryId = repositoryId, ProjectId = projectId, BaseImportance = Math.Clamp(baseImportance, 0.0, 1.0) };
        await AttachEmbeddingBestEffortAsync(entry, ct);
        _db.MemoryEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task RecordSessionOutcomeAsync(AgentSession session, CancellationToken ct = default)
    {
        if (await _db.MemoryEntries.AnyAsync(m => m.SourceAgentSessionId == session.Id, ct)) return;
        var entry = new MemoryEntry
        {
            OwnerUserId = session.OwnerUserId,
            Scope = "Execution",
            Title = $"{session.State}: {Truncate(session.UserRequest, 80)}",
            Content = Truncate(session.State switch
            {
                "Completed" => $"Request succeeded. {session.FinalSummary}",
                "Cancelled" => $"Request was cancelled by the user. Request was: {session.UserRequest}",
                _ => $"Request failed. Reason: {session.FailureReason}\n{session.FinalSummary}"
            }, 4000)!,
            RepositoryId = session.RepositoryId, ProjectId = session.ProjectId,
            BaseImportance = session.State == "Failed" ? 0.6 : 0.4, SourceAgentSessionId = session.Id
        };
        await AttachEmbeddingBestEffortAsync(entry, ct);
        _db.MemoryEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
    }

    private async Task AttachEmbeddingBestEffortAsync(MemoryEntry entry, CancellationToken ct)
    {
        try
        {
            var vector = await _embeddings.EmbedAsync($"{entry.Title}\n{entry.Content}", ct: ct);
            entry.EmbeddingJson = JsonSerializer.Serialize(vector);
            entry.EmbeddingModelId = _embeddings.DefaultEmbeddingModelId;
        }
        catch { /* Memory must remain usable when the optional embedding model is not installed. */ }
    }

    private static string? Truncate(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] + "... [truncated]" : s;
}
