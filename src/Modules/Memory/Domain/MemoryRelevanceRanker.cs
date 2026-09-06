using System.Text.RegularExpressions;

namespace LocalAgentPlatform.Modules.Memory.Domain;

public sealed record ScorableMemory(Guid Id, string Title, string Content, string? Tags, double BaseImportance, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastAccessedAtUtc);

public sealed record RankedMemory(Guid Id, double Score);

/// <summary>
/// Real, deterministic keyword-overlap relevance ranking — no vector embeddings, no
/// external service. This is an honest, documented substitute for semantic search
/// (spec Section 14 explicitly allows designing so "PostgreSQL-compatible vector
/// storage can be used" later; this is the pre-vector version, not a fake one). Pure
/// function, no I/O, fully unit-testable: same inputs always produce the same ranking.
/// </summary>
public static class MemoryRelevanceRanker
{
    private static readonly Regex WordSplitter = new(@"[^a-zA-Z0-9]+", RegexOptions.Compiled);

    public static IReadOnlyList<RankedMemory> Rank(string query, IReadOnlyList<ScorableMemory> candidates, DateTimeOffset nowUtc)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0) return Array.Empty<RankedMemory>();

        var ranked = new List<RankedMemory>();
        foreach (var m in candidates)
        {
            var contentTokens = Tokenize(m.Title + " " + m.Content + " " + (m.Tags ?? ""));
            var overlap = queryTokens.Intersect(contentTokens).Count();
            if (overlap == 0 && m.BaseImportance < 0.75) continue; // let high-importance pinned notes through even without overlap

            var overlapScore = queryTokens.Count == 0 ? 0 : (double)overlap / queryTokens.Count;

            var ageDays = (nowUtc - (m.LastAccessedAtUtc ?? m.CreatedAtUtc)).TotalDays;
            var recencyScore = 1.0 / (1.0 + Math.Max(0, ageDays) / 30.0); // decays over ~30 days, never hits zero

            // Weighted blend: keyword match matters most, then user-assigned importance, then recency.
            var score = (overlapScore * 0.6) + (m.BaseImportance * 0.3) + (recencyScore * 0.1);
            ranked.Add(new RankedMemory(m.Id, score));
        }

        return ranked.OrderByDescending(r => r.Score).ToList();
    }

    private static HashSet<string> Tokenize(string text) =>
        WordSplitter.Split(text.ToLowerInvariant())
            .Where(t => t.Length > 2) // drop very short/noise tokens
            .ToHashSet();
}
