using System.Text.Json;
using LocalAgentPlatform.Modules.Memory.Domain;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Models;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Modules.Memory.Application.Services;

public sealed record RetrievedMemory(Guid Id, string Scope, string Title, string Content, double Score);

/// <summary>
/// Hybrid semantic retrieval. Stored Ollama embeddings are ranked by cosine similarity
/// and blended with the deterministic lexical/recency/importance score. If the local
/// embedding runtime/model is unavailable, retrieval degrades to lexical ranking rather
/// than failing the agent run.
/// </summary>
public sealed class MemoryRetrievalService
{
    private readonly PlatformDbContext _db;
    private readonly IEmbeddingProvider _embeddings;

    public MemoryRetrievalService(PlatformDbContext db, IEmbeddingProvider embeddings)
    {
        _db = db;
        _embeddings = embeddings;
    }

    public async Task<IReadOnlyList<RetrievedMemory>> RetrieveRelevantAsync(
        Guid? repositoryId, string query, int maxEntries = 5, int maxTotalChars = 2500, CancellationToken ct = default, Guid? ownerUserId = null)
    {
        var candidates = await _db.MemoryEntries
            .Where(m => (ownerUserId == null || m.OwnerUserId == ownerUserId) && (m.RepositoryId == repositoryId || m.RepositoryId == null))
            .ToListAsync(ct);
        if (candidates.Count == 0) return Array.Empty<RetrievedMemory>();

        var lexical = MemoryRelevanceRanker.Rank(query, candidates.Select(m => new ScorableMemory(
            m.Id, m.Title, m.Content, m.Tags, m.BaseImportance, m.CreatedAtUtc, m.LastAccessedAtUtc)).ToList(), DateTimeOffset.UtcNow)
            .ToDictionary(x => x.Id, x => x.Score);

        float[]? queryVector = null;
        try { queryVector = await _embeddings.EmbedAsync(query, ct: ct); } catch { /* local embedding model is optional at runtime */ }

        var ranked = candidates.Select(m =>
        {
            var lexicalScore = lexical.GetValueOrDefault(m.Id);
            var semantic = queryVector is null ? (double?)null : TryCosine(queryVector, ParseVector(m.EmbeddingJson));
            var blended = semantic is null ? lexicalScore : (semantic.Value * 0.70) + (lexicalScore * 0.30);
            return new { Entry = m, Score = blended };
        }).OrderByDescending(x => x.Score).ToList();

        var selected = new List<RetrievedMemory>();
        var usedChars = 0;
        foreach (var item in ranked)
        {
            if (selected.Count >= maxEntries) break;
            var entry = item.Entry;
            if (usedChars + entry.Content.Length > maxTotalChars && selected.Count > 0) continue;
            selected.Add(new RetrievedMemory(entry.Id, entry.Scope, entry.Title, entry.Content, item.Score));
            usedChars += entry.Content.Length;
            entry.AccessCount++;
            entry.LastAccessedAtUtc = DateTimeOffset.UtcNow;
        }
        if (selected.Count > 0) await _db.SaveChangesAsync(ct);
        return selected;
    }

    public async Task<int> BackfillEmbeddingsAsync(Guid? repositoryId = null, CancellationToken ct = default)
    {
        var entries = await _db.MemoryEntries
            .Where(m => (repositoryId == null || m.RepositoryId == repositoryId) && m.EmbeddingJson == null)
            .ToListAsync(ct);
        var updated = 0;
        foreach (var entry in entries)
        {
            try
            {
                var vector = await _embeddings.EmbedAsync($"{entry.Title}\n{entry.Content}", ct: ct);
                entry.EmbeddingJson = JsonSerializer.Serialize(vector);
                entry.EmbeddingModelId = _embeddings.DefaultEmbeddingModelId;
                updated++;
            }
            catch { break; }
        }
        if (updated > 0) await _db.SaveChangesAsync(ct);
        return updated;
    }

    public static string FormatForPrompt(IReadOnlyList<RetrievedMemory> memories)
    {
        if (memories.Count == 0) return "";
        return "Relevant context from previous work on this repository:\n" +
               string.Join('\n', memories.Select(m => $"- [{m.Scope}] {m.Title}: {m.Content}"));
    }

    private static float[]? ParseVector(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<float[]>(json); } catch { return null; }
    }

    private static double? TryCosine(float[] a, float[]? b)
    {
        if (b is null || a.Length == 0 || a.Length != b.Length) return null;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        if (aa == 0 || bb == 0) return null;
        return Math.Clamp((dot / Math.Sqrt(aa * bb) + 1.0) / 2.0, 0.0, 1.0);
    }
}
