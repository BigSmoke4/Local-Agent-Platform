using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;

namespace Platform.Web.Services.Memory;

public record MemoryResult(Models.Memory Memory, double RelevanceScore, string ScoringMethod);

/// <summary>
/// Real retrieval-based memory per §14. Two real scoring paths exist:
///
/// 1. Embedding similarity (cosine distance) — used when an embedding
///    model runtime id is configured AND the embedding call actually
///    succeeds for both the query and a given memory row.
/// 2. Keyword overlap — used as an honest fallback per-row when no
///    embedding is available (no embedding model configured, or the
///    embedding call failed for that memory when it was stored).
///
/// A memory table can have a mix of both — ScoringMethod on each result
/// says which one produced its score, so callers/tests can tell them apart
/// rather than the distinction being silently hidden.
/// </summary>
public class MemoryService
{
    private readonly PlatformDbContext _db;
    private readonly IModelProvider _modelProvider;
    private readonly string? _embeddingModelRuntimeId;

    public MemoryService(PlatformDbContext db, IModelProvider modelProvider, IConfiguration config)
    {
        _db = db;
        _modelProvider = modelProvider;
        var configured = config["ModelRuntime:EmbeddingModel"];
        _embeddingModelRuntimeId = string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    public async Task<Models.Memory> StoreAsync(MemoryType type, string content, string tags, Guid? sessionId, CancellationToken ct = default)
    {
        var memory = new Models.Memory
        {
            Type = type,
            Content = content,
            Tags = tags,
            AgentSessionId = sessionId
        };

        if (_embeddingModelRuntimeId is not null)
        {
            var embedding = await _modelProvider.GetEmbeddingAsync(_embeddingModelRuntimeId, content + " " + tags, ct);
            if (embedding is not null)
                memory.EmbeddingCsv = string.Join(',', embedding.Select(f => f.ToString("R")));
            // If embedding fails, EmbeddingCsv stays null — this row will use
            // keyword-overlap fallback at retrieval time. Not treated as an error.
        }

        _db.Memories.Add(memory);
        await _db.SaveChangesAsync(ct);
        return memory;
    }

    public async Task<List<MemoryResult>> RetrieveAsync(string query, int maxResults = 5, CancellationToken ct = default)
    {
        var candidates = await _db.Memories
            .OrderByDescending(m => m.CreatedAtUtc)
            .Take(500) // bounded candidate set — avoids loading the entire memory table (§44)
            .ToListAsync(ct);

        if (candidates.Count == 0) return new List<MemoryResult>();

        float[]? queryEmbedding = null;
        if (_embeddingModelRuntimeId is not null)
            queryEmbedding = await _modelProvider.GetEmbeddingAsync(_embeddingModelRuntimeId, query, ct);

        var queryTerms = Tokenize(query);
        var scored = new List<MemoryResult>();

        foreach (var memory in candidates)
        {
            if (queryEmbedding is not null && memory.EmbeddingCsv is not null)
            {
                var memoryEmbedding = ParseEmbedding(memory.EmbeddingCsv);
                var similarity = CosineSimilarity(queryEmbedding, memoryEmbedding);
                if (similarity > 0)
                    scored.Add(new MemoryResult(memory, similarity, "Embedding"));
            }
            else
            {
                var overlap = ScoreOverlap(queryTerms, Tokenize(memory.Content + " " + memory.Tags));
                if (overlap > 0)
                    scored.Add(new MemoryResult(memory, overlap, "KeywordOverlap"));
            }
        }

        var top = scored.OrderByDescending(r => r.RelevanceScore).Take(maxResults).ToList();

        foreach (var result in top)
        {
            result.Memory.TimesRetrieved++;
            result.Memory.LastRetrievedAtUtc = DateTime.UtcNow;
        }
        if (top.Count > 0) await _db.SaveChangesAsync(ct);

        return top;
    }

    private static float[] ParseEmbedding(string csv)
        => csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(float.Parse).ToArray();

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static double ScoreOverlap(HashSet<string> queryTerms, HashSet<string> candidateTerms)
    {
        if (candidateTerms.Count == 0 || queryTerms.Count == 0) return 0;
        var intersection = queryTerms.Intersect(candidateTerms).Count();
        return (double)intersection / queryTerms.Count;
    }

    private static HashSet<string> Tokenize(string text)
        => text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', ',', '.', ';', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .ToHashSet();
}
