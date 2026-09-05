using LocalAgentPlatform.Modules.Memory.Domain;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Modules.Memory.Application.Services;

public sealed record RetrievedMemory(Guid Id, string Scope, string Title, string Content, double Score);

/// <summary>
/// Retrieval-based memory access (spec Section 14: "Do not blindly inject all memories
/// into every prompt. Use retrieval-based memory."). Ranks candidate memories with the
/// real, deterministic <see cref="MemoryRelevanceRanker"/> and greedily fills a
/// character budget — a documented proxy for a token budget (see docs/STATUS.md) —
/// rather than dumping everything stored for a repository into the prompt.
/// </summary>
public sealed class MemoryRetrievalService
{
    private readonly PlatformDbContext _db;

    public MemoryRetrievalService(PlatformDbContext db) => _db = db;

    public async Task<IReadOnlyList<RetrievedMemory>> RetrieveRelevantAsync(
        Guid? repositoryId, string query, int maxEntries = 5, int maxTotalChars = 2500, CancellationToken ct = default)
    {
        var candidates = await _db.MemoryEntries
            .Where(m => m.RepositoryId == repositoryId || m.RepositoryId == null)
            .ToListAsync(ct);

        if (candidates.Count == 0) return Array.Empty<RetrievedMemory>();

        var scorable = candidates.Select(m => new ScorableMemory(
            m.Id, m.Title, m.Content, m.Tags, m.BaseImportance, m.CreatedAtUtc, m.LastAccessedAtUtc)).ToList();

        var ranked = MemoryRelevanceRanker.Rank(query, scorable, DateTimeOffset.UtcNow);
        var byId = candidates.ToDictionary(m => m.Id);

        var selected = new List<RetrievedMemory>();
        var usedChars = 0;
        foreach (var r in ranked)
        {
            if (selected.Count >= maxEntries) break;
            var entry = byId[r.Id];
            if (usedChars + entry.Content.Length > maxTotalChars && selected.Count > 0) continue;

            selected.Add(new RetrievedMemory(entry.Id, entry.Scope, entry.Title, entry.Content, r.Score));
            usedChars += entry.Content.Length;

            entry.AccessCount++;
            entry.LastAccessedAtUtc = DateTimeOffset.UtcNow;
        }

        if (selected.Count > 0) await _db.SaveChangesAsync(ct);
        return selected;
    }

    /// <summary>Formats retrieved memories for inclusion in a planning prompt — plain
    /// text, no markdown fences, so it composes cleanly with AgentPlanningService's
    /// existing system prompt.</summary>
    public static string FormatForPrompt(IReadOnlyList<RetrievedMemory> memories)
    {
        if (memories.Count == 0) return "";
        var lines = memories.Select(m => $"- [{m.Scope}] {m.Title}: {m.Content}");
        return "Relevant context from previous work on this repository:\n" + string.Join('\n', lines);
    }
}
