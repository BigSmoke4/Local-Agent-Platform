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
        Guid? repositoryId, Guid? projectId, double baseImportance, Guid ownerUserId, CancellationToken ct = default)
    {
        var allowedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LongTerm", "UserPreference", "Working" };
        if (!allowedScopes.Contains(scope) || string.IsNullOrWhiteSpace(title) || title.Length > 160 ||
            string.IsNullOrWhiteSpace(content) || content.Length > 4_000 || tags?.Length > 500 ||
            !double.IsFinite(baseImportance))
            throw new ArgumentException("Memory scope or content is invalid or exceeds its size limit.");

        if (repositoryId is { } repoId)
        {
            var repository = await _db.Repositories
                .Where(r => r.Id == repoId && r.Project!.OwnerUserId == ownerUserId)
                .Select(r => new { r.Id, r.ProjectId })
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Repository not found or access denied.");
            if (projectId is not null && projectId != repository.ProjectId)
                throw new InvalidOperationException("Repository and project do not match.");
            projectId = repository.ProjectId;
        }
        else if (projectId is { } projectIdValue &&
                 !await _db.Projects.AnyAsync(p => p.Id == projectIdValue && p.OwnerUserId == ownerUserId, ct))
        {
            throw new InvalidOperationException("Project not found or access denied.");
        }

        var entry = new MemoryEntry { OwnerUserId = ownerUserId, Scope = scope, Title = title.Trim(), Content = content, Tags = tags,
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* Memory remains usable when the optional local embedding model is unavailable. */ }
    }

    private static string? Truncate(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] + "... [truncated]" : s;
}
